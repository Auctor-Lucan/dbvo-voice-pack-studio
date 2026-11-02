using System.Text.Json;
using DbvoStudio.App;
if (args.Length > 0 && args[0] == "--make-data") { MakeData.Run(args.Length > 1 && args[1] == "fresh"); return; }

var stdoutLock = new object();
void Out(object o) { var s = JsonSerializer.Serialize(o, Backend.JsonOptions); lock (stdoutLock) { Console.Out.WriteLine(s); Console.Out.Flush(); } }
var backend = new Backend(new System.Windows.Window(), Out);
Console.Error.WriteLine("[host] ready " + backend.LibraryRoot);
string? line;
var drop = new List<string>();
while ((line = Console.In.ReadLine()) != null)
{
    if (line.StartsWith("PICK "))
    {
        Microsoft.Win32.DemoPick.Next.Enqueue(JsonSerializer.Deserialize<string[]>(line[5..])!);
        continue;
    }
    if (line.StartsWith("DROP ")) { drop = JsonSerializer.Deserialize<List<string>>(line[5..])!; continue; }
    var l = line; var d = drop; drop = new List<string>();
    _ = Task.Run(() => backend.HandleAsync(l, d));
}
