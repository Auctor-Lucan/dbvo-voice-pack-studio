using System.Text;

namespace DbvoStudio.Core.Text;

/// <summary>Text encodings used by Skyrim plugins and strings files.</summary>
public static class Encodings
{
    private static readonly Lazy<Encoding> Cp1252Lazy = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252);
    });

    /// <summary>Windows-1252, the code page of English Skyrim SE plugin and strings text.</summary>
    public static Encoding Cp1252 => Cp1252Lazy.Value;

    /// <summary>UTF-8 without byte-order mark (topic files).</summary>
    public static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
}
