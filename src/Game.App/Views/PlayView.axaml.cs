using Avalonia.Controls;
using Avalonia.Threading;
using Game.App.ViewModels;

namespace Game.App.Views;

public partial class PlayView : UserControl
{
    private PlayViewModel? _model;

    public PlayView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_model is not null)
        {
            _model.ScrollToTopRequested -= ScrollToTop;
            _model.ScrollToEndRequested -= ScrollToEnd;
        }

        _model = DataContext as PlayViewModel;
        if (_model is not null)
        {
            _model.ScrollToTopRequested += ScrollToTop;
            _model.ScrollToEndRequested += ScrollToEnd;
        }

        base.OnDataContextChanged(e);
    }

    private void ScrollToTop() => Side.ScrollToHome();

    // After layout, so the newest words are already measured when the view follows them.
    private void ScrollToEnd() => Dispatcher.UIThread.Post(() => Side.ScrollToEnd(), DispatcherPriority.Background);
}
