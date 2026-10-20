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
        Loaded += async (_, _) => await InitAsync();
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
            core.Settings.AreDevToolsEnabled = true;
            core.Settings.IsZoomControlEnabled = true;
            core.Settings.AreDefaultContextMenusEnabled = true;
            core.WebMessageReceived += OnMessage;
            core.NavigationCompleted += (_, _) => Loading.Visibility = Visibility.Collapsed;
            core.Navigate("https://app.dbvo/index.html");
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Loading.Text = "Microsoft Edge WebView2 Runtime is missing. Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 and restart.";
        }
        catch (Exception ex)
        {
            App.Log("Init failed: " + ex);
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
