namespace Noks.AvaloniaApp.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel(string title)
    {
        Title = title;
    }

    public string Title { get; }
}
