namespace DbvoStudio.Core.Naming;

/// <summary>xxHash64 (seed 0 by default). Matches the reference implementation and DBVO Voice Pack Maker 2.7.0.</summary>
public static class XxHash64
{
    private const ulong P1 = 0x9E3779B185EBCA87UL, P2 = 0xC2B2AE3D27D4EB4FUL, P3 = 0x165667B19E3779F9UL,
        P4 = 0x85EBCA77C2B2AE63UL, P5 = 0x27D4EB2F165667C5UL;

    private static ulong Rotl(ulong x, int r) => (x << r) | (x >> (64 - r));
    private static ulong Round(ulong acc, ulong input) => Rotl(acc + input * P2, 31) * P1;
    private static ulong Merge(ulong acc, ulong val) => (acc ^ Round(0, val)) * P1 + P4;
    private static ulong U64(ReadOnlySpan<byte> b, int o) => BitConverter.ToUInt64(b.Slice(o, 8));
    private static uint U32(ReadOnlySpan<byte> b, int o) => BitConverter.ToUInt32(b.Slice(o, 4));

    public static ulong Hash(ReadOnlySpan<byte> b, ulong seed = 0)
    {
        unchecked
        {
            int n = b.Length, p = 0;
            ulong h;
            if (n >= 32)
            {
                ulong v1 = seed + P1 + P2, v2 = seed + P2, v3 = seed, v4 = seed - P1;
                while (p <= n - 32)
                {
                    v1 = Round(v1, U64(b, p)); p += 8;
                    v2 = Round(v2, U64(b, p)); p += 8;
                    v3 = Round(v3, U64(b, p)); p += 8;
                    v4 = Round(v4, U64(b, p)); p += 8;
                }
                h = Rotl(v1, 1) + Rotl(v2, 7) + Rotl(v3, 12) + Rotl(v4, 18);
                h = Merge(h, v1); h = Merge(h, v2); h = Merge(h, v3); h = Merge(h, v4);
            }
            else
            {
                h = seed + P5;
            }
            h += (ulong)n;
            while (p + 8 <= n)
            {
                h ^= Round(0, U64(b, p));
                h = Rotl(h, 27) * P1 + P4;
                p += 8;
            }
            if (p + 4 <= n)
            {
                h ^= U32(b, p) * P1;
                h = Rotl(h, 23) * P2 + P3;
                p += 4;
            }
            while (p < n)
            {
                h ^= b[p] * P5;
                h = Rotl(h, 11) * P1;
                p++;
            }
            h ^= h >> 33; h *= P2; h ^= h >> 29; h *= P3; h ^= h >> 32;
            return h;
        }
    }
}
