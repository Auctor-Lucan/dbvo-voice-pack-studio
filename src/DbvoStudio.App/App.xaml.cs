using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DbvoStudio.App;

public partial class App : Application
{
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
        MessageBox.Show(e.Exception.Message, "DBVO Voice Pack Studio", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
