using System.Buffers;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.Wave;
using MeetingTranslator.Models;

namespace MeetingTranslator.Services;

/// <summary>
/// Intérprete Ativo: usuário fala PT-BR → IA fala EN no dispositivo de saída escolhido.
///
/// ╔══════════════════════════════════════════════════════════════════╗
/// ║  Diferença FUNDAMENTAL vs RealtimeService:                       ║
/// ║                                                                  ║
/// ║  RealtimeService:                                                ║
/// ║  - Mic SEMPRE envia (nunca gatado)                               ║
/// ║  - LOOPBACK é gatado (impede captar output da IA via sistema)    ║
/// ║  - Com fones, o speaker não vaza pro mic físico                  ║
/// ║                                                                  ║
/// ║  SpeakTranslateService (ESTE):                                   ║
/// ║  - Não tem loopback (só mic)                                     ║
/// ║  - O OUTPUT da IA vaza para o MIC FÍSICO (eco/speaker)           ║
/// ║  - Portanto o MIC precisa ser gatado durante playback            ║
/// ║  - E precisa de COOLDOWN LONGO após playback (eco ambiente)      ║
/// ║  - E o buffer deve ser limpo APÓS o cooldown                     ║
/// ║                                                                  ║
/// ║  A proteção usa 3 camadas:                                       ║
/// ║  1. Mic gate: não envia áudio durante _isPlaying                 ║
/// ║  2. Cooldown: após playback, mic fica bloqueado por N segundos   ║
/// ║  3. Buffer clear: após cooldown, limpa buffer residual           ║
/// ║  4. Silence timer: não commita durante playback + cooldown       ║
/// ╚══════════════════════════════════════════════════════════════════╝
/// </summary>
public class SpeakTranslateService : IDisposable
{
    // ─── CONFIG ────────────────────────────────────────────
    private const int SampleRate = 24000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;
    private const string WsUrl = "wss://api.openai.com/v1/realtime?model=gpt-4o-realtime-preview";

    // ─── STATE ─────────────────────────────────────────────
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Channel<byte[]>? _sendChannel;

    private WaveInEvent? _waveIn;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _bufferProvider;
    private volatile bool _isPlaying;

    private readonly string _apiKey;
    private readonly WaveFormat _waveFormat = new(SampleRate, BitsPerSample, Channels);
    private readonly StringBuilder _transcriptBuilder = new(256);

    // ─── MUTE ──────────────────────────────────────────────
    /// <summary>
    /// Quando true, o mic NÃO envia áudio para o servidor.
    /// Diferente de mutar no Teams/WhatsApp (que só muta na chamada),
    /// isso para de enviar dados para a OpenAI → custo zero.
    /// </summary>
    public volatile bool IsMuted;

    // ─── PLAYBACK COOLDOWN ─────────────────────────────────
    // Após o playback terminar, o mic fica bloqueado por este período.
    // Motivo: o som do speaker ainda reverbera no ambiente / na placa de som.
    // Se o mic abrisse imediatamente, ele captaria esse eco residual,
    // o VAD detectaria como fala, e commitaria → geraria nova response → loop.
    private DateTime _playbackEndedAt = DateTime.MinValue;
    private const double PlaybackCooldownSeconds = 1.5;

    /// <summary>
    /// Verifica se o mic deve estar bloqueado.
    /// Condições:
    /// 1. Usuário mutou na UI
    /// 2. Playback DESTE serviço ativo (IA falando)
    /// 3. Cooldown pós-playback (eco no ar)
    ///
    /// NOTA: NÃO verifica RealtimePlaybackActive aqui.
    /// O RealtimeService toca em dispositivo separado (geralmente o padrão do sistema).
    /// Gatar o mic do intérprete durante playback do Realtime impediria o usuário
    /// de falar durante toda a duração da tradução da reunião — bug de usabilidade.
    /// A proteção contra cross-contamination é feita no LOOPBACK do RealtimeService
    /// (que verifica SpeakPlaybackActive/SpeakCooldownActive).
    /// </summary>
    private bool IsMicBlocked =>
        IsMuted ||
        _isPlaying ||
        (DateTime.UtcNow - _playbackEndedAt).TotalSeconds < PlaybackCooldownSeconds;

