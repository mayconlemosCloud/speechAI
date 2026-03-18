using MeetingTranslator.Models;
using MeetingTranslator.Services.Common;
using Microsoft.CognitiveServices.Speech;
using Microsoft.CognitiveServices.Speech.Audio;
using Microsoft.CognitiveServices.Speech.Transcription;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Linq;

namespace MeetingTranslator.Services.Azure;

/// <summary>
/// Transcrição em tempo real com diarização via ConversationTranscriber (Azure Speech SDK).
/// Usa Azure Translator REST API para tradução pt-BR.
/// Suporta microfone (AudioConfig direto) e loopback (WasapiLoopbackCapture → PushAudioInputStream).
/// Auto-reconecta em caso de erros de sessão.
/// </summary>
public sealed class AzureTranscriptionService : IDisposable
{
    private readonly string _speechKey;
    private readonly string _speechRegion;
    private readonly AzureTranslatorClient _translator;

    // ── Mic ──
    private ConversationTranscriber? _micTranscriber;
    private AudioConfig? _micAudioConfig;

    // ── Loopback ──
    private ConversationTranscriber? _loopbackTranscriber;
    private AudioConfig? _loopbackAudioConfig;
    private WasapiLoopbackCapture? _loopbackCapture;
    private PushAudioInputStream? _loopbackPushStream;

    // ── Estado ──
    private CancellationTokenSource _cts = new();
    private bool _isConnected;
    private bool _isDisposed;

    // Parâmetros do último Start para poder reconectar
    private int _lastMicIndex;
    private int _lastLoopbackIndex;
    private bool _lastUseMic;
    private bool _lastUseLoopback;

    // Controle de reconnect por canal
    private int _micReconnectAttempts;
    private int _loopbackReconnectAttempts;
    private const int MaxReconnectAttempts = 5;

    // ── Eventos públicos ──
    public event EventHandler<TranscriptEventArgs>? TranscriptReceived;
    public event EventHandler<StatusEventArgs>? StatusChanged;
    public event EventHandler<StatusEventArgs>? ErrorOccurred;
    public event EventHandler<bool>? AnalyzingChanged;

    public bool IsConnected => _isConnected;

    public AzureTranscriptionService(string speechKey, string speechRegion)
    {
        _speechKey = speechKey;
        _speechRegion = speechRegion;
        _translator = new AzureTranslatorClient(speechKey, speechRegion);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Start / Stop
    // ─────────────────────────────────────────────────────────────────

    public async Task StartAsync(int micDeviceIndex, int loopbackDeviceIndex, bool useMic, bool useLoopback)
    {
        _cts = new CancellationTokenSource();
        _lastMicIndex = micDeviceIndex;
        _lastLoopbackIndex = loopbackDeviceIndex;
        _lastUseMic = useMic;
        _lastUseLoopback = useLoopback;
        _micReconnectAttempts = 0;
        _loopbackReconnectAttempts = 0;

        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Conectando Azure..." });

        try
        {
            if (useMic)
                await StartMicTranscriberAsync(micDeviceIndex).ConfigureAwait(false);

            if (useLoopback)
                await StartLoopbackTranscriberAsync(loopbackDeviceIndex).ConfigureAwait(false);

            _isConnected = true;
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — ouvindo..." });
        }
        catch (Exception ex)
        {
            Log($"Erro em StartAsync: {ex.Message}");
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"Erro ao iniciar: {ex.Message}" });
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();

        try { if (_micTranscriber != null) await _micTranscriber.StopTranscribingAsync().ConfigureAwait(false); } catch { }
        try { if (_loopbackTranscriber != null) await _loopbackTranscriber.StopTranscribingAsync().ConfigureAwait(false); } catch { }
        try { _loopbackCapture?.StopRecording(); } catch { }

        DisposeTranscribers();
        _isConnected = false;
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Desconectado" });
    }

    // ─────────────────────────────────────────────────────────────────
    //  Start: Microfone
    // ─────────────────────────────────────────────────────────────────

