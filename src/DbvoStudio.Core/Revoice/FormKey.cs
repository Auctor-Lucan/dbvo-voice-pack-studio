using DbvoStudio.Core.Plugins;

namespace DbvoStudio.Core.Revoice;

/// <summary>
/// A load-order independent form reference: the plugin that defines the form plus its 24-bit object ID.
/// This is also exactly what voice file names use (the FormID with the plugin index stripped).
/// </summary>
public readonly record struct FormKey(string Plugin, uint Id)
{
    public static FormKey Resolve(PluginFile file, uint raw)
    {
        int idx = (int)(raw >> 24);
        string owner = idx < file.Masters.Count ? file.Masters[idx] : file.FileName;
        return new FormKey(owner.ToLowerInvariant(), raw & 0xFFFFFF);
    }

    public bool IsNull => Id == 0 && Plugin.Length == 0;
    public string Hex => Id.ToString("X6");
    public override string ToString() => $"{Plugin}|{Id:X6}";

    /// <summary>"Skyrim.esm|1A696" style used by SkyPatcher (plugin name as spelled in the load order).</summary>
    public string ToSkyPatcher(string pluginSpelling) => $"{pluginSpelling}|{Id:X}";

    /// <summary>"0x1A696~Skyrim.esm" style used by FormList Manipulator.</summary>
    public string ToFlm(string pluginSpelling) => $"0x{Id:X}~{pluginSpelling}";
}