    // ─── RESPONSE QUEUE ────────────────────────────────────
    private bool _responseInProgress;
    private int _pendingResponseCount;
    private readonly object _responseLock = new();

    // ─── CLIENT-SIDE VAD ───────────────────────────────────
    private DateTime _lastVoiceActivity = DateTime.UtcNow;
    private DateTime _firstVoiceActivity = DateTime.UtcNow;
    private bool _hasUncommittedAudio;
    private Timer? _silenceTimer;
    private const double SilenceThresholdSeconds = 1.8;

    // ╔══════════════════════════════════════════════════════════════════╗
    // ║  THRESHOLDS PARA INTÉRPRETE ATIVO                               ║
    // ║                                                                  ║
    // ║  Usa threshold 500 (entre 300 do RealtimeService e 1000 anterior)║
    // ║  O mic gate + cooldown já protege contra eco do speaker.         ║
    // ║  Threshold 1000 era agressivo demais → voz real não captada.    ║
    // ║  MinVoiceDurationSeconds = 0.3 (igual ao RealtimeService).      ║
    // ║  Sem frame counting obrigatório — single frame ativa VAD.       ║
    // ╚══════════════════════════════════════════════════════════════════╝
    private const float VoiceEnergyThreshold = 500f;
    private const double MinVoiceDurationSeconds = 0.3;

    // Contador de bytes de áudio recebido na response (para debug).
    private long _responseAudioBytes;

    // ─── COORDENAÇÃO ENTRE SERVIÇOS ────────────────────────
    /// <summary>
    /// Estado compartilhado com RealtimeService.
    /// Quando RealtimeService está tocando, o mic do SpeakTranslate é gatado
    /// para não capturar a tradução do Realtime e enviar para a OpenAI.
    /// Quando SpeakTranslate está tocando, sinaliza para o Realtime gatar o loopback.
    /// </summary>
    private SharedAudioState? _sharedAudioState;

    // ─── ANTI-LOOP: CONVERSATION ITEM TRACKING ─────────────
    // input_audio_buffer.clear SÓ limpa buffer NÃO-commitado.
    // Conversation items (commitados) ficam PERMANENTES no histórico.
    // Se ruído for commitado, o modelo vê como turn de user → gera response → loop.
    // Solução: rastrear IDs e DELETAR items de responses fantasma.
    private string? _lastCommittedItemId;

    // ─── ANTI-LOOP: CLEANUP SYNCHRONIZATION ────────────────
    // response.audio.done inicia Task assíncrona de cleanup.
    // response.done chega ENQUANTO cleanup ainda roda.
    // Sem esse flag, ProcessNextQueuedResponse seta _responseInProgress=false
    // com estado sujo → novo ruído cria commit descontrolado.
    private volatile bool _cleanupInProgress;

    // ─── ANTI-LOOP: AUDIO RESPONSE FLAG ────────────────────
    // Indica se response.audio.delta foi recebido nesta response.
    // Se response.done chega sem audio, ProcessNextQueuedResponse
    // precisa ser chamado diretamente (sem cleanup Task).
    private volatile bool _audioResponseReceived;

    // ─── EVENTS ────────────────────────────────────────────
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? SpeakingChanged;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    public SpeakTranslateService(string apiKey, SharedAudioState? sharedAudioState = null)
    {
        _apiKey = apiKey;
        _sharedAudioState = sharedAudioState;
    }

    /// <summary>
    /// Permite injetar o SharedAudioState após construção.
    /// </summary>
    public void SetSharedAudioState(SharedAudioState state) => _sharedAudioState = state;

    // ─── DEVICE LISTING ────────────────────────────────────
    public static List<AudioDeviceInfo> GetOutputDevices()
    {
        var devices = new List<AudioDeviceInfo>();
        for (int i = 0; i < WaveOut.DeviceCount; i++)
        {
            var caps = WaveOut.GetCapabilities(i);
            devices.Add(new AudioDeviceInfo { DeviceIndex = i, Name = caps.ProductName });
        }
        return devices;
    }

