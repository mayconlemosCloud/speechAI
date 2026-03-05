using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using MeetingTranslator.Models;

namespace MeetingTranslator.Services;

/// <summary>
/// Engine de transcrição e tradução em tempo real via OpenAI Realtime API.
/// Usa gpt-4o-mini-realtime-preview com modalities=["text"]:
/// - Server-side VAD detecta turnos de fala
/// - O modelo transcreve E traduz para PT-BR num único passo via instructions
/// - Sem chamada extra à Chat API
/// - Sem saída de áudio (text-only)
/// </summary>
public class TranscriptionService : IDisposable
{
    // ─── CONFIG ────────────────────────────────────────────
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    private const string WsUrl =
        "wss://api.openai.com/v1/realtime?model=gpt-realtime-mini";

    // ─── STATE ─────────────────────────────────────────────
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;

    private WaveInEvent? _waveIn;
    private WasapiLoopbackCapture? _loopbackCapture;

    private readonly string _apiKey;
    private readonly WaveFormat _waveFormat = new(SampleRate, BitsPerSample, Channels);

    // ─── RESPONSE STATE ────────────────────────────────────
    // Acumula texto de resposta do modelo (tradução) por resposta
    private readonly StringBuilder _currentResponseText = new(512);
    // Acumula deltas de transcrição do áudio de entrada (original, tempo real)
    private readonly Dictionary<string, StringBuilder> _inputTranscriptBuffers = new();

    // ─── EVENTS ────────────────────────────────────────────
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public TranscriptionService(string apiKey)
    {
        _apiKey = apiKey;
    }

