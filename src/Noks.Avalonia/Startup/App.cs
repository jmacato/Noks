using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Noks.AvaloniaApp.Diagnostics;
using Noks.AvaloniaApp.Views;

namespace Noks.AvaloniaApp.Startup;

public sealed class App : Avalonia.Application
{
#if !BROWSER
    private DesktopAutomationServer? automationServer;
#endif
    public static Func<IReadOnlyList<string>, MainView>? CreateMainView { get; set; }

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            IReadOnlyList<string> args = desktop.Args ?? [];
            MainView view = CreateView(args);
#if !BROWSER
            automationServer = DesktopAutomationServer.TryStart(args, () => view.Emulator);
            desktop.Exit += (_, _) => automationServer?.Dispose();
#endif
            MainWindow window = new(view);
            desktop.MainWindow = window;
            window.Show();
            window.Activate();
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = CreateView([]);
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static MainView CreateView(IReadOnlyList<string> args)
    {
        return CreateMainView?.Invoke(args) ?? new MainView(args);
    }
}