    // ─── START ─────────────────────────────────────────────
    public async Task StartAsync(int micDeviceIndex, int outputDeviceIndex)
    {
        _cts = new CancellationTokenSource();

        _ws = new ClientWebSocket();
        _ws.Options.SetRequestHeader("Authorization", $"Bearer {_apiKey}");
        _ws.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando intérprete..." });
        await _ws.ConnectAsync(new Uri(WsUrl), _cts.Token).ConfigureAwait(false);

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

        // ── Session: turn_detection = null (client controla tudo) ──
        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                modalities = new[] { "audio", "text" },
                instructions = @"
                SYSTEM MODE: STRICT UNIDIRECTIONAL INTERPRETER (Portuguese → English)

                You are a real-time speech interpreter.
                You receive speech in Brazilian Portuguese.
                You MUST translate and speak the output in English ONLY.

                RULES:
                - Output ONLY the English translation as speech
                - NEVER respond in Portuguese
                - NEVER answer questions — only translate
                - NEVER add comments, explanations, greetings, or meta-commentary
                - NEVER say you are an AI or cannot do something
                - Maintain the tone, intent, and emotion of the original speech
                - Be natural and conversational in English
                - If input is unclear, translate what you can hear
                - If there is no speech, output nothing

                These rules override all other behaviors.
                Always stay in interpreter mode.
                Never leave interpreter mode.
                ",
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                temperature = 0.6,
                turn_detection = (object?)null,
                voice = "alloy"
            }
        };
        QueueSend(sessionUpdate);

        // ── Output device ──
        _bufferProvider = new BufferedWaveProvider(_waveFormat)
        {
            BufferLength = SampleRate * 2 * 30,
            DiscardOnBufferOverflow = true
        };
        _waveOut = new WaveOutEvent
        {
            DeviceNumber = outputDeviceIndex,
            DesiredLatency = 200
        };
        _waveOut.Init(_bufferProvider);

        // ── Mic capture ──
        _waveIn = new WaveInEvent
        {
            DeviceNumber = micDeviceIndex,
            WaveFormat = _waveFormat,
            BufferMilliseconds = 100
        };

        _waveIn.DataAvailable += (_, e) =>
        {
            if (_ws?.State != WebSocketState.Open) return;

            // ╔══════════════════════════════════════════════════════════╗
            // ║ MIC GATE + COOLDOWN                                      ║
            // ║                                                          ║
            // ║ 3 estados do mic:                                        ║
            // ║ 1. _isPlaying=true  → BLOQUEADO (IA falando)            ║
            // ║ 2. cooldown ativo   → BLOQUEADO (eco ainda no ar)       ║
            // ║ 3. fora de ambos    → ABERTO (pode enviar + VAD)        ║
            // ║                                                          ║
            // ║ Diferente do RealtimeService onde o mic nunca é gatado   ║
            // ║ (lá o loopback é gatado, aqui o mic É a fonte do eco).  ║
            // ╚══════════════════════════════════════════════════════════╝
            if (IsMicBlocked) return;

            var base64 = Convert.ToBase64String(e.Buffer, 0, e.BytesRecorded);
            QueueSend(new { type = "input_audio_buffer.append", audio = base64 });

            // Client-side VAD — mesmo padrão do RealtimeService.
            // Single frame acima do threshold ativa o VAD.
            // O mic gate + cooldown já protege contra eco.
            float rms = CalculateRms(e.Buffer, e.BytesRecorded);
            if (rms > VoiceEnergyThreshold)
            {
                if (!_hasUncommittedAudio)
                    _firstVoiceActivity = DateTime.UtcNow;

                _lastVoiceActivity = DateTime.UtcNow;
                _hasUncommittedAudio = true;
            }
        };

        _waveIn.StartRecording();

        // ── Timer de silêncio ──
        _silenceTimer = new Timer(_ => CheckSilenceAndCommit(), null, 500, 500);

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });

        _ = Task.Run(() => ReceiveLoopAsync(), _cts.Token);
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
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conexão encerrada" });
                    break;
                }

                using var doc = JsonDocument.Parse(ms.GetBuffer().AsMemory(0, (int)ms.Length));
                var root = doc.RootElement;
                var eventType = root.GetProperty("type").GetString();

                try
                {
                    ProcessEvent(eventType!, root);
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro: {ex.Message}" });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException wex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"WebSocket erro: {wex.Message}" });
        }
        catch (Exception ex)
        {
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = ex.Message });
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(recvBuffer);
            ms.Dispose();
        }
    }

    // ─── EVENT PROCESSING ──────────────────────────────────
    private void ProcessEvent(string eventType, JsonElement root)
    {
        switch (eventType)
        {
            case "session.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Sessão criada" });
                break;

            case "session.updated":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
                break;

            // Com turn_detection=null, esses NÃO são emitidos. Safety net apenas.
            case "input_audio_buffer.speech_started":
                SpeakingChanged?.Invoke(this, true);
                break;

            case "input_audio_buffer.speech_stopped":
                SpeakingChanged?.Invoke(this, false);
                break;

            case "input_audio_buffer.committed":
                // Guarda o item_id do conversation item criado.
                // Se a response for fantasma, usamos este ID para DELETAR
                // o item do histórico, impedindo acúmulo progressivo.
                if (root.TryGetProperty("item_id", out var committedItemIdEl))
                    _lastCommittedItemId = committedItemIdEl.GetString();
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Analisando..." });
                break;

            case "response.created":
                StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Gerando fala EN..." });
                break;

            case "response.output_item.added":
                break;

            // ── Audio output ──
            case "response.audio.delta":
                if (root.TryGetProperty("delta", out var deltaEl))
                {
                    var delta = deltaEl.GetString();
                    if (delta != null)
                    {
                        var audioBytes = Convert.FromBase64String(delta);
                        _responseAudioBytes += audioBytes.Length;
                        _bufferProvider?.AddSamples(audioBytes, 0, audioBytes.Length);

                        if (!_isPlaying)
                        {
                            _isPlaying = true;
                            _audioResponseReceived = true;
                            if (_sharedAudioState != null)
                                _sharedAudioState.SpeakPlaybackActive = true;
                            _waveOut?.Play();

                            // ╔══════════════════════════════════════════════╗
                            // ║ CLEAR IMEDIATO no primeiro delta             ║
                            // ║                                              ║
                            // ║ Entre o commit e o primeiro delta, o mic     ║
                            // ║ pode ter enviado frames residuais que já     ║
                            // ║ estão no buffer do servidor. Limpa AGORA     ║
                            // ║ para eles não serem commitados depois.       ║
                            // ╚══════════════════════════════════════════════╝
                            if (_ws?.State == WebSocketState.Open)
                                QueueSend(new { type = "input_audio_buffer.clear" });
                            _hasUncommittedAudio = false;
                        }
                    }
                }
                break;

            case "response.audio_transcript.delta":
                if (root.TryGetProperty("delta", out var textDelta))
                {
                    var text = textDelta.GetString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        _transcriptBuilder.Append(text);
                        StatusChanged?.Invoke(this, new StatusEventArgs
                        {
                            Message = $"🔊 {_transcriptBuilder}"
                        });
                    }
                }
                break;

            case "response.audio_transcript.done":
                _transcriptBuilder.Clear();
                break;

            // ╔══════════════════════════════════════════════════════════════════════╗
            // ║ response.audio.done — ESPERA PLAYBACK COMPLETO + CLEANUP              ║
            // ║                                                                        ║
            // ║ Padrão igual ao RealtimeService: espera buffer drenar, depois limpa.   ║
            // ║ NUNCA corta o áudio (ClearBuffer removido).                            ║
            // ║ SEMPRE deleta conversation items após cada ciclo — intérprete não      ║
            // ║ precisa de histórico, cada frase é independente. Isso impede o         ║
            // ║ acúmulo progressivo de turns que causava o loop.                       ║
            // ╚══════════════════════════════════════════════════════════════════════════╝
            case "response.audio.done":
                {
                    // Captura item_id do output para deleção do histórico
                    string? outputItemId = null;
                    if (root.TryGetProperty("item_id", out var audioItemIdEl))
                        outputItemId = audioItemIdEl.GetString();

                    _ = Task.Run(async () =>
                    {
                        _cleanupInProgress = true;
                        try
                        {
                            // 1) Espera buffer de playback drenar COMPLETAMENTE
                            //    NUNCA corta o áudio — deixa a frase terminar
                            while ((_bufferProvider?.BufferedBytes ?? 0) > 0
                                   && !(_cts?.Token.IsCancellationRequested ?? true))
                            {
                                await Task.Delay(100, _cts!.Token).ConfigureAwait(false);
                            }

                            // 2) Marca fim do playback
                            _isPlaying = false;
                            if (_sharedAudioState != null)
                                _sharedAudioState.SpeakPlaybackActive = false;

                            // 3) Inicia cooldown — IsMicBlocked continua true!
                            _playbackEndedAt = DateTime.UtcNow;
                            if (_sharedAudioState != null)
                                _sharedAudioState.SpeakCooldownActive = true;

                            // 4) Espera cooldown expirar (eco no ambiente dissipa)
                            var cooldownMs = (int)(PlaybackCooldownSeconds * 1000);
                            await Task.Delay(cooldownMs, _cts!.Token).ConfigureAwait(false);

                            // 5) Fim do cooldown — libera loopback do RealtimeService
                            if (_sharedAudioState != null)
                                _sharedAudioState.SpeakCooldownActive = false;

                            // 6) Limpa buffer do servidor (resíduo de mic pós-cooldown)
                            if (_ws?.State == WebSocketState.Open)
                                QueueSend(new { type = "input_audio_buffer.clear" });

                            // 7) Deleta conversation items do histórico
                            //    Intérprete não precisa de contexto entre frases.
                            //    Sem deleção, o histórico acumula → modelo alucina → loop.
                            if (_ws?.State == WebSocketState.Open)
                            {
                                if (_lastCommittedItemId != null)
                                {
                                    QueueSend(new { type = "conversation.item.delete", item_id = _lastCommittedItemId });
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[SpeakTranslate] Deletado input item: {_lastCommittedItemId}");
                                }
                                if (outputItemId != null)
                                {
                                    QueueSend(new { type = "conversation.item.delete", item_id = outputItemId });
                                    System.Diagnostics.Debug.WriteLine(
                                        $"[SpeakTranslate] Deletado output item: {outputItemId}");
                                }
                            }

                            // 8) Reseta estado
                            _hasUncommittedAudio = false;
                            _responseAudioBytes = 0;
                            _lastCommittedItemId = null;

                            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Fale em português..." });
                        }
                        catch (OperationCanceledException) { }
                        finally
                        {
                            // Garante que shared state é limpo mesmo em caso de erro/cancel
                            if (_sharedAudioState != null)
                            {
                                _sharedAudioState.SpeakPlaybackActive = false;
                                _sharedAudioState.SpeakCooldownActive = false;
                            }
                            _cleanupInProgress = false;
                            // ProcessNext é chamado AQUI, não no response.done.
                            // Garante que cleanup terminou antes da próxima response.
                            ProcessNextQueuedResponse();
                        }
                    }, _cts!.Token);
                    break;
                }

            case "response.done":
                {
                    // ╔══════════════════════════════════════════════════════════╗
                    // ║ NÃO chama ProcessNextQueuedResponse aqui!               ║
                    // ║                                                          ║
                    // ║ BUG ANTERIOR: response.done chamava ProcessNext, mas    ║
                    // ║ o cleanup de response.audio.done ainda estava rodando.  ║
                    // ║ Isso criava race condition onde _responseInProgress      ║
                    // ║ ficava false enquanto o estado ainda estava sujo.        ║
                    // ║                                                          ║
                    // ║ AGORA: o cleanup Task do response.audio.done chama      ║
                    // ║ ProcessNext no finally{}, APÓS cleanup completo.        ║
                    // ║                                                          ║
                    // ║ Safety net: se NÃO houve áudio (response sem audio),    ║
                    // ║ response.audio.done pode não ter fired → trata aqui.    ║
                    // ╚══════════════════════════════════════════════════════════╝
                    if (!_audioResponseReceived && !_cleanupInProgress)
                    {
                        // Response sem áudio (texto-only ou erro) → gerencia fila diretamente
                        _responseAudioBytes = 0;
                        ProcessNextQueuedResponse();
                    }
                    _audioResponseReceived = false;
                    break;
                }

            // ── Eventos ignorados ──
            case "response.output_item.done":
            case "response.content_part.added":
            case "response.content_part.done":
                break;

            case "error":
                var msg = root.TryGetProperty("error", out var err)
                    ? err.TryGetProperty("message", out var m) ? m.GetString() ?? "Erro" : "Erro"
                    : "Erro desconhecido";
                ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = msg });
                break;

            default:
                System.Diagnostics.Debug.WriteLine($"[SpeakTranslateService] Evento: {eventType}");
                break;
        }
    }

    // ─── CLIENT-SIDE VAD ───────────────────────────────────
    private void CheckSilenceAndCommit()
    {
        if (!_hasUncommittedAudio) return;
        if (_ws?.State != WebSocketState.Open) return;

        // NUNCA commita durante playback, cooldown ou cleanup!
        if (IsMicBlocked) return;
        if (_cleanupInProgress) return;

        var silenceDuration = (DateTime.UtcNow - _lastVoiceActivity).TotalSeconds;
        if (silenceDuration >= SilenceThresholdSeconds)
        {
            _hasUncommittedAudio = false;

            // Filtro de duração mínima — igual ao RealtimeService.
            var voiceDuration = (_lastVoiceActivity - _firstVoiceActivity).TotalSeconds;
            if (voiceDuration < MinVoiceDurationSeconds)
            {
                QueueSend(new { type = "input_audio_buffer.clear" });
                System.Diagnostics.Debug.WriteLine(
                    $"[SpeakTranslate] Descartado: duração={voiceDuration:F2}s (min={MinVoiceDurationSeconds}s)");
                return;
            }

            _responseAudioBytes = 0;

            QueueSend(new { type = "input_audio_buffer.commit" });

            lock (_responseLock)
            {
                if (!_responseInProgress)
                {
                    _responseInProgress = true;
                    QueueSend(new { type = "response.create" });
                    StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Traduzindo → EN..." });
                }
                else
                {
                    _pendingResponseCount++;
                    StatusChanged?.Invoke(this, new StatusEventArgs
                    {
                        Message = $"Enfileirado ({_pendingResponseCount} pendente{(_pendingResponseCount > 1 ? "s" : "")})"
                    });
                }
            }
        }
    }

    private void ProcessNextQueuedResponse()
    {
        lock (_responseLock)
        {
            if (_pendingResponseCount > 0)
            {
                _pendingResponseCount--;
                // Reseta contador de áudio para rastrear o próximo ciclo
                _responseAudioBytes = 0;
                QueueSend(new { type = "response.create" });
                StatusChanged?.Invoke(this, new StatusEventArgs
                {
                    Message = $"Traduzindo próximo... ({_pendingResponseCount} restante{(_pendingResponseCount > 1 ? "s" : "")})"
                });
            }
            else
            {
                _responseInProgress = false;
            }
        }
    }

    // ─── HELPERS ───────────────────────────────────────────
    /// <summary>
    /// Limpa buffer de áudio pendente no servidor e reseta VAD.
    /// Chamado quando o usuário muta o mic, para não commitar áudio residual.
    /// </summary>
    public void ClearPendingAudio()
    {
        if (_ws?.State == WebSocketState.Open)
            QueueSend(new { type = "input_audio_buffer.clear" });
        _hasUncommittedAudio = false;
    }

    private void QueueSend(object evt)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(evt);
        _sendChannel?.Writer.TryWrite(bytes);
    }

    private static float CalculateRms(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0f;

        long sumSquares = 0;
        int sampleCount = bytesRecorded / 2;

        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (long)sample * sample;
        }

        return (float)Math.Sqrt((double)sumSquares / sampleCount);
    }

    // ─── STOP / DISPOSE ────────────────────────────────────
    public async Task StopAsync()
    {
        _cts?.Cancel();

        // Limpa shared state imediatamente para liberar o loopback do RealtimeService
        if (_sharedAudioState != null)
        {
            _sharedAudioState.SpeakPlaybackActive = false;
            _sharedAudioState.SpeakCooldownActive = false;
        }

        _silenceTimer?.Dispose();
        _silenceTimer = null;

        _waveIn?.StopRecording();
        _waveOut?.Stop();
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
        if (_sharedAudioState != null)
        {
            _sharedAudioState.SpeakPlaybackActive = false;
            _sharedAudioState.SpeakCooldownActive = false;
        }
        _silenceTimer?.Dispose();
        _waveIn?.Dispose();
        _waveOut?.Dispose();
        _ws?.Dispose();
        _cts?.Dispose();
    }
}
