using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace LazerReplayCompare;

public sealed class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The app's typeface is LRC Sans. Torus (osu!'s typeface) isn't redistributed with the app; a build made with
        // the font files in LocalFonts has them in it, and then Torus leads and LRC Sans fills in what it lacks.
        bool torus = Avalonia.Platform.AssetLoader.Exists(new Uri("avares://LazerReplayCompare/Assets/Fonts/Torus-Regular.otf"));
        Resources["AppFontFamily"] = Avalonia.Media.FontFamily.Parse(torus
            ? "avares://LazerReplayCompare/Assets/Fonts#Torus, LRC Sans"
            : "avares://LazerReplayCompare/Assets/Fonts#LRC Sans");

        // a number in its box: Torus's digits hang under the middle of its line, so its box has more room below
        Resources["NumberBoxPaddingLarge"] = torus ? new Thickness(11, 0, 11, 5) : new Thickness(11, 2, 11, 3);
        Resources["NumberBoxPaddingMedium"] = torus ? new Thickness(8, 0, 8, 3) : new Thickness(8, 1, 8, 2);
        Resources["NumberBoxPaddingSmall"] = torus ? new Thickness(6, 0, 6, 2) : new Thickness(6, 1, 6, 1);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow();

        base.OnFrameworkInitializationCompleted();
    }
}
