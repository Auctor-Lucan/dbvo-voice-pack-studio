using System.Text.Json;
namespace System.Windows
{
    public class Dispatcher
    {
        public T Invoke<T>(Func<T> f) => f();
        public void Invoke(Action a) => a();
        public void BeginInvoke(Action a) { }
    }
    public class Window { public Dispatcher Dispatcher { get; } = new(); }
    public class Application { public static Application Current { get; } = new(); public void Shutdown() { } }
}
namespace Microsoft.Win32
{
    public static class DemoPick
    {
        public static readonly Queue<string[]> Next = new();
        public static string[]? Take() => Next.Count > 0 ? Next.Dequeue() : null;
    }
    public class OpenFileDialog
    {
        public string Title { get; set; } = ""; public bool Multiselect { get; set; } public string Filter { get; set; } = "";
        public string FileName { get; set; } = ""; public string[] FileNames { get; set; } = Array.Empty<string>();
        public bool? ShowDialog(object? o = null) { var p = DemoPick.Take(); if (p == null || p.Length == 0) return false; FileNames = p; FileName = p[0]; return true; }
    }
    public class OpenFolderDialog
    {
        public string Title { get; set; } = ""; public string FolderName { get; set; } = "";
        public bool? ShowDialog(object? o = null) { var p = DemoPick.Take(); if (p == null || p.Length == 0) return false; FolderName = p[0]; return true; }
    }
    public class SaveFileDialog
    {
        public string Title { get; set; } = ""; public string Filter { get; set; } = ""; public string FileName { get; set; } = "";
        public bool? ShowDialog(object? o = null) { var p = DemoPick.Take(); if (p == null || p.Length == 0) return false; FileName = p[0]; return true; }
    }
}
namespace DbvoStudio.App
{
    public static class App { public static void Log(string m) => Console.Error.WriteLine("[log] " + m); }
    public static class Secrets { public static string Protect(string s) => s; public static string Unprotect(string s) => s; }
    public static class WindowChrome { public static void Apply(object w, string a, string b, bool d) { } }
}
