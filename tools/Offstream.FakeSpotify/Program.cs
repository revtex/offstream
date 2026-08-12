using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Offstream.FakeSpotify;

/// <summary>
/// A stand-in for the Spotify client that only reproduces the one behaviour Offstream
/// depends on: a window title that changes between track, advertisement and idle states.
/// </summary>
/// <remarks>
/// Phase 1 scaffold — enough to drive title-polling work in Phase 2 without needing the
/// real client running, or an account, or network. Playlist scripting arrives with the
/// Spotify detection work.
/// </remarks>
internal static class Program
{
    private static readonly string[] Titles =
    [
        "Fleetwood Mac - Dreams",
        "Advertisement",
        "Talking Heads - This Must Be the Place",
        "Spotify",
        "Kate Bush - Running Up That Hill (A Deal with God)",
    ];

    [STAThread]
    private static void Main()
    {
        var index = 0;

        var label = new TextBlock
        {
            Margin = new Thickness(16),
            FontSize = 16,
            TextWrapping = TextWrapping.Wrap,
        };

        var window = new Window
        {
            Title = Titles[0],
            Width = 520,
            Height = 200,
            Content = label,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        void Advance()
        {
            window.Title = Titles[index];
            label.Text =
                $"Window title: {window.Title}{Environment.NewLine}{Environment.NewLine}" +
                "Offstream reads the title of this window exactly as it reads Spotify's.";
            index = (index + 1) % Titles.Length;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => Advance();

        window.Loaded += (_, _) =>
        {
            Advance();
            timer.Start();
        };

        new Application().Run(window);
    }
}
