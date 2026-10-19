namespace DbvoStudio.Core.Audio;

/// <summary>
/// Bethesda .fuz container: "FUZE", uint32 version (1), uint32 LIP size, LIP bytes, then the xWMA (RIFF XWMA) audio.
/// Layout verified byte-for-byte against a Yakitori-built file.
/// </summary>
public static class FuzFile
{
    public static byte[] Build(byte[] lip, byte[] xwm)
    {
        if (xwm.Length < 12 || xwm[0] != 'R' || xwm[1] != 'I' || xwm[2] != 'F' || xwm[3] != 'F' || xwm[8] != 'X' || xwm[9] != 'W')
            throw new InvalidDataException("Audio for a .fuz must be xWMA (RIFF XWMA).");
        var result = new byte[12 + lip.Length + xwm.Length];
        "FUZE"u8.CopyTo(result);
        BitConverter.GetBytes(1u).CopyTo(result, 4);
        BitConverter.GetBytes((uint)lip.Length).CopyTo(result, 8);
        lip.CopyTo(result, 12);
        xwm.CopyTo(result, 12 + lip.Length);
        return result;
    }

    public static (byte[] Lip, byte[] Xwm) Split(byte[] fuz)
    {
        if (fuz.Length < 12 || fuz[0] != 'F' || fuz[1] != 'U' || fuz[2] != 'Z' || fuz[3] != 'E')
            throw new InvalidDataException("Not a .fuz file.");
        int lipSize = (int)BitConverter.ToUInt32(fuz, 8);
        return (fuz.AsSpan(12, lipSize).ToArray(), fuz.AsSpan(12 + lipSize).ToArray());
    }
}
