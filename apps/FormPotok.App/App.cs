using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace FormPotok.App;

public sealed class App : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = LocalTextWindow.CreateStartupWindow(
                () => new FormPotok.PilotCore.LocalText.LocalTextWorkspace(LocalTextRuntime.ResolveStorageRoot()));
        }

        base.OnFrameworkInitializationCompleted();
    }
}
