using System.IO;
using System.Windows;
using System.Windows.Threading;
using WinFormsApp = System.Windows.Forms.Application;
namespace PatchcordInstaller;

public partial class App : System.Windows.Application
{
    // Log path lives next to the exe if writable, otherwise falls back to %TEMP%.
    private static readonly string LogPath = ResolveLogPath();

    protected override void OnStartup(StartupEventArgs e)
    {
        // Catch anything that would otherwise kill the process silently, at every
        // layer .NET exposes: UI thread, background threads, and unobserved tasks.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ReportFatal(args.ExceptionObject as Exception, "AppDomain.UnhandledException");

        DispatcherUnhandledException += (_, args) =>
        {
            ReportFatal(args.Exception, "Dispatcher.UnhandledException");
            args.Handled = true; // keep the window open instead of hard-crashing
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ReportFatal(args.Exception, "TaskScheduler.UnobservedTaskException");
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    private static string ResolveLogPath()
    {
        try
        {
            var dir = AppDomain.CurrentDomain.BaseDirectory;
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return Path.Combine(dir, "PatchcordInstaller-crash.log");
        }
        catch
        {
            return Path.Combine(Path.GetTempPath(), "PatchcordInstaller-crash.log");
        }
    }

    private static void ReportFatal(Exception? ex, string source)
    {
        var text = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n";
        try { File.AppendAllText(LogPath, text); } catch { /* best effort */ }

        try
        {
            System.Windows.MessageBox.Show(
                $"Patchcord Installer hit an unexpected error and needs to close.\n\n" +
                $"{ex?.GetType().Name}: {ex?.Message}\n\n" +
                $"Details were written to:\n{LogPath}\n\n" +
                "Please share that file if you need help troubleshooting.",
                "Patchcord Installer — Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch { /* if even the message box fails, at least the log file exists */ }
    }
}
