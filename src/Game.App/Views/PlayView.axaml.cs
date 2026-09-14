using Avalonia.Controls;
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
        }

        _model = DataContext as PlayViewModel;
        if (_model is not null)
        {
            _model.ScrollToTopRequested += ScrollToTop;
        }

        base.OnDataContextChanged(e);
    }

    private void ScrollToTop() => Side.ScrollToHome();
}
