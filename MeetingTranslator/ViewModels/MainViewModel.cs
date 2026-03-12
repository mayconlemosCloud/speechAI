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
using MeetingTranslator.Services.Common;
using MeetingTranslator.Services.OpenAI;
using MeetingTranslator.Services.Azure;
using OpenAiVoiceService = MeetingTranslator.Services.OpenAI.VoiceTranslationService;
using AzureVoiceService = MeetingTranslator.Services.Azure.VoiceTranslationService;
using dotenv.net;
using System.Linq;
using System.Windows.Data;

namespace MeetingTranslator.ViewModels;

public partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // --- Serviços ativos ---
    private OpenAiVoiceService? _voiceService;
    private AzureVoiceService? _azureVoiceService;
    private TextTranslationService? _transcriptionService;
    private AzureTranscriptionService? _azureTranscriptionService;
    private IInterpreterService? _speakService;
    private readonly Dispatcher _dispatcher;
    private bool _useAzureProvider;

    /// <summary>
    /// Estado compartilhado entre serviços para evitar cross-contamination de áudio.
    /// </summary>
    private readonly SharedAudioState _sharedAudioState = new();

    // --- Propriedades de interface ---
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

    // Guarda o estado original do mute antes do app alterar
    private bool _wasMicMutedBefore;
    private bool _micMuteManaged;

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

    private TranslationMode _selectedMode = TranslationMode.Transcription;
    public TranslationMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            _selectedMode = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ModeDescription));
        }
    }


    public string ModeDescription => _selectedMode switch
    {
        TranslationMode.Voice => "IA ouve, traduz e fala. Resultado após silêncio.",
        TranslationMode.Transcription => "Texto em tempo real enquanto fala. Tradução após cada frase.",
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

    private bool _isSpeaking;
    public bool IsSpeaking
    {
        get => _isSpeaking;
        set { _isSpeaking = value; OnPropertyChanged(); }
    }

    // --- Intérprete ---
    private bool _isSpeakFeatureEnabled;
    public bool IsSpeakFeatureEnabled
    {
        get => _isSpeakFeatureEnabled;
        set { _isSpeakFeatureEnabled = value; OnPropertyChanged(); }
    }

    private InterpreterProvider _selectedInterpreterProvider = InterpreterProvider.OpenAI;
    public InterpreterProvider SelectedInterpreterProvider
    {
        get => _selectedInterpreterProvider;
        set
        {
            _selectedInterpreterProvider = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsOpenAiInterpreterProvider));
            OnPropertyChanged(nameof(IsAzureInterpreterProvider));
            OnPropertyChanged(nameof(ShowOpenAiInterpreterSettings));
            OnPropertyChanged(nameof(ShowAzureInterpreterSettings));
            OnPropertyChanged(nameof(InterpreterProviderDescription));
        }
    }

    public bool IsOpenAiInterpreterProvider
    {
        get => _selectedInterpreterProvider == InterpreterProvider.OpenAI;
        set { if (value) SelectedInterpreterProvider = InterpreterProvider.OpenAI; }
    }

    public bool IsAzureInterpreterProvider
    {
        get => _selectedInterpreterProvider == InterpreterProvider.AzureSpeech;
        set { if (value) SelectedInterpreterProvider = InterpreterProvider.AzureSpeech; }
    }

    public bool ShowOpenAiInterpreterSettings => _selectedInterpreterProvider == InterpreterProvider.OpenAI;

    public bool ShowAzureInterpreterSettings => _selectedInterpreterProvider == InterpreterProvider.AzureSpeech;

    public string InterpreterProviderDescription => _selectedInterpreterProvider switch
    {
        InterpreterProvider.OpenAI => "Mantem o interprete atual com OpenAI Realtime. Sem quebrar o fluxo que voce ja validou.",
        InterpreterProvider.AzureSpeech => "Prepara o interprete para a arquitetura Azure Speech/Live Interpreter. Ideal para uma segunda implementacao sem substituir a atual.",
        _ => string.Empty
    };

    private string _azureSpeechKey = "";
    public string AzureSpeechKey
    {
        get => _azureSpeechKey;
        set { _azureSpeechKey = value; OnPropertyChanged(); }
    }

    private string _azureSpeechRegion = "";
    public string AzureSpeechRegion
    {
        get => _azureSpeechRegion;
        set { _azureSpeechRegion = value; OnPropertyChanged(); }
    }

    private string _azureSpeechVoice = "en-US-JennyNeural";
    public string AzureSpeechVoice
    {
        get => _azureSpeechVoice;
        set
        {
            _azureSpeechVoice = value;
            OnPropertyChanged();
            // Aplica imediatamente quando o valor muda pela UI
            _ = ApplySelectedVoiceToActiveServicesAsync();
        }
    }

    // Azure voices listing
    private bool _isAzureBusy;
    public bool IsAzureBusy
    {
        get => _isAzureBusy;
        set { _isAzureBusy = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AzureVoiceInfo> AzureVoices { get; } = new();
    public ICollectionView AzureVoicesView { get; }

    private string _azureVoiceFilter = string.Empty;
    public string AzureVoiceFilter
    {
        get => _azureVoiceFilter;
        set
        {
            _azureVoiceFilter = value ?? string.Empty;
            OnPropertyChanged();
            AzureVoicesView.Refresh();
        }
    }

    private AzureVoiceInfo? _selectedAzureVoice;
    public AzureVoiceInfo? SelectedAzureVoice
    {
        get => _selectedAzureVoice;
        set
        {
            _selectedAzureVoice = value;
            OnPropertyChanged();
            if (value != null)
            {
                AzureSpeechVoice = value.ShortName;
                _ = ApplySelectedVoiceToActiveServicesAsync();
            }
        }
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
    /// Aviso quando a saída do intérprete é o mesmo dispositivo que o loopback (causa eco).
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

    // --- Coleções de dados ---
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> SpeakOutputDevices { get; } = new();
    public ObservableCollection<ConversationEntry> History { get; } = new();
    public ObservableCollection<CombinedInputDevice> AllInputDevices { get; } = new();

    private CombinedInputDevice? _selectedInputDevice;
    public CombinedInputDevice? SelectedInputDevice
    {
        get => _selectedInputDevice;
        set
        {
            _selectedInputDevice = value;
            OnPropertyChanged();

            if (value == null) return;

            if (value.IsMic)
            {
                UseMic = true;
                UseLoopback = false;
                var match = MicDevices.FirstOrDefault(d => d.DeviceIndex == value.DeviceIndex)
                            ?? MicDevices.FirstOrDefault(d => d.Name == value.Name || ("🎤 " + d.Name) == value.Name);
                if (match != null) SelectedMicDevice = match;
            }
            else if (value.IsLoopback)
            {
                UseMic = false;
                UseLoopback = true;
                var match = LoopbackDevices.FirstOrDefault(d => d.DeviceIndex == value.DeviceIndex)
                            ?? LoopbackDevices.FirstOrDefault(d => d.Name == value.Name || ("🔊 " + d.Name) == value.Name);
                if (match != null) SelectedLoopbackDevice = match;
            }
        }
    }

    // Acumulador de transcript parcial
    private string _partialTranscript = "";

    // Throttle para transcrições parciais
    private volatile string? _pendingPartialText;
    private volatile bool _partialUpdateScheduled;
    private bool _isDisposed;

    public System.Windows.Input.ICommand CopyBubbleCommand { get; }

    public MainViewModel()
    {
        _dispatcher = Application.Current.Dispatcher;
        LoadEnvironmentVariables();
        LoadDevices();
        
        CopyBubbleCommand = new DelegateCommand(obj => 
        {
            if (obj is MeetingTranslator.Models.ConversationEntry entry)
            {
                try
                {
                    System.Windows.Clipboard.SetText(entry.TranslatedText);
                }
                catch { /* Ignorar erro do clipboard */ }
            }
        });

        _logWriterTask = Task.Run(RunLogWriter);

        // View filtrável para as vozes do Azure
        AzureVoicesView = CollectionViewSource.GetDefaultView(AzureVoices);
        AzureVoicesView.Filter = o =>
        {
            if (string.IsNullOrWhiteSpace(_azureVoiceFilter)) return true;
            if (o is not AzureVoiceInfo v) return false;
            string Norm(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
            var f = Norm(_azureVoiceFilter);
            return Norm(v.ShortName).Contains(f)
                   || Norm(v.LocalName).Contains(f)
                   || Norm(v.Locale).Contains(f)
                   || Norm(v.Gender).Contains(f);
        };

        // Carrega vozes do Azure automaticamente focando em pt-BR (usando .env)
        _ = Task.Run(() => LoadAzureVoicesAsync("pt-BR"));
        
        InitializeAnalysisCommands();
    }

    private bool _envInitialized;
    private void LoadEnvironmentVariables()
    {
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;
        DotEnv.Load(new DotEnvOptions(envFilePaths: new[] {
            Path.Combine(exeDir, ".env"),
            Path.Combine(Directory.GetCurrentDirectory(), ".env"),
            ".env"
        }, ignoreExceptions: true));

        AzureSpeechKey = Environment.GetEnvironmentVariable("AZURE_SPEECH_KEY") ?? "";
        AzureSpeechRegion = Environment.GetEnvironmentVariable("AZURE_SPEECH_REGION") ?? "";
        if (!_envInitialized && string.IsNullOrWhiteSpace(AzureSpeechVoice))
        {
            AzureSpeechVoice = Environment.GetEnvironmentVariable("AZURE_SPEECH_VOICE") ?? "en-US-JennyNeural";
        }

        var provider = Environment.GetEnvironmentVariable("PROVIDER") ?? "openai";
        _useAzureProvider = provider.Equals("azure", StringComparison.OrdinalIgnoreCase);

        SelectedInterpreterProvider = _useAzureProvider
            ? InterpreterProvider.AzureSpeech
            : InterpreterProvider.OpenAI;

        _envInitialized = true;
    }



    public void ToggleHistory()
    {
        IsHistoryVisible = !IsHistoryVisible;
    }

    public void ClearHistory()
    {
        History.Clear();
    }

    public void ToggleSettings()
    {
        IsSettingsVisible = !IsSettingsVisible;
    }

    public void ToggleMute()
    {
        IsMuted = !IsMuted;

        // Muta o mic no nível do Windows (afeta Teams, Discord, etc.)
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


    // --- INotifyPropertyChanged ---
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;

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

        if (_azureTranscriptionService != null)
        {
            _azureTranscriptionService.TranscriptReceived -= OnTranscriptReceived;
            _azureTranscriptionService.StatusChanged -= OnStatusChanged;
            _azureTranscriptionService.ErrorOccurred -= OnError;
            _azureTranscriptionService.AnalyzingChanged -= OnAnalyzingChanged;
            _azureTranscriptionService.Dispose();
            _azureTranscriptionService = null;
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
        try { _logCts.Cancel(); } catch (ObjectDisposedException) { }
        _logChannel.Writer.TryComplete();
        try { _logCts.Dispose(); } catch (ObjectDisposedException) { }
    }
}
