using System.Collections.Specialized;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using DeskBeam.Host.ViewModels;

namespace DeskBeam.Host;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private bool _closing;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        _viewModel.LogLines.CollectionChanged += OnLogChanged;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel.LogLines.Count == 0)
            return;

        LogList.ScrollIntoView(_viewModel.LogLines[^1]);
    }

    private async void OnCopyShare(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null && !string.IsNullOrWhiteSpace(_viewModel.ShareText))
            await clipboard.SetTextAsync(_viewModel.ShareText);
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closing)
        {
            _closing = true;

            if (_viewModel.IsRunning)
            {
                e.Cancel = true;
                try
                {
                    await _viewModel.ShutdownAsync();
                }
                catch (Exception)
                {
                    // Never block the window from closing.
                }

                Close();
                return;
            }

            _ = _viewModel.ShutdownAsync();
        }

        base.OnClosing(e);
    }
}
