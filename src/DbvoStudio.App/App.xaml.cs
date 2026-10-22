using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DbvoStudio.App;

public partial class App : Application
{
    private static SplashScreen? _splash;

    /// <summary>The splash shows at once, before the window and the web page load, so a slow start is visibly a start.</summary>
    public App()
    {
        try { _splash = new SplashScreen("assets/splash.png"); _splash.Show(false, true); }
        catch (Exception ex) { Log("Splash: " + ex.Message); _splash = null; }
    }

    /// <summary>Fade the splash out (the page is ready, or starting failed and the window has something to say).</summary>
    public static void CloseSplash()
    {
        var s = _splash;
        _splash = null;
        try { s?.Close(TimeSpan.FromMilliseconds(350)); } catch { }
    }

    public static string LogPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBVOStudio", "app.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch { }
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("Unhandled: " + e.Exception);
        MessageBox.Show(e.Exception.Message, "DBVO Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