    // ─── START ─────────────────────────────────────────────
    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando..." });
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token).ConfigureAwait(false);
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectado!" });

        // ── Send channel (thread-safe, bounded) ──
        _sendChannel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(200)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _ = Task.Run(async () =>
        {
            await foreach (var msg in _sendChannel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                try
                {
                    if (_ws.State == WebSocketState.Open)
                        await _ws.SendAsync(msg, WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (WebSocketException) { break; }
                catch { }
            }
        }, _cts.Token);

        // ── Session update (realtime model, text-only) ──
        // modalities=["text"] → sem saída de áudio, só texto
        // instructions → o modelo transcreve e traduz para PT-BR diretamente
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "text" },
                instructions = @"You are a live transcription and translation engine. " +
                               @"Listen to the audio, transcribe it, and output ONLY the translated text in Brazilian Portuguese. " +
                               @"Do not add any explanation, prefix, or formatting. Output only the translated content.",
                input_audio_format = "pcm16",
                input_audio_transcription = new
                {
                    model = "gpt-4o-transcribe"
                },
                turn_detection = new
                {
                    type = "server_vad",
                    threshold = 0.5,
                    prefix_padding_ms = 300,
                    silence_duration_ms = 500
                }
            }
        };
        QueueSend(sessionUpdate);

        // ── Mic capture ──
        if (useMic)
        {
            _waveIn = new WaveInEvent
            {
                DeviceNumber = micDeviceIndex,
                WaveFormat = _waveFormat,
                BufferMilliseconds = 100
            };

            _waveIn.DataAvailable += (_, e) =>
            {
                if (_ws.State != WebSocketState.Open) return;
                var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
                QueueSend(new { type = "input_audio_buffer.append", audio = base64 });
            };

            _waveIn.StartRecording();
        }

        // ── Loopback capture ──
        if (useLoopback)
        {
            StartLoopbackCapture(loopbackDeviceIndex);
        }

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });

        // ── Receive loop ──
        _ = Task.Run(() => ReceiveLoopAsync(), _cts.Token);
    }

    // ─── LOOPBACK ──────────────────────────────────────────
    private void StartLoopbackCapture(int deviceIndex)
    {
        var enumerator = new MMDeviceEnumerator();
        var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();
        if (deviceIndex < 0 || deviceIndex >= devices.Count) return;

        var device = devices[deviceIndex];
        _loopbackCapture = new WasapiLoopbackCapture(device);
        var loopbackFormat = _loopbackCapture.WaveFormat;

        _loopbackCapture.DataAvailable += (_, e) =>
        {
            if (_ws?.State != WebSocketState.Open) return;
            if (e.BytesRecorded == 0) return;

            byte[] converted = ConvertAudioFormat(e.Buffer, e.BytesRecorded, loopbackFormat, _waveFormat);
            if (converted.Length == 0) return;

            var base64 = Convert.ToBase64String(converted);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });
        };

        _loopbackCapture.StartRecording();
    }

    [ThreadStatic] private static byte[]? _resampleBuffer;
    [ThreadStatic] private static MemoryStream? _resampleMs;

    private static byte[] ConvertAudioFormat(byte[] sourceBuffer, int bytesRecorded, WaveFormat sourceFormat, WaveFormat targetFormat)
    {
        using var sourceStream = new RawSourceWaveStream(sourceBuffer, 0, bytesRecorded, sourceFormat);
        using var resampler = new MediaFoundationResampler(sourceStream, targetFormat);
        resampler.ResamplerQuality = 60;

        _resampleBuffer ??= new byte[4096];
        var ms = _resampleMs ??= new MemoryStream(16384);
        ms.SetLength(0);

        int read;
        while ((read = resampler.Read(_resampleBuffer, 0, _resampleBuffer.Length)) > 0)
        {
            ms.Write(_resampleBuffer, 0, read);
        }
        return ms.ToArray();
    }

    // ─── RECEIVE LOOP ──────────────────────────────────────
    private async Task ReceiveLoopAsync()
    {
        var recvBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var ms = new MemoryStream(64 * 1024);

        try
        {
            while (_ws?.State == WebSocketState.Open && !_cts!.Token.IsCancellationRequested)
            {
                ms.SetLength(0);

                ValueWebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(recvBuffer.AsMemory(), _cts.Token).ConfigureAwait(false);
                    ms.Write(recvBuffer, 0, result.Count);
                } while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    var reason = _ws.CloseStatusDescription ?? _ws.CloseStatus?.ToString() ?? "desconhecido";
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"WebSocket fechado: {reason}" });
                    break;
                }

                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                var root = doc.RootElement;
                var eventType = root.GetProperty("type").GetString();

                try
                {
                    await ProcessEventAsync(eventType!, root);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro '{eventType}': {ex.Message}" });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException wex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"WebSocket erro: {wex.Message}" });
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conexão perdida" });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = ex.Message });
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Erro na conexão" });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(recvBuffer);
            ms.Dispose();
        }
    }

    // ─── EVENT PROCESSING ──────────────────────────────────
    private Task ProcessEventAsync(string eventType, JsonElement root)
    {
        switch (eventType)
        {
            // ── SESSION LIFECYCLE ──
            case "session.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessão criada" });
                break;

            case "session.updated":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — ouvindo..." });
                break;

            // ── VAD EVENTS ──
            case "input_audio_buffer.speech_started":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo fala..." });
                break;

            case "input_audio_buffer.speech_stopped":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Processando..." });
                AnalyzingChanged?.Invoke(this, true);
                break;

            case "input_audio_buffer.committed":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Transcrevendo..." });
                break;

            case "input_audio_buffer.cleared":
                break;

            // ── INPUT AUDIO TRANSCRIPTION (tempo real, original) ──
            // Aparece ENQUANTO a pessoa fala — transcreve o áudio de entrada em paralelo
            case "conversation.item.input_audio_transcription.delta":
                {
                    var itemId = root.TryGetProperty("item_id", out var id) ? id.GetString() ?? "" : "";
                    var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(delta)) break;

                    if (!_inputTranscriptBuffers.TryGetValue(itemId, out var sb))
                    {
                        sb = new StringBuilder(256);
                        _inputTranscriptBuffers[itemId] = sb;
                    }
                    sb.Append(delta);

                    AnalyzingChanged?.Invoke(this, false);
                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                    {
                        Speaker = Speaker.Them,
                        OriginalText = sb.ToString(),
                        TranslatedText = sb.ToString(),
                        IsPartial = true
                    });
                    break;
                }

            case "conversation.item.input_audio_transcription.completed":
                {
                    var itemId = root.TryGetProperty("item_id", out var id) ? id.GetString() ?? "" : "";
                    _inputTranscriptBuffers.Remove(itemId);
                    break;
                }

            // ── RESPONSE LIFECYCLE ──
            case "response.created":
                _currentResponseText.Clear();
                break;

            // ── STREAMING TEXT DELTAS ──
            // O modelo retorna texto traduzido incrementalmente
            case "response.text.delta":
                {
                    var delta = root.TryGetProperty("delta", out var d) ? d.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(delta)) break;

                    _currentResponseText.Append(delta);
                    AnalyzingChanged?.Invoke(this, false);

                    TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                    {
                        Speaker = Speaker.Them,
                        OriginalText = "",
                        TranslatedText = _currentResponseText.ToString(),
                        IsPartial = true
                    });
                    break;
                }

            // ── TEXT COMPLETE ──
            case "response.text.done":
                {
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(text)) text = _currentResponseText.ToString();

                    AnalyzingChanged?.Invoke(this, false);

                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        TranscriptReceived?.Invoke(this, new TranscriptEventArgs
                        {
                            Speaker = Speaker.Them,
                            OriginalText = "",
                            TranslatedText = text,
                            IsPartial = false
                        });
                    }

                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Ouvindo..." });
                    break;
                }

            case "response.done":
                AnalyzingChanged?.Invoke(this, false);
                break;

            // ── IGNORED ──
            case "response.output_item.added":
            case "response.output_item.done":
            case "response.content_part.added":
            case "response.content_part.done":
            case "conversation.item.created":
            case "rate_limits.updated":
                break;

            // ── ERROR ──
            case "error":
                {
                    var msg = root.TryGetProperty("error", out var err)
                        ? err.TryGetProperty("message", out var m) ? m.GetString() ?? "Erro" : "Erro"
                        : "Erro desconhecido";
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
                    break;
                }

            // ── UNKNOWN ──
            default:
                System.Diagnostics.Debug.WriteLine($"[TranscriptionService] Evento não tratado: {eventType}");
                break;
        }

        return Task.CompletedTask;
    }

    // ─── HELPERS ───────────────────────────────────────────
    private void QueueSend(object evt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt);
        _sendChannel?.Writer.TryWrite(bytes);
    }

    // ─── STOP / DISPOSE ────────────────────────────────────
    public async Task StopAsync()
    {
        _cts?.Cancel();

        _waveIn?.StopRecording();
        _loopbackCapture?.StopRecording();
        _sendChannel?.Writer.Complete();

        if (_ws?.State == WebSocketState.Open)
        {
            try
            {
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
            }
            catch { }
        }

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _waveIn?.Dispose();
        _loopbackCapture?.Dispose();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
