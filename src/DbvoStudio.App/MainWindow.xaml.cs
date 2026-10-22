using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace DbvoStudio.App;

public partial class MainWindow : Window
{
    private Backend? _backend;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowChrome.Apply(this, "#010409", "#e6edf3", true);
        Loaded += async (_, _) => await InitAsync();
        Backend.PageReady += () => Dispatcher.BeginInvoke(App.CloseSplash);
    }

    private async Task InitAsync()
    {
        try
        {
            var dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBVOStudio", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);
            var core = Web.CoreWebView2;

            _backend = new Backend(this, Post);
            var www = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            core.SetVirtualHostNameToFolderMapping("app.dbvo", www, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("media.dbvo", _backend.LibraryRoot, CoreWebView2HostResourceAccessKind.Allow);
            core.SetVirtualHostNameToFolderMapping("user.dbvo", Backend.UserFilesDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.WebMessageReceived += OnMessage;
            // Recording takes: the app's own pages may use the microphone without a browser prompt; nothing else can.
            core.PermissionRequested += (_, e) =>
            {
                if (e.PermissionKind == CoreWebView2PermissionKind.Microphone)
                    e.State = e.Uri.StartsWith("https://app.dbvo/", StringComparison.OrdinalIgnoreCase) ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
            };
            core.NavigationCompleted += (_, _) =>
            {
                Loading.Visibility = Visibility.Collapsed;
                // The page closes the splash when its first screen is drawn (window.ready); this is the safety net.
                _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ => Dispatcher.BeginInvoke(App.CloseSplash));
            };
            core.Navigate("https://app.dbvo/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            App.CloseSplash();
            Loading.Text = "Microsoft Edge WebView2 Runtime is missing. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and restart.";
        }
        catch (Exception ex)
        {
            App.Log("Init failed: " + ex);
            App.CloseSplash();
            Loading.Text = "Could not start: " + ex.Message;
        }
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_backend == null) return;
        string json;
        try { json = e.WebMessageAsJson; }
        catch { return; }

        // Files dropped onto the page arrive as additional objects with real paths.
        var dropped = new List<string>();
        if (e.AdditionalObjects != null)
        {
            foreach (var o in e.AdditionalObjects)
                if (o is CoreWebView2File f) dropped.Add(f.Path);
        }
        await _backend.HandleAsync(json, dropped);
    }

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, Backend.JsonOptions);
        Dispatcher.BeginInvoke(() =>
        {
            try { Web.CoreWebView2?.PostWebMessageAsJson(json); }
            catch (Exception ex) { App.Log("Post failed: " + ex.Message); }
        });
    }
}
