using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
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

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Cache Storyboard — evita FindResource a cada toggle de IsAnalyzing
        _pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
        Storyboard.SetTarget(_pulseStoryboard, AnalyzingLabel);

        // Cache das novas storyboards
        _slideDownStoryboard = (Storyboard)FindResource("SlideDownAnimation");
        _slideUpStoryboard = (Storyboard)FindResource("SlideUpAnimation");
        _subtitleFadeInStoryboard = (Storyboard)FindResource("SubtitleFadeIn");
        Storyboard.SetTarget(_subtitleFadeInStoryboard, SubtitleBlock);

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
                _pulseStoryboard?.Begin();
            }
            else
            {
                _pulseStoryboard?.Stop();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsSettingsVisible))
        {
            var sb = _vm.IsSettingsVisible ? _slideDownStoryboard : _slideUpStoryboard;
            if (sb != null)
            {
                Storyboard.SetTarget(sb, SettingsPanel);
                sb.Begin();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.IsHistoryVisible))
        {
            var sb = _vm.IsHistoryVisible ? _slideDownStoryboard : _slideUpStoryboard;
            if (sb != null)
            {
                Storyboard.SetTarget(sb, HistoryPanel);
                sb.Begin();
            }
        }
        else if (e.PropertyName == nameof(MainViewModel.SubtitleText))
        {
            _subtitleFadeInStoryboard?.Begin();
        }
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

    protected override void OnClosed(EventArgs e)
    {
        _vm.Dispose();
        base.OnClosed(e);
    }
    
    private void CaptureScreen_Click(object sender, RoutedEventArgs e)
    {
        var screenWindow = new ScreenCaptureWindow();
        if (screenWindow.ShowDialog() == true && !string.IsNullOrEmpty(screenWindow.Base64CapturedImage))
        {
            var base64 = screenWindow.Base64CapturedImage;
            
            // Guarda a imagem pendente no ViewModel e abre o chat
            _vm.PendingScreenshotBase64 = base64;
            _vm.AiPrompt = ""; // Limpa prompt para focar na imagem
            
            // Abre o drawer de histórico/chat
            if (!_vm.IsHistoryVisible)
            {
                _vm.ToggleHistory();
            }
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