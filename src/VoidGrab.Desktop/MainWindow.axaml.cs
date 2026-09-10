using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using VoidGrab.ViewModels;

namespace VoidGrab.Desktop;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        // The view model is platform-neutral; the folder picker it needs is not,
        // and on macOS that picker needs the owning window.
        _viewModel = new MainViewModel(new AvaloniaPlatformServices(this));
        DataContext = _viewModel;

        // Keep the newest log line in view without the user chasing it.
        ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("LogScroller")?.ScrollToEnd());

        Opened += async (_, _) => await _viewModel.InitialiseAsync();
        Closing += (_, _) => _viewModel.Persist();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    /// <summary>
    /// Enter queues the link; Shift+Enter inserts a newline.
    /// </summary>
    /// <remarks>
    /// The box accepts several links at once, so plain Enter cannot simply be a
    /// newline — but neither can a multi-line box swallow the most obvious way
    /// to submit one link. Shift is the usual escape hatch for exactly this.
    /// </remarks>
    private void OnLinkKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)) return;

        e.Handled = true;
        if (_viewModel.AddCommand.CanExecute(null)) _viewModel.AddCommand.Execute(null);
    }
}
