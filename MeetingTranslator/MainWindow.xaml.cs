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
    
    private System.Windows.Threading.DispatcherTimer? _autoScrollResumeTimer;
    private bool _userIsReadingHistory;
    private System.Windows.Controls.ScrollViewer? _historyScrollViewer;

    [DllImport("user32.dll")]
    public static extern uint SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    // Hotkey: Ctrl+Shift+F = desativa modo furtivo e traz janela
    private const int HOTKEY_ID_STEALTH = 9001;
    private const int HOTKEY_ID_WHISPER = 9002;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT   = 0x0004;
    private const uint VK_F        = 0x46; // tecla F
    private const uint VK_SPACE    = 0x20; // espaço

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

        _autoScrollResumeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _autoScrollResumeTimer.Tick += AutoScrollResumeTimer_Tick;

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
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
                    {
                        try
                        {
                            if (_userIsReadingHistory) return;
                            
                            if (_vm.History.Count > 0)
                            {
                                if (_historyScrollViewer != null)
                                    _historyScrollViewer.ScrollToBottom();
                                else
                                    HistoryList.ScrollIntoView(_vm.History[^1]);
                            }
                        }
                        catch
                        {
                            // Safety net: ignora falhas de layout em edge cases
                        }
                    }));
                }
            }
        };

        HistoryList.Loaded += HistoryList_Loaded;

        // Wire up analyzing animation
        _vm.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void HistoryList_Loaded(object sender, RoutedEventArgs e)
    {
        _historyScrollViewer = FindVisualChild<System.Windows.Controls.ScrollViewer>(HistoryList);
        if (_historyScrollViewer != null)
        {
            _historyScrollViewer.ScrollChanged += HistoryScrollViewer_ScrollChanged;
        }
    }

    private void HistoryScrollViewer_ScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        // Ignore changes that are purely due to extent/viewport resizing (like adding items)
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0 && e.VerticalChange != 0)
        {
            if (_historyScrollViewer == null) return;
            
            // Check if user scrolled far from the bottom
            const double tolerance = 5.0;
            bool isAtBottom = _historyScrollViewer.VerticalOffset >= (_historyScrollViewer.ScrollableHeight - tolerance);

            if (!isAtBottom)
            {
                _userIsReadingHistory = true;
                _autoScrollResumeTimer?.Stop();
                _autoScrollResumeTimer?.Start();
            }
            else
            {
                // User scrolled back to the bottom manually, we can resume auto-scroll immediately
                _userIsReadingHistory = false;
                _autoScrollResumeTimer?.Stop();
            }
        }
    }

    private void AutoScrollResumeTimer_Tick(object? sender, EventArgs e)
    {
        _autoScrollResumeTimer?.Stop();
        _userIsReadingHistory = false;
        
        if (_historyScrollViewer != null)
        {
            _historyScrollViewer.ScrollToBottom();
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                return typedChild;
            
            var result = FindVisualChild<T>(child);
            if (result != null)
                return result;
        }
        return null;
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
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                SubtitleScrollViewer?.ScrollToBottom();
            }));
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

        // Apenas oculta de capturas de tela / compartilhamento de tela.
        // A janela continua 100% visível e acessível para o usuário.
        uint affinity = active ? WDA_EXCLUDEFROMCAPTURE : 0x0; // WDA_NONE = 0
        SetWindowDisplayAffinity(hwnd, affinity);
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

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        this.WindowState = WindowState.Minimized;
    }

    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        this.Opacity = e.NewValue;
        if (OpacityText != null)
        {
            OpacityText.Text = $"{(int)(e.NewValue * 100)}%";
        }
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

        // Registra hotkey global CTRL+SHIFT+F para desativar modo furtivo
        var helper = new WindowInteropHelper(this);
        RegisterHotKey(helper.Handle, HOTKEY_ID_STEALTH, MOD_CONTROL | MOD_SHIFT, VK_F);
        RegisterHotKey(helper.Handle, HOTKEY_ID_WHISPER, MOD_CONTROL, VK_SPACE);

        // Conecta ao WndProc para interceptar a mensagem da hotkey
        HwndSource source = HwndSource.FromHwnd(helper.Handle);
        source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY)
        {
            if (wParam.ToInt32() == HOTKEY_ID_STEALTH)
            {
                // Desativa modo furtivo e traz a janela para frente
                _vm.IsStealthModeActive = false;
                this.Show();
                this.WindowState = WindowState.Normal;
                this.Activate();
                this.Topmost = true;
                handled = true;
            }
            else if (wParam.ToInt32() == HOTKEY_ID_WHISPER)
            {
                // Ativa o gatilho manual stealth do Auto-Sussurro
                _vm.TriggerManualAutoWhisper();
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        // Remove o hotkey global ao fechar
        var helper = new WindowInteropHelper(this);
        if (helper.Handle != IntPtr.Zero)
        {
            UnregisterHotKey(helper.Handle, HOTKEY_ID_STEALTH);
            UnregisterHotKey(helper.Handle, HOTKEY_ID_WHISPER);
        }

        _vm.Dispose();
        base.OnClosed(e);
    }
    
    private async void CaptureFullScreen_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Debug.WriteLine("[UI] Botão Captura Tela Cheia clicado.");
            double dpiX = 1.0, dpiY = 1.0;
            PresentationSource source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiX = source.CompositionTarget.TransformToDevice.M11;
                dpiY = source.CompositionTarget.TransformToDevice.M22;
            }

            int screenX = (int)(SystemParameters.VirtualScreenLeft * dpiX);
            int screenY = (int)(SystemParameters.VirtualScreenTop * dpiY);
            int physicalWidth = (int)(SystemParameters.VirtualScreenWidth * dpiX);
            int physicalHeight = (int)(SystemParameters.VirtualScreenHeight * dpiY);

            // Torna janela principal transparente rápido para não sair no print
            double oldOpacity = this.Opacity;
            this.Opacity = 0;
            await System.Threading.Tasks.Task.Delay(150);

            string? base64 = await System.Threading.Tasks.Task.Run(() =>
            {
                using (var bitmap = new System.Drawing.Bitmap(physicalWidth, physicalHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                {
                    using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
                    {
                        graphics.CopyFromScreen(screenX, screenY, 0, 0, bitmap.Size, System.Drawing.CopyPixelOperation.SourceCopy);
                    }
                    using (var ms = new System.IO.MemoryStream())
                    {
                        bitmap.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            });

            this.Opacity = oldOpacity;

            if (!string.IsNullOrEmpty(base64))
            {
                System.Diagnostics.Debug.WriteLine($"[UI] Captura FullScreen confirmada. Base64 len: {base64.Length}");
                _vm.PendingScreenshotBase64 = base64;
                _vm.AiPrompt = ""; 
                if (!_vm.IsHistoryVisible)
                {
                    _vm.ToggleHistory();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[UI] Erro captura tela cheia: {ex.Message}");
            this.Opacity = 1;
        }
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