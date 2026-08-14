using Avalonia.Controls;
using Avalonia.Media;

namespace Noks.AvaloniaApp.Views;

public sealed class MainWindow : Window
{
    public MainWindow(MainView view)
    {
        Title = "Noks";
        Width = 480;
        Height = 900;
        MinWidth = 320;
        MinHeight = 568;
        Background = new SolidColorBrush(Color.Parse("#111312"));
        Content = view;
    }
}