    private async Task StartMicTranscriberAsync(int micDeviceIndex)
    {
        var config = BuildSpeechConfig("pt-BR");
        _micAudioConfig = ResolveMicrophoneAudioConfig(micDeviceIndex);
        _micTranscriber = new ConversationTranscriber(config, _micAudioConfig);

        WireEvents(_micTranscriber, Speaker.You, "pt-BR", "en", "Mic", isMic: true);

        await _micTranscriber.StartTranscribingAsync().ConfigureAwait(false);
        Log("Mic: StartTranscribingAsync OK");
    }

    // ─────────────────────────────────────────────────────────────────
    //  Start: Loopback (áudio do sistema via WasapiLoopbackCapture)
    // ─────────────────────────────────────────────────────────────────

    private async Task StartLoopbackTranscriberAsync(int loopbackDeviceIndex)
    {
        // 1. Encontrar dispositivo de renderização selecionado
        var enumerator = new MMDeviceEnumerator();
        var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active).ToList();

        MMDevice? chosen = null;
        var uiDevices = AudioHelper.GetLoopbackDevices();
        var selected = uiDevices.FirstOrDefault(d => d.DeviceIndex == loopbackDeviceIndex);

        if (selected != null && !string.IsNullOrWhiteSpace(selected.Name))
        {
            chosen = renderDevices.FirstOrDefault(d => d.FriendlyName.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))
                  ?? renderDevices.FirstOrDefault(d => d.FriendlyName.Contains(selected.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (chosen == null && loopbackDeviceIndex >= 0 && loopbackDeviceIndex < renderDevices.Count)
            chosen = renderDevices[loopbackDeviceIndex];

        Log($"Loopback: dispositivo='{chosen?.FriendlyName ?? "padrão do sistema"}'");

        // 2. WasapiLoopbackCapture captura o áudio do dispositivo de renderização
        _loopbackCapture = chosen != null
            ? new WasapiLoopbackCapture(chosen)
            : new WasapiLoopbackCapture();

        // 3. PushAudioInputStream → Azure SDK espera 16kHz mono PCM16
        var pushFormat = AudioStreamFormat.GetWaveFormatPCM(16000, 16, 1);
        _loopbackPushStream = AudioInputStream.CreatePushStream(pushFormat);

        var targetWaveFormat = new WaveFormat(16000, 16, 1);

        _loopbackCapture.DataAvailable += (s, e) =>
        {
            if (e.BytesRecorded == 0 || _cts.IsCancellationRequested) return;
            try
            {
                var converted = AudioHelper.ConvertAudioFormat(
                    e.Buffer, e.BytesRecorded,
                    _loopbackCapture.WaveFormat, targetWaveFormat);

                if (converted.Length > 0)
                    _loopbackPushStream.Write(converted, converted.Length);
            }
            catch (Exception ex) { Log($"Loopback DataAvailable erro: {ex.Message}"); }
        };

        _loopbackCapture.RecordingStopped += (s, e) =>
        {
            Log($"Loopback: WasapiLoopbackCapture parou{(e.Exception != null ? $" com erro: {e.Exception.Message}" : "")}");
        };

        _loopbackCapture.StartRecording();
        Log("Loopback: WasapiLoopbackCapture.StartRecording() OK");

        // 4. Criar ConversationTranscriber com PushAudioInputStream
        var config = BuildSpeechConfig("en-US");
        _loopbackAudioConfig = AudioConfig.FromStreamInput(_loopbackPushStream);
        _loopbackTranscriber = new ConversationTranscriber(config, _loopbackAudioConfig);

        WireEvents(_loopbackTranscriber, Speaker.Them, "en-US", "pt-BR", "Loopback", isMic: false);

        await _loopbackTranscriber.StartTranscribingAsync().ConfigureAwait(false);
        Log("Loopback: StartTranscribingAsync OK");
    }

    // ─────────────────────────────────────────────────────────────────
    //  Config e wiring de eventos
    // ─────────────────────────────────────────────────────────────────

    private SpeechConfig BuildSpeechConfig(string recognitionLanguage)
    {
        var config = SpeechConfig.FromSubscription(_speechKey, _speechRegion);
        config.SpeechRecognitionLanguage = recognitionLanguage;
        config.SetProperty(PropertyId.Speech_SegmentationSilenceTimeoutMs, "500");
        config.SetProperty(PropertyId.SpeechServiceConnection_EndSilenceTimeoutMs, "2000");
        config.SetProperty(PropertyId.SpeechServiceResponse_StablePartialResultThreshold, "1");
        config.SetProperty(PropertyId.SpeechServiceResponse_DiarizeIntermediateResults, "true");
        return config;
    }

    private void WireEvents(ConversationTranscriber t, Speaker speaker, string fromLang, string toLang, string source, bool isMic)
    {
        t.Transcribing     += (s, e) => OnTranscribing(e, speaker);
        t.Transcribed      += (s, e) => _ = OnTranscribedAsync(e, speaker, fromLang, toLang);
        t.Canceled         += (s, e) => _ = OnCanceledAsync(e, source, isMic);
        t.SessionStarted   += (s, e) =>
        {
            Log($"{source}: sessão iniciada");
            if (isMic) _micReconnectAttempts = 0;
            else _loopbackReconnectAttempts = 0;
        };
        t.SessionStopped   += (s, e) => _ = OnSessionStoppedAsync(source, isMic);
    }

    // ─────────────────────────────────────────────────────────────────
    //  Handlers de transcrição
    // ─────────────────────────────────────────────────────────────────

    private void OnTranscribing(ConversationTranscriptionEventArgs e, Speaker speaker)
    {
        try
        {
            var text = e.Result.Text;
            if (string.IsNullOrWhiteSpace(text)) return;

            AnalyzingChanged?.Invoke(this, false);
            TranscriptReceived?.Invoke(this, new TranscriptEventArgs
            {
                Speaker        = speaker,
                OriginalText   = text,
                TranslatedText = text,        // parcial: exibe original, sem HTTP
                IsPartial      = true,
                SpeakerId      = NormalizeSpeakerId(e.Result.SpeakerId)
            });
        }
        catch (Exception ex) { Log($"Erro OnTranscribing: {ex.Message}"); }
    }

    private async Task OnTranscribedAsync(ConversationTranscriptionEventArgs e, Speaker speaker, string fromLang, string toLang)
    {
        try
        {
            if (e.Result.Reason == ResultReason.NoMatch)
            {
                AnalyzingChanged?.Invoke(this, false);
                return;
            }
            if (e.Result.Reason != ResultReason.RecognizedSpeech) return;

            var original = e.Result.Text;
            if (string.IsNullOrWhiteSpace(original)) return;

            // Tradução via REST
            var translated = await _translator.TranslateAsync(original, fromLang, toLang, _cts.Token)
                                              .ConfigureAwait(false);

            TranscriptReceived?.Invoke(this, new TranscriptEventArgs
            {
                Speaker        = speaker,
                OriginalText   = original,
                TranslatedText = translated,
                IsPartial      = false,
                SpeakerId      = NormalizeSpeakerId(e.Result.SpeakerId)
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log($"Erro OnTranscribedAsync: {ex.Message}"); }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Resiliência: cancel / session stopped → reconnect
    // ─────────────────────────────────────────────────────────────────

    private async Task OnCanceledAsync(ConversationTranscriptionCanceledEventArgs e, string source, bool isMic)
    {
        Log($"{source} Cancelado: {e.Reason}");

        if (e.Reason == CancellationReason.Error)
        {
            Log($"{source} Erro: {e.ErrorCode} — {e.ErrorDetails}");
            ErrorOccurred?.Invoke(this, new StatusEventArgs { Message = $"{source}: {e.ErrorCode}" });
            await TryReconnectAsync(source, isMic).ConfigureAwait(false);
        }
        // EndOfStream → áudio cessou, não reconectar (pode ser silêncio normal em loopback)
    }

    private async Task OnSessionStoppedAsync(string source, bool isMic)
    {
        if (_cts.IsCancellationRequested || _isDisposed) return;
        Log($"{source}: sessão parou inesperadamente");
        await TryReconnectAsync(source, isMic).ConfigureAwait(false);
    }

    private async Task TryReconnectAsync(string source, bool isMic)
    {
        if (_cts.IsCancellationRequested || _isDisposed) return;

        ref int attempts = ref isMic ? ref _micReconnectAttempts : ref _loopbackReconnectAttempts;

        if (attempts >= MaxReconnectAttempts)
        {
            Log($"[Reconnect] {source}: máximo de {MaxReconnectAttempts} tentativas atingido");
            StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"⚠ {source}: sem conexão após {MaxReconnectAttempts} tentativas" });
            return;
        }

        attempts++;
        int delaySec = (int)Math.Pow(2, attempts); // 2, 4, 8, 16, 32
        StatusChanged?.Invoke(this, new StatusEventArgs { Message = $"Reconectando {source}... ({attempts}/{MaxReconnectAttempts})" });
        Log($"[Reconnect] {source}: aguardando {delaySec}s (tentativa {attempts})");

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySec), _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        if (_cts.IsCancellationRequested || _isDisposed) return;

        try
        {
            if (isMic)
            {
                DisposeMicTranscriber();
                await StartMicTranscriberAsync(_lastMicIndex).ConfigureAwait(false);
            }
            else
            {
                DisposeLoopbackTranscriber();
                await StartLoopbackTranscriberAsync(_lastLoopbackIndex).ConfigureAwait(false);
            }

            StatusChanged?.Invoke(this, new StatusEventArgs { Message = "Pronto — ouvindo..." });
            Log($"[Reconnect] {source}: OK");
        }
        catch (Exception ex)
        {
            Log($"[Reconnect] {source}: falha — {ex.Message}");
            await TryReconnectAsync(source, isMic).ConfigureAwait(false);
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Resolução de microfone
    // ─────────────────────────────────────────────────────────────────

    private AudioConfig ResolveMicrophoneAudioConfig(int micDeviceIndex)
    {
        var uiDevices = AudioHelper.GetInputDevices();
        var selected = uiDevices.FirstOrDefault(d => d.DeviceIndex == micDeviceIndex);

        var enumerator = new MMDeviceEnumerator();
        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        MMDevice? chosen = null;
        if (selected != null && !string.IsNullOrWhiteSpace(selected.Name))
        {
            chosen = captureDevices.FirstOrDefault(d => d.FriendlyName.Equals(selected.Name, StringComparison.OrdinalIgnoreCase))
                  ?? captureDevices.FirstOrDefault(d => d.FriendlyName.Contains(selected.Name, StringComparison.OrdinalIgnoreCase));
        }

        if (chosen == null && micDeviceIndex >= 0 && micDeviceIndex < captureDevices.Count)
            chosen = captureDevices[micDeviceIndex];

        if (chosen == null)
        {
            Log("Mic: dispositivo padrão (fallback)");
            return AudioConfig.FromDefaultMicrophoneInput();
        }

        Log($"Mic: '{chosen.FriendlyName}' ID={chosen.ID}");
        try { return AudioConfig.FromMicrophoneInput(chosen.ID); }
        catch
        {
            try { return AudioConfig.FromMicrophoneInput(chosen.FriendlyName); }
            catch { return AudioConfig.FromDefaultMicrophoneInput(); }
        }
    }

    // ─────────────────────────────────────────────────────────────────
    //  Utilitários
    // ─────────────────────────────────────────────────────────────────

    private static string? NormalizeSpeakerId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
            return null;
        return id;
    }

    private static void Log(string msg)
        => System.Diagnostics.Debug.WriteLine($"[AzureTranscription] {msg}");

    // ─────────────────────────────────────────────────────────────────
    //  Dispose
    // ─────────────────────────────────────────────────────────────────

    private void DisposeMicTranscriber()
    {
        _micTranscriber?.Dispose();
        _micTranscriber = null;
        _micAudioConfig?.Dispose();
        _micAudioConfig = null;
    }

    private void DisposeLoopbackTranscriber()
    {
        try { _loopbackCapture?.StopRecording(); } catch { }
        _loopbackCapture?.Dispose();
        _loopbackCapture = null;

        _loopbackTranscriber?.Dispose();
        _loopbackTranscriber = null;

        _loopbackPushStream?.Dispose();
        _loopbackPushStream = null;

        _loopbackAudioConfig?.Dispose();
        _loopbackAudioConfig = null;
    }

    private void DisposeTranscribers()
    {
        DisposeMicTranscriber();
        DisposeLoopbackTranscriber();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        _cts.Cancel();
        DisposeTranscribers();
        _translator.Dispose();
        _cts.Dispose();
    }
}
