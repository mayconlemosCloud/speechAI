using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using MeetingTranslator.Models;
using MeetingTranslator.Services;
using dotenv.net;

namespace MeetingTranslator.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private RealtimeService? _voiceService;
    private TranscriptionService? _transcriptionService;
    private SpeakTranslateService? _speakService;
    private readonly Dispatcher _dispatcher;

    // ─── COORDENAÇÃO ENTRE SERVIÇOS ────────────────────────
    /// <summary>
    /// Estado compartilhado entre RealtimeService e SpeakTranslateService.
    /// Previne cross-contamination de áudio entre os dois serviços.
    /// </summary>
    private readonly SharedAudioState _sharedAudioState = new();

    // ─── BINDABLE PROPERTIES ───────────────────────────────
    private string _subtitleText = "";
    public string SubtitleText
    {
        get => _subtitleText;
        set { _subtitleText = value; OnPropertyChanged(); }
    }

    private string _statusText = "Desconectado";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private bool _isConnected;
    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(ConnectButtonText)); }
    }

    public string ConnectButtonText => IsConnected ? "Desconectar" : "Conectar";

    private bool _isHistoryVisible;
    public bool IsHistoryVisible
    {
        get => _isHistoryVisible;
        set { _isHistoryVisible = value; OnPropertyChanged(); }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set { _isMuted = value; OnPropertyChanged(); OnPropertyChanged(nameof(MuteIcon)); }
    }
    public string MuteIcon => IsMuted ? "🔇" : "🎤";

    // Guarda o estado original do mute do mic antes do app alterar,
    // para restaurar quando o app fechar ou desmutar.
    private bool _wasMicMutedBefore;
    private bool _micMuteManaged; // true quando o app gerencia o mute do mic

    private bool _useMic = true;
    public bool UseMic
    {
        get => _useMic;
        set { _useMic = value; OnPropertyChanged(); }
    }

    private bool _useLoopback = true;
    public bool UseLoopback
    {
        get => _useLoopback;
        set { _useLoopback = value; OnPropertyChanged(); }
    }

    private ApiMode _selectedMode = ApiMode.Transcription;
    public ApiMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            _selectedMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsVoiceMode));
            OnPropertyChanged(nameof(IsTranscriptionMode));
            OnPropertyChanged(nameof(ModeDescription));
        }
    }

    public bool IsVoiceMode
    {
        get => _selectedMode == ApiMode.Voice;
        set { if (value) SelectedMode = ApiMode.Voice; }
    }

    public bool IsTranscriptionMode
    {
        get => _selectedMode == ApiMode.Transcription;
        set { if (value) SelectedMode = ApiMode.Transcription; }
    }

    public string ModeDescription => _selectedMode switch
    {
        ApiMode.Voice => "IA ouve, traduz e fala. Resultado após silêncio.",
        ApiMode.Transcription => "Texto em tempo real enquanto fala. Tradução após cada frase.",
        _ => ""
    };

    private AudioDeviceInfo? _selectedMicDevice;
    public AudioDeviceInfo? SelectedMicDevice
    {
        get => _selectedMicDevice;
        set { _selectedMicDevice = value; OnPropertyChanged(); }
    }

    private AudioDeviceInfo? _selectedLoopbackDevice;
    public AudioDeviceInfo? SelectedLoopbackDevice
    {
        get => _selectedLoopbackDevice;
        set
        {
            _selectedLoopbackDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakDeviceWarning));
            OnPropertyChanged(nameof(HasSpeakDeviceWarning));
        }
    }

    private bool _isSettingsVisible;
    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        set { _isSettingsVisible = value; OnPropertyChanged(); }
    }

    private bool _isAssistantTyping;
    public bool IsAssistantTyping
    {
        get => _isAssistantTyping;
        set { _isAssistantTyping = value; OnPropertyChanged(); }
    }

    private bool _isAnalyzing;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set { _isAnalyzing = value; OnPropertyChanged(); }
    }

    private string _inputText = "";
    public string InputText
    {
        get => _inputText;
        set
        {
            _inputText = value;
            OnPropertyChanged();
            if (SendMessageCommand is DelegateCommand cmd)
            {
                cmd.RaiseCanExecuteChanged();
            }
        }
    }

    // ─── INTÉRPRETE ATIVO ─────────────────────────────────
    private bool _isSpeakFeatureEnabled;
    public bool IsSpeakFeatureEnabled
    {
        get => _isSpeakFeatureEnabled;
        set { _isSpeakFeatureEnabled = value; OnPropertyChanged(); }
    }

    private bool _isSpeakConnected;
    public bool IsSpeakConnected
    {
        get => _isSpeakConnected;
        set { _isSpeakConnected = value; OnPropertyChanged(); OnPropertyChanged(nameof(SpeakButtonTooltip)); }
    }

    private string _speakStatusText = "";
    public string SpeakStatusText
    {
        get => _speakStatusText;
        set { _speakStatusText = value; OnPropertyChanged(); }
    }

    public string SpeakButtonTooltip => IsSpeakConnected ? "Parar Intérprete PT→EN" : "Iniciar Intérprete PT→EN";

    private AudioDeviceInfo? _selectedSpeakMicDevice;
    public AudioDeviceInfo? SelectedSpeakMicDevice
    {
        get => _selectedSpeakMicDevice;
        set { _selectedSpeakMicDevice = value; OnPropertyChanged(); }
    }

    private AudioDeviceInfo? _selectedSpeakOutputDevice;
    public AudioDeviceInfo? SelectedSpeakOutputDevice
    {
        get => _selectedSpeakOutputDevice;
        set
        {
            _selectedSpeakOutputDevice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakDeviceWarning));
            OnPropertyChanged(nameof(HasSpeakDeviceWarning));
        }
    }

    /// <summary>
    /// Aviso quando o dispositivo de saída do intérprete é o mesmo que o loopback.
    /// Isso causa cross-contamination: o loopback captura o áudio EN do intérprete
    /// e o RealtimeService traduz de volta para PT, criando eco.
    /// </summary>
    public string SpeakDeviceWarning
    {
        get
        {
            if (_selectedSpeakOutputDevice == null || _selectedLoopbackDevice == null)
                return "";

            // Compara nomes (os DeviceIndex podem diferir entre WaveOut e WASAPI Render)
            if (_selectedSpeakOutputDevice.Name == _selectedLoopbackDevice.Name)
                return "⚠ Atenção: a saída do intérprete é o mesmo dispositivo que o loopback do tradutor. " +
                       "Isso pode causar eco da sua própria voz. " +
                       "Use VB-Cable ou outro dispositivo virtual como saída do intérprete.";

            return "";
        }
    }

    public bool HasSpeakDeviceWarning => !string.IsNullOrEmpty(SpeakDeviceWarning);

    // ─── COLLECTIONS ──────────────────────────────────────
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> SpeakOutputDevices { get; } = new();
    public ObservableCollection<ConversationEntry> History { get; } = new();

    // partial transcript accumulator (texto completo atual da fala)
    private string _partialTranscript = "";

    // ─── THROTTLE PARA PARTIAL TRANSCRIPTS ─────────────────
    // Evita inundar o UI thread com atualizações a cada token
    private volatile string? _pendingPartialText;
    private volatile bool _partialUpdateScheduled;

    // ─── BATCHED LOG WRITER ────────────────────────────────
    // Um único background writer para todos os logs — evita File.Open/Close por linha
    private static readonly string _logBasePath = AppDomain.CurrentDomain.BaseDirectory;
    private readonly Channel<(string FileName, string Line)> _logChannel =
        Channel.CreateBounded<(string, string)>(new BoundedChannelOptions(1000)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropOldest
        });
    private Task? _logWriterTask;
    private readonly CancellationTokenSource _logCts = new();

    public ICommand SendMessageCommand { get; }

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        LoadDevices();
        SendMessageCommand = new DelegateCommand(_ => SendMessage(), _ => !string.IsNullOrWhiteSpace(InputText));
        _logWriterTask = Task.Run(RunLogWriter);
    }

    private void LoadDevices()
    {
        MicDevices.Clear();
        foreach (var d in RealtimeService.GetInputDevices())
            MicDevices.Add(d);

        LoopbackDevices.Clear();
        foreach (var d in RealtimeService.GetLoopbackDevices())
            LoopbackDevices.Add(d);

        SpeakOutputDevices.Clear();
        foreach (var d in SpeakTranslateService.GetOutputDevices())
            SpeakOutputDevices.Add(d);

        if (MicDevices.Count > 0) SelectedMicDevice = MicDevices[0];
        if (LoopbackDevices.Count > 0) SelectedLoopbackDevice = LoopbackDevices[0];
        if (MicDevices.Count > 0 && SelectedSpeakMicDevice == null) SelectedSpeakMicDevice = MicDevices[0];
        if (SpeakOutputDevices.Count > 0) SelectedSpeakOutputDevice = SpeakOutputDevices[0];
    }

    // ─── COMMANDS ──────────────────────────────────────────
    public async Task ToggleConnectionAsync()
    {
        if (IsConnected)
        {
            await DisconnectAsync();
        }
        else
        {
            await ConnectAsync();
        }
    }

    private async Task ConnectAsync()
    {
        // Procura .env na pasta do executável e na pasta de trabalho atual
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        DotEnv.Load(new DotEnvOptions(envFilePaths: new[] {
            Path.Combine(exeDir, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            ".env"
        }, ignoreExceptions: true));

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            StatusText = "⚠ OPENAI_API_KEY não encontrada";
            return;
        }

        try
        {
            if (SelectedMode == ApiMode.Voice)
            {
                await ConnectVoiceModeAsync(apiKey);
            }
            else
            {
                await ConnectTranscriptionModeAsync(apiKey);
            }

            IsConnected = true;
            StatusText = "Pronto — ouvindo...";
        }
        catch (Exception ex)
        {
            StatusText = $"⚠ Erro: {ex.Message}";
        }
    }

    private async Task ConnectVoiceModeAsync(string apiKey)
    {
        _voiceService = new RealtimeService(apiKey, _sharedAudioState);

        _voiceService.TranscriptReceived += OnTranscriptReceived;
        _voiceService.StatusChanged += OnStatusChanged;
        _voiceService.ErrorOccurred += OnError;
        _voiceService.AnalyzingChanged += OnAnalyzingChanged;

        await _voiceService.StartAsync(
            SelectedMicDevice?.DeviceIndex ?? 0,
            SelectedLoopbackDevice?.DeviceIndex ?? 0,
            UseMic,
            UseLoopback
        );
    }

    private async Task ConnectTranscriptionModeAsync(string apiKey)
    {
        _transcriptionService = new TranscriptionService(apiKey);

        _transcriptionService.TranscriptReceived += OnTranscriptReceived;
        _transcriptionService.StatusChanged += OnStatusChanged;
        _transcriptionService.ErrorOccurred += OnError;
        _transcriptionService.AnalyzingChanged += OnAnalyzingChanged;

        await _transcriptionService.StartAsync(
            SelectedMicDevice?.DeviceIndex ?? 0,
            SelectedLoopbackDevice?.DeviceIndex ?? 0,
            UseMic,
            UseLoopback
        );
    }

    private async Task DisconnectAsync()
    {
        if (_voiceService != null)
        {
            _voiceService.TranscriptReceived -= OnTranscriptReceived;
            _voiceService.StatusChanged -= OnStatusChanged;
            _voiceService.ErrorOccurred -= OnError;
            _voiceService.AnalyzingChanged -= OnAnalyzingChanged;

            await _voiceService.StopAsync();
            _voiceService.Dispose();
            _voiceService = null;
        }

        if (_transcriptionService != null)
        {
            _transcriptionService.TranscriptReceived -= OnTranscriptReceived;
            _transcriptionService.StatusChanged -= OnStatusChanged;
            _transcriptionService.ErrorOccurred -= OnError;
            _transcriptionService.AnalyzingChanged -= OnAnalyzingChanged;

            await _transcriptionService.StopAsync();
            _transcriptionService.Dispose();
            _transcriptionService = null;
        }

        IsConnected = false;
        SubtitleText = "";
        StatusText = "Desconectado";
    }

    public void ToggleHistory()
    {
        IsHistoryVisible = !IsHistoryVisible;
    }

    public void ToggleSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;

        // ╔══════════════════════════════════════════════════════════╗
        // ║ MUTE DO SISTEMA (Windows Core Audio API)                  ║
        // ║                                                          ║
        // ║ Muta o dispositivo de mic no nível do Windows.             ║
        // ║ Um único botão muta em TODO lugar:                        ║
        // ║ - Teams / WhatsApp / Discord / etc.                       ║
        // ║ - WaveInEvent (NAudio) → silenciado                       ║
        // ║ - OpenAI Realtime API → sem custo                        ║
        // ║                                                          ║
        // ║ NOTA: Guarda o estado original do mute para restaurar     ║
        // ║ quando desmutar ou quando o app fechar.                   ║
        // ╚══════════════════════════════════════════════════════════╝
        try
        {
            SetSystemMicMute(IsMuted);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Mute] Erro ao mutar mic do sistema: {ex.Message}");
        }

        // Propaga mute para os serviços (safety net — com mute do sistema
        // o WaveInEvent já recebe silêncio, mas o gate de software evita
        // processar/enviar frames de ruído residual)
        if (_voiceService != null)
        {
            _voiceService.IsMuted = IsMuted;
            if (IsMuted) _voiceService.ClearPendingAudio();
        }
        if (_speakService != null)
        {
            _speakService.IsMuted = IsMuted;
            if (IsMuted) _speakService.ClearPendingAudio();
        }

        StatusText = IsMuted ? "🔇 Mic mutado (sistema + app)" : "🎤 Mic ativo";
    }

    /// <summary>
    /// Muta/desmuta o mic selecionado no nível do Windows usando Core Audio API.
    /// Afeta TODOS os apps que usam o mic (Teams, WhatsApp, etc.).
    /// </summary>
    private void SetSystemMicMute(bool mute)
    {
        var deviceIndex = SelectedMicDevice?.DeviceIndex ?? 0;

        // MMDeviceEnumerator lista dispositivos de captura (mic)
        using var enumerator = new MMDeviceEnumerator();
        var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).ToList();

        if (captureDevices.Count == 0) return;

        // Tenta encontrar o dispositivo correspondente ao índice WaveIn.
        // WaveIn e MMDevice usam ordenações diferentes, então comparamos por nome.
        MMDevice? targetDevice = null;

        if (deviceIndex >= 0 && deviceIndex < WaveInEvent.DeviceCount)
        {
            var waveInCaps = WaveInEvent.GetCapabilities(deviceIndex);
            var waveInName = waveInCaps.ProductName; // truncado a 31 chars

            // WaveIn trunca o nome a 31 caracteres, então usamos StartsWith
            targetDevice = captureDevices.FirstOrDefault(
                d => d.FriendlyName.StartsWith(waveInName, StringComparison.OrdinalIgnoreCase)
                  || d.FriendlyName.Contains(waveInName, StringComparison.OrdinalIgnoreCase));
        }

        // Fallback: dispositivo padrão de captura
        targetDevice ??= enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

        if (targetDevice == null) return;

        if (mute)
        {
            // Guarda estado original antes de mutar
            _wasMicMutedBefore = targetDevice.AudioEndpointVolume.Mute;
            _micMuteManaged = true;
            targetDevice.AudioEndpointVolume.Mute = true;
            System.Diagnostics.Debug.WriteLine(
                $"[Mute] Mic mutado no sistema: {targetDevice.FriendlyName} (era muted={_wasMicMutedBefore})");
        }
        else
        {
            // Restaura estado original — só desmuta se foi ESTE app que mutou
            if (_micMuteManaged)
            {
                targetDevice.AudioEndpointVolume.Mute = _wasMicMutedBefore;
                _micMuteManaged = false;
                System.Diagnostics.Debug.WriteLine(
                    $"[Mute] Mic restaurado no sistema: {targetDevice.FriendlyName} (restaurado para muted={_wasMicMutedBefore})");
            }
        }
    }

    public void RefreshDevices()
    {
        LoadDevices();
    }

    // ─── INTÉRPRETE ATIVO ─────────────────────────────────
    public async Task ToggleSpeakConnectionAsync()
    {
        if (IsSpeakConnected)
            await DisconnectSpeakAsync();
        else
            await ConnectSpeakAsync();
    }

    private async Task ConnectSpeakAsync()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        DotEnv.Load(new DotEnvOptions(envFilePaths: new[] {
            Path.Combine(exeDir, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            ".env"
        }, ignoreExceptions: true));

        var apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            SpeakStatusText = "⚠ OPENAI_API_KEY não encontrada";
            return;
        }

        try
        {
            _speakService = new SpeakTranslateService(apiKey, _sharedAudioState);
            _speakService.StatusChanged += OnSpeakStatusChanged;
            _speakService.ErrorOccurred += OnSpeakError;
            _speakService.SpeakingChanged += OnSpeakingChanged;

            await _speakService.StartAsync(
                SelectedSpeakMicDevice?.DeviceIndex ?? 0,
                SelectedSpeakOutputDevice?.DeviceIndex ?? 0
            );

            IsSpeakConnected = true;
        }
        catch (Exception ex)
        {
            SpeakStatusText = $"⚠ Erro: {ex.Message}";
        }
    }

    private async Task DisconnectSpeakAsync()
    {
        if (_speakService != null)
        {
            _speakService.StatusChanged -= OnSpeakStatusChanged;
            _speakService.ErrorOccurred -= OnSpeakError;
            _speakService.SpeakingChanged -= OnSpeakingChanged;

            await _speakService.StopAsync();
            _speakService.Dispose();
            _speakService = null;
        }

        IsSpeakConnected = false;
        SpeakStatusText = "";
    }

    private void SendMessage()
    {
        var text = InputText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        History.Add(new ConversationEntry
        {
            Speaker = Speaker.You,
            TranslatedText = text
        });

        InputText = string.Empty;
    }
    // ─── BATCHED LOG WRITER ────────────────────────────────
    private async Task RunLogWriter()
    {
        var writers = new Dictionary<string, StreamWriter>();
        try
        {
            await foreach (var (fileName, line) in _logChannel.Reader.ReadAllAsync(_logCts.Token).ConfigureAwait(false))
            {
                var fullPath = Path.Combine(_logBasePath, fileName);
                if (!writers.TryGetValue(fileName, out var writer))
                {
                    writer = new StreamWriter(fullPath, append: true) { AutoFlush = false };
                    writers[fileName] = writer;
                }
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                Console.WriteLine(line);

                // Flush quando o channel estiver vazio (batch completo)
                if (!_logChannel.Reader.TryPeek(out _))
                {
                    foreach (var w in writers.Values)
                        await w.FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { /* best-effort logging */ }
        finally
        {
            foreach (var w in writers.Values)
            {
                try { await w.FlushAsync(); w.Dispose(); } catch { }
            }
        }
    }
    // ─── EVENT HANDLERS ────────────────────────────────────
    private void OnTranscriptReceived(object? sender, TranscriptEventArgs e)
    {
        // Log via channel batched — zero Task.Run, zero File.Open por linha
        var logLine =
            $"[{DateTime.Now:HH:mm:ss}] IsPartial={e.IsPartial}, Speaker={e.Speaker}, Original=\"{e.OriginalText}\", Translated=\"{e.TranslatedText}\"";
        _logChannel.Writer.TryWrite(("transcripts.log", logLine));

        if (e.IsPartial)
        {
            // Throttle: guarda o texto mais recente e agenda UM único dispatch
            _pendingPartialText = e.TranslatedText;

            if (!_partialUpdateScheduled)
            {
                _partialUpdateScheduled = true;
                _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Normal, () =>
                {
                    _partialUpdateScheduled = false;
                    var text = _pendingPartialText;
                    if (text != null)
                    {
                        IsAnalyzing = false;
                        IsAssistantTyping = true;
                        _partialTranscript = text;
                        SubtitleText = text;
                    }
                });
            }
        }
        else
        {
            // Final transcript — prioridade normal, sempre entrega
            var translatedText = e.TranslatedText;
            var originalText = e.OriginalText;
            var speaker = e.Speaker;

            _dispatcher.BeginInvoke(() =>
            {
                IsAnalyzing = false;
                IsAssistantTyping = false;
                _pendingPartialText = null;

                var finalText = string.IsNullOrEmpty(translatedText) ? _partialTranscript : translatedText;

                SubtitleText = finalText;

                if (!string.IsNullOrWhiteSpace(finalText))
                {
                    History.Add(new ConversationEntry
                    {
                        Speaker = speaker,
                        OriginalText = originalText ?? "",
                        TranslatedText = finalText
                    });
                }

                _partialTranscript = "";
            });
        }
    }

    private void OnAnalyzingChanged(object? sender, bool isAnalyzing)
    {
        _dispatcher.BeginInvoke(() =>
        {
            IsAnalyzing = isAnalyzing;
        });
    }

    private void OnStatusChanged(object? sender, StatusEventArgs e)
    {
        _dispatcher.BeginInvoke(() => StatusText = e.Message);
    }

    private void OnError(object? sender, StatusEventArgs e)
    {
        // Log via channel batched
        var errorMsg = e.Message;
        _logChannel.Writer.TryWrite(("error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {errorMsg}"));

        _dispatcher.BeginInvoke(() =>
        {
            IsAnalyzing = false;
            StatusText = $"⚠ {errorMsg}";
        });
    }

    // ─── SPEAK EVENT HANDLERS ──────────────────────────────
    private void OnSpeakStatusChanged(object? sender, StatusEventArgs e)
    {
        _dispatcher.BeginInvoke(() => SpeakStatusText = e.Message);
    }

    private void OnSpeakError(object? sender, StatusEventArgs e)
    {
        _logChannel.Writer.TryWrite(("error.log", $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [Speak] {e.Message}"));
        _dispatcher.BeginInvoke(() => SpeakStatusText = $"⚠ {e.Message}");
    }

    private void OnSpeakingChanged(object? sender, bool isSpeaking)
    {
        // Reservado para indicadores visuais futuros
    }

    // ─── INotifyPropertyChanged ────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        // Restaura mute do mic se este app mutou
        if (_micMuteManaged && IsMuted)
        {
            try { SetSystemMicMute(false); }
            catch { /* best-effort */ }
        }

        if (_voiceService != null)
        {
            _voiceService.TranscriptReceived -= OnTranscriptReceived;
            _voiceService.StatusChanged -= OnStatusChanged;
            _voiceService.ErrorOccurred -= OnError;
            _voiceService.AnalyzingChanged -= OnAnalyzingChanged;
            _voiceService.Dispose();
            _voiceService = null;
        }

        if (_transcriptionService != null)
        {
            _transcriptionService.TranscriptReceived -= OnTranscriptReceived;
            _transcriptionService.StatusChanged -= OnStatusChanged;
            _transcriptionService.ErrorOccurred -= OnError;
            _transcriptionService.AnalyzingChanged -= OnAnalyzingChanged;
            _transcriptionService.Dispose();
            _transcriptionService = null;
        }

        if (_speakService != null)
        {
            _speakService.StatusChanged -= OnSpeakStatusChanged;
            _speakService.ErrorOccurred -= OnSpeakError;
            _speakService.SpeakingChanged -= OnSpeakingChanged;
            _speakService.Dispose();
            _speakService = null;
        }

        // Finaliza o log writer
        _logCts.Cancel();
        _logChannel.Writer.TryComplete();
        _logCts.Dispose();
    }
}
