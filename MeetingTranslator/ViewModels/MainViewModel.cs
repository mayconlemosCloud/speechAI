using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MeetingTranslator.Models;
using MeetingTranslator.Services;
using dotenv.net;

namespace MeetingTranslator.ViewModels;

public class MainViewModel : INotifyPropertyChanged, IDisposable
{
    private RealtimeService? _service;
    private readonly Dispatcher _dispatcher;

    // ─── BINDABLE PROPERTIES ───────────────────────────────
    private string _subtitleText = "Aguardando conexão...";
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
        set { _selectedLoopbackDevice = value; OnPropertyChanged(); }
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

    // ─── COLLECTIONS ──────────────────────────────────────
    public ObservableCollection<AudioDeviceInfo> MicDevices { get; } = new();
    public ObservableCollection<AudioDeviceInfo> LoopbackDevices { get; } = new();
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

        if (MicDevices.Count > 0) SelectedMicDevice = MicDevices[0];
        if (LoopbackDevices.Count > 0) SelectedLoopbackDevice = LoopbackDevices[0];
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
            SubtitleText = "⚠ OPENAI_API_KEY não encontrada";
            return;
        }

        _service = new RealtimeService(apiKey);

        _service.TranscriptReceived += OnTranscriptReceived;
        _service.StatusChanged += OnStatusChanged;
        _service.ErrorOccurred += OnError;
        _service.AnalyzingChanged += OnAnalyzingChanged;

        try
        {
            await _service.StartAsync(
                SelectedMicDevice?.DeviceIndex ?? 0,
                SelectedLoopbackDevice?.DeviceIndex ?? 0,
                UseMic,
                UseLoopback
            );
            IsConnected = true;
            SubtitleText = "Pronto — ouvindo...";
        }
        catch (Exception ex)
        {
            SubtitleText = $"⚠ Erro: {ex.Message}";
        }
    }

    private async Task DisconnectAsync()
    {
        if (_service != null)
        {
            // Desregistra handlers para evitar memory leak e chamadas fantasma
            _service.TranscriptReceived -= OnTranscriptReceived;
            _service.StatusChanged -= OnStatusChanged;
            _service.ErrorOccurred -= OnError;
            _service.AnalyzingChanged -= OnAnalyzingChanged;

            await _service.StopAsync();
            _service.Dispose();
            _service = null;
        }
        IsConnected = false;
        SubtitleText = "Desconectado";
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
        // TODO: actually mute the mic in the service
    }

    public void RefreshDevices()
    {
        LoadDevices();
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
            $"[{DateTime.Now:HH:mm:ss}] IsPartial={e.IsPartial}, Speaker={e.Speaker}, Text=\"{e.TranslatedText}\"";
        _logChannel.Writer.TryWrite(("transcripts.log", logLine));

        if (e.IsPartial)
        {
            // Throttle: guarda o texto mais recente e agenda UM único dispatch
            _pendingPartialText = e.TranslatedText;

            if (!_partialUpdateScheduled)
            {
                _partialUpdateScheduled = true;
                _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
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
            if (isAnalyzing)
            {
                SubtitleText = "🔄 Analisando...";
            }
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
            SubtitleText = $"⚠ {errorMsg}";
        });
    }

    // ─── INotifyPropertyChanged ────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        if (_service != null)
        {
            _service.TranscriptReceived -= OnTranscriptReceived;
            _service.StatusChanged -= OnStatusChanged;
            _service.ErrorOccurred -= OnError;
            _service.AnalyzingChanged -= OnAnalyzingChanged;
            _service.Dispose();
            _service = null;
        }

        // Finaliza o log writer
        _logCts.Cancel();
        _logChannel.Writer.TryComplete();
        _logCts.Dispose();
    }
}
