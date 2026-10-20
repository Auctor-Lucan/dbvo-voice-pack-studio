using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DbvoStudio.App;

/// <summary>Colours the Windows title bar to match the theme (DWM: caption/text colour on Windows 11, dark mode on 10 and 11).</summary>
internal static class WindowChrome
{
    private const int DwmUseImmersiveDarkModeOld = 19, DwmUseImmersiveDarkMode = 20, DwmBorderColor = 34, DwmCaptionColor = 35, DwmTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Apply(Window w, string caption, string text, bool dark)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = dark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, 4) != 0) DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref on, 4);
        if (TryColor(caption, out int c)) { DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref c, 4); DwmSetWindowAttribute(hwnd, DwmBorderColor, ref c, 4); }
        if (TryColor(text, out int t)) DwmSetWindowAttribute(hwnd, DwmTextColor, ref t, 4);
    }

    /// <summary>"#rrggbb" to a COLORREF (0x00bbggrr).</summary>
    private static bool TryColor(string hex, out int colorref)
    {
        colorref = 0;
        hex = hex.Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out int rgb)) return false;
        int r = (rgb >> 16) & 0xFF, g = (rgb >> 8) & 0xFF, b = rgb & 0xFF;
        colorref = r | (g << 8) | (b << 16);
        return true;
    }
}
