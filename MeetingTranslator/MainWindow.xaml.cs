using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using MeetingTranslator.ViewModels;

namespace MeetingTranslator;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private Storyboard? _pulseStoryboard;
    private Storyboard? _slideDownStoryboard;
    private Storyboard? _slideUpStoryboard;
    private Storyboard? _subtitleFadeInStoryboard;
    
    [DllImport("user32.dll")]
    public static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Cache Storyboard — evita FindResource a cada toggle de IsAnalyzing
        _pulseStoryboard = (Storyboard)FindResource("PulseAnimation");

        // Cache das novas storyboards
        _slideDownStoryboard = (Storyboard)FindResource("SlideDownAnimation");
        _slideUpStoryboard = (Storyboard)FindResource("SlideUpAnimation");
        _subtitleFadeInStoryboard = (Storyboard)FindResource("SubtitleFadeIn");

        // Auto-scroll history when new items are added
        _vm.History.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add ||
                e.Action == NotifyCollectionChangedAction.Replace)
            {
                // Só faz scroll se o histórico estiver visível e o ListBox renderizado.
                // Chamar ScrollIntoView num ListBox colapsado causa exceção
                // que congela o dispatcher do WPF.
                if (_vm.History.Count > 0 && _vm.IsHistoryVisible && HistoryList.IsVisible)
                {
                    // Defer scroll so WPF finishes measuring/arranging the new item first
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
                    {
                        try
                        {
                            if (_vm.History.Count > 0)
                                HistoryList.ScrollIntoView(_vm.History[^1]);
                        }
                        catch
                        {
                            // Safety net: ignora falhas de layout em edge cases
                        }
                    });
                }
            }
        };

        // Wire up analyzing animation
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsAnalyzing))
        {
            if (_vm.IsAnalyzing)
            {
                _pulseStoryboard?.Begin(AnalyzingLabel);
            }
            else
            {
                _pulseStoryboard?.Stop(AnalyzingLabel);
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSettingsVisible))
        {
            var sb = _vm.IsSettingsVisible ? _slideDownStoryboard : _slideUpStoryboard;
            sb?.Begin(SettingsPanel);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsHistoryVisible))
        {
            var sb = _vm.IsHistoryVisible ? _slideDownStoryboard : _slideUpStoryboard;
            sb?.Begin(HistoryPanel);
        }
        else if (e.PropertyName == nameof(MainViewModel.SubtitleText))
        {
            _subtitleFadeInStoryboard?.Begin(SubtitleBlock);
        }
        else if (e.PropertyName == nameof(MainViewModel.IsStealthModeActive))
        {
            ApplyStealthMode(_vm.IsStealthModeActive);
        }
    }

    private void ApplyStealthMode(bool active)
    {
        var helper = new WindowInteropHelper(this);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return;

        // 1. Invisible on Screen Share
        uint affinity = active ? WDA_EXCLUDEFROMCAPTURE : 0x0; // WDA_NONE = 0
        SetWindowDisplayAffinity(hwnd, affinity);

        // 2. Invisible to Tab Switching (Alt+Tab)
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        if (active)
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TOOLWINDOW);
        else
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_TOOLWINDOW);

        // 3. Cursor visibility
        this.Cursor = active ? Cursors.None : Cursors.Arrow;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.S)
        {
            e.Handled = true;
            CaptureScreen_Click(this, new RoutedEventArgs());
        }
    }

    private async void ToggleConnect_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ToggleConnectionAsync();
    }

    private void ToggleHistory_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleHistory();
    }

    private void ToggleMute_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleMute();
    }

    private void ToggleSettings_Click(object sender, RoutedEventArgs e)
    {
        _vm.ToggleSettings();
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        _vm.RefreshDevices();
    }

    private async void ToggleSpeak_Click(object sender, RoutedEventArgs e)
    {
        await _vm.ToggleSpeakConnectionAsync();
    }

    private async void ListAzureVoices_Click(object sender, RoutedEventArgs e)
    {
        // Load all locales; could filter later by UI
        await _vm.LoadAzureVoicesAsync();
    }

    private async void PreviewAzureVoice_Click(object sender, RoutedEventArgs e)
    {
        await _vm.PreviewSelectedAzureVoiceAsync();
    }


    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _vm.ClearHistory();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _vm.Dispose();
        Close();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyStealthMode(_vm.IsStealthModeActive);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
    
    private void CaptureScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[UI] Botão Captura clicado.");
            var screenWindow = new ScreenCaptureWindow();
            screenWindow.IsStealthModeActive = _vm.IsStealthModeActive;
            bool result = screenWindow.ShowDialog() == true;
            System.Diagnostics.Debug.WriteLine($"[UI] ShowDialog retornou: {result}");

            if (result && !string.IsNullOrEmpty(screenWindow.Base64CapturedImage))
            {
                var base64 = screenWindow.Base64CapturedImage;
                System.Diagnostics.Debug.WriteLine($"[UI] Captura confirmada. Base64 len: {base64.Length}");
                
                // Guarda a imagem pendente no ViewModel
                _vm.PendingScreenshotBase64 = base64;
                _vm.AiPrompt = ""; // Limpa prompt para focar na imagem
                
                // Abre o drawer de histórico/chat se estiver fechado
                if (!_vm.IsHistoryVisible)
                {
                    _vm.ToggleHistory();
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[UI] Captura cancelada ou vazia.");
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UI] Erro ao abrir captura: {ex.Message}");
            MessageBox.Show($"Erro ao iniciar captura: {ex.Message}", "Erro de Captura", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ClearScreenshot_Click(object sender, RoutedEventArgs e)
    {
        _vm.PendingScreenshotBase64 = null;
    }

    private async void SendAiMessage_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SendAiMessageAsync();
    }

    private async void AiPrompt_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await _vm.SendAiMessageAsync();
        }
    }

    private async void AnalyzeMeeting_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.IsHistoryVisible)
        {
            _vm.ToggleHistory();
        }
        await _vm.AnalyzeMeetingHistoryAsync();
    }
}