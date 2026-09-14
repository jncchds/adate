using CommunityToolkit.Mvvm.ComponentModel;

namespace Game.App.ViewModels;

/// <summary>One screen. Loading starts once the screen is shown.</summary>
public abstract class PageViewModel : ObservableObject
{
    public virtual Task LoadAsync() => Task.CompletedTask;
}
