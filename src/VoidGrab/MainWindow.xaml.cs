using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using VoidGrab.ViewModels;

namespace VoidGrab;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _viewModel;

        // Keep the newest log line in view without the user chasing it. Bound to
        // the collection rather than a scroll event so it also fires for lines
        // that arrive while the window is in the background.
        ((INotifyCollectionChanged)_viewModel.Log).CollectionChanged += (_, _) =>
            LogScroller.ScrollToEnd();

        Loaded += async (_, _) => await _viewModel.InitialiseAsync();
        Closing += (_, _) => _viewModel.Persist();
    }

    /// <summary>
    /// Enter queues the link; Shift+Enter inserts a newline.
    /// </summary>
    /// <remarks>
    /// The box accepts several links at once, so plain Enter cannot simply be a
    /// newline — but neither can a multi-line box swallow the most obvious way
    /// to submit one link. Shift is the usual escape hatch for exactly this.
    /// </remarks>
    private void OnLinkKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || (Keyboard.Modifiers & ModifierKeys.Shift) != 0) return;

        e.Handled = true;
        if (_viewModel.AddCommand.CanExecute(null)) _viewModel.AddCommand.Execute(null);
    }
}
