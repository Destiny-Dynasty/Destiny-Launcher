using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using DestinyLauncher;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Run the real App startup handler; invoke with --data-root to isolate it.
        var app = new App();
        app.InitializeComponent();
        var clock = Stopwatch.StartNew();
        double? introStart = null;
        double? mainStart = null;
        var overlap = false;
        var failed = false;
        var animations = SystemParameters.ClientAreaAnimation;
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(15) };
        timer.Tick += (_, _) =>
        {
            var windows = app.Windows.Cast<Window>().ToArray();
            var intro = windows.FirstOrDefault(w => w.Title == "Destiny · Iniciando" && w.IsVisible);
            var main = windows.OfType<MainWindow>().FirstOrDefault(w => w.IsVisible);
            if (intro is not null) introStart ??= clock.Elapsed.TotalSeconds;
            if (intro is not null && main is not null) overlap = true;
            if (main is not null)
            {
                mainStart = clock.Elapsed.TotalSeconds;
                failed = overlap || (animations && (introStart is null || mainStart - introStart < 2.9));
                timer.Stop();
                main.Close(); // Also verifies OnMainWindowClose terminates the real App.
            }
            else if (clock.Elapsed.TotalSeconds > 15)
            {
                failed = true;
                timer.Stop();
                app.Shutdown(1);
            }
        };
        timer.Start();
        var exit = app.Run();
        var result = new { animations, introStart, mainStart, overlap, exit, passed = !failed && mainStart is not null };
        var output = Path.GetFullPath("artifacts/startup-smoke");
        Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(output, "startup-result.json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(result));
        return result.passed ? 0 : 1;
    }
}
