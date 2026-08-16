using CommunityToolkit.Mvvm.ComponentModel;

namespace IcarusStarlink.App.ViewModels;

public abstract class PlaceholderViewModelBase : ObservableObject
{
    public string Title { get; }

    public string PlaceholderMessage { get; }

    protected PlaceholderViewModelBase(string title, string placeholderMessage)
    {
        Title = title;
        PlaceholderMessage = placeholderMessage;
    }
}
