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

    public MainWindow()
    {
        InitializeComponent();
        _vm = new MainViewModel();
        DataContext = _vm;

        // Cache Storyboard — evita FindResource a cada toggle de IsAnalyzing
        _pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
        Storyboard.SetTarget(_pulseStoryboard, AnalyzingLabel);

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
                    try
                    {
                        HistoryList.ScrollIntoView(_vm.History[^1]);
                    }
                    catch
                    {
                        // Safety net: ignora falhas de layout em edge cases
                    }
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
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
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
}