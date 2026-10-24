using DbvoStudio.Core.Plugins;

namespace DbvoStudio.Core.Grunts;

/// <summary>A plugin written for a PCHT voice pack, and what it holds.</summary>
public sealed class PchtPlugin
{
    public required string FileName { get; init; }
    public required byte[] Bytes { get; init; }
    public List<string> Masters { get; init; } = new();
    public int Overrides { get; init; }
    public int NewRecords { get; init; }
    /// <summary>MPCHT: the FormList Manipulator lines that register the voice set.</summary>
    public List<string> FlmLines { get; init; } = new();
    public string FemaleVoiceType { get; init; } = "";
    public string MaleVoiceType { get; init; } = "";
}

/// <summary>
/// The plugins of a PCHT voice pack, built from the installed "PC Head Tracking - MCM.esp" (which is only read, never changed).
///
/// Standard pack: PCHT plays a random file of each event's sound descriptor (SNDR), and the base plugin lists 5 (greetings 10).
/// A pack with a different number of lines needs those descriptors to list its files, which is what the Vampira pack does by
/// shipping an edited copy of the base plugin. Here a separate ESL-flagged plugin overrides only the descriptors that change,
/// so the base plugin stays untouched and later PCHT updates of other records still apply. It must load after
/// PC Head Tracking - MCM.esp (it is its master, so every load order tool puts it there).
///
/// Multi Voice Packs (MPCHT): the same as the MPCHT Toolkit's xEdit script (!PCHT_create_a_voiceset.pas): copies of the voice
/// event effect and spell, every sound descriptor, sound and voice type, as new records with the prefix added to their editor
/// IDs, the sound paths moved into Sound\FX\PC Head Tracking\&lt;prefix&gt;, the copies linked to each other, the spell given the
/// pack's name and gender keyword, and the FormList Manipulator lines that add the set to the MCM's lists.
/// </summary>
public static class PchtPlugins
{
    private static readonly string[] GroupOrder =
    {
        "GMST", "KYWD", "LCRT", "AACT", "TXST", "GLOB", "CLAS", "FACT", "HDPT", "HAIR", "EYES", "RACE", "SOUN", "ASPC", "MGEF", "SCPT", "LTEX",
        "ENCH", "SPEL", "SCRL", "ACTI", "TACT", "ARMO", "BOOK", "CONT", "DOOR", "INGR", "LIGH", "MISC", "APPA", "STAT", "SCOL", "MSTT", "PWAT",
        "GRAS", "TREE", "CLDC", "FLOR", "FURN", "WEAP", "AMMO", "NPC_", "LVLN", "KEYM", "ALCH", "IDLM", "COBJ", "PROJ", "HAZD", "SLGM", "LVLI",
        "WTHR", "CLMT", "SPGD", "RFCT", "REGN", "NAVI", "CELL", "WRLD", "DIAL", "QUST", "IDLE", "PACK", "CSTY", "LSCR", "LVSP", "ANIO", "WATR",
        "EFSH", "EXPL", "DEBR", "IMGS", "IMAD", "FLST", "PERK", "BPTD", "ADDN", "AVIF", "CAMS", "CPTH", "VTYP", "MATT", "IPCT", "IPDS", "ARMA",
        "ECZN", "LCTN", "MESG", "RGDL", "DOBJ", "LGTM", "MUSC", "FSTP", "FSTS", "SMBN", "SMQN", "SMEN", "DLBR", "MUST", "DLVW", "WOOP", "SHOU",
        "EQUP", "RELA", "SCEN", "ASTP", "OTFT", "ARTO", "MATO", "MOVT", "SNDR", "DUAL", "SNCT", "SOPM", "COLL", "CLFM", "REVB",
    };

    public static readonly HashSet<string> SourceSignatures = new() { "MGEF", "SPEL", "SOPM", "SNDC", "SNDR", "SOUN", "VTYP", "KYWD", "FLST" };

    /// <summary>Reads the installed base plugin with the records the pack plugins copy.</summary>
    public static PluginFile ReadSource(string mcmPath) => PluginFile.Read(mcmPath, SourceSignatures);

    /// <summary>True when the plugin is the Multi Voice Packs version of the base plugin (it has the voice set keywords and lists).</summary>
    public static bool IsMulti(PluginFile src) =>
        src.Records.Any(r => r.Signature == "KYWD" && r.EditorId == "PCHT_female_voiceset") && src.Records.Any(r => r.Signature == "FLST" && r.EditorId == "BYOVoiceSets");

    private static void CheckSource(PluginFile src)
    {
        if (!src.FileName.Equals(GruntCatalogs.PchtPlugin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The base plugin must be {GruntCatalogs.PchtPlugin}, not {src.FileName}.");
        // Copies keep every reference unchanged because the new plugin's masters are the base plugin's masters followed by
        // the base plugin itself: index 00 stays Skyrim.esm and 01 stays the base plugin.
        if (src.Masters.Count != 1 || !src.Masters[0].Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{src.FileName} has the masters {string.Join(", ", src.Masters)}; the PCHT versions this tool knows have only Skyrim.esm. Update DBVO Studio or report this version.");
        if (src.IsLocalized) throw new InvalidOperationException($"{src.FileName} is localized; PCHT's plugin never is, so this isn't a PCHT version this tool knows.");
    }

    // ------------------------------------------------------------------ standard pack: descriptor overrides

    /// <param name="files">Sound descriptor editor ID → the data-relative files it should list (e.g. data\Sound\FX\PC Head Tracking\Combat\Attack\01.wav).</param>
    public static PchtPlugin BuildOverride(PluginFile src, string fileName, IReadOnlyDictionary<string, List<string>> files, string description)
    {
        CheckSource(src);
        var records = new List<OutRecord>();
        foreach (var (edid, list) in files)
        {
            var sndr = src.Records.FirstOrDefault(r => r.Signature == "SNDR" && string.Equals(r.EditorId, edid, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"The sound descriptor {edid} isn't in {src.FileName}.");
            if (list.Count == 0) continue;
            records.Add(new OutRecord("SNDR", sndr.Flags & ~PluginRecord.FlagCompressed, sndr.FormId, sndr.HeaderTail, ReplaceFiles(sndr.Subrecords, list)));
        }
        if (records.Count == 0) throw new InvalidOperationException("No event has lines to list.");
        var masters = new List<string>(src.Masters) { src.FileName };
        var bytes = Write(masters, records, 0x800, description);
        return new PchtPlugin { FileName = fileName, Bytes = bytes, Masters = masters, Overrides = records.Count };
    }

    /// <summary>The descriptor's subrecords with its file list (ANAM) replaced, in the same place.</summary>
    private static List<Subrecord> ReplaceFiles(List<Subrecord> subs, List<string> files)
    {
        var outSubs = new List<Subrecord>();
        bool done = false;
        foreach (var s in subs)
        {
            if (s.Type == "ANAM")
            {
                if (done) continue;
                foreach (var f in files) outSubs.Add(new Subrecord("ANAM", FixPluginWriter.ZString(f)));
                done = true;
                continue;
            }
            // No file list yet (a descriptor PCHT left empty): add it before the output model, as the Creation Kit does.
            if (!done && s.Type is "ONAM" or "LNAM" or "BNAM")
            {
                foreach (var f in files) outSubs.Add(new Subrecord("ANAM", FixPluginWriter.ZString(f)));
                done = true;
            }
            outSubs.Add(new Subrecord(s.Type, (byte[])s.Data.Clone()));
        }
        if (!done) foreach (var f in files) outSubs.Add(new Subrecord("ANAM", FixPluginWriter.ZString(f)));
        return outSubs;
    }

    // ------------------------------------------------------------------ MPCHT voice set

    /// <param name="files">Sound descriptor editor ID → files in the prefix folder. Descriptors not listed keep their base files, moved into the prefix folder.</param>
    /// <param name="gender">female | male | both</param>
    public static PchtPlugin BuildVoiceSet(PluginFile src, string prefix, string fullName, string gender, IReadOnlyDictionary<string, List<string>> files, string description)
    {
        CheckSource(src);
        if (!IsMulti(src))
            throw new InvalidOperationException($"The {src.FileName} found is the plain PCHT one. A Multi Voice Packs voice set needs the version from \"Multi Voice Packs support for PC Head Tracking\" installed over it (it adds the voice set keywords and lists).");
        if (prefix.Length == 0 || !prefix.All(char.IsAsciiLetterOrDigit)) throw new InvalidOperationException("The prefix may only have letters and digits (no spaces or _).");
        if (fullName.Trim().Length == 0) throw new InvalidOperationException("Give the voice set a name for the MCM.");

        uint selfIndex = (uint)(src.Masters.Count + 1) << 24; // Skyrim.esm, the base plugin, then this file
        uint next = 0x800;
        var map = new Dictionary<uint, uint>();      // old FormID → new FormID
        var byEdid = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase); // old EDID → new FormID
        var outRecs = new List<OutRecord>();
        OutRecord Copy(PluginRecord r)
        {
            var subs = r.Subrecords.Select(s => new Subrecord(s.Type, (byte[])s.Data.Clone())).ToList();
            var edid = r.EditorId ?? $"{r.Signature}_{r.FormId:X8}";
            var e = subs.FirstOrDefault(s => s.Type == "EDID");
            if (e != null) e.Data = FixPluginWriter.ZString(prefix + edid);
            else subs.Insert(0, new Subrecord("EDID", FixPluginWriter.ZString(prefix + edid)));
            uint id = selfIndex | next++;
            map[r.FormId] = id;
            byEdid[edid] = id;
            var o = new OutRecord(r.Signature, r.Flags & ~PluginRecord.FlagCompressed, id, r.HeaderTail, subs);
            outRecs.Add(o);
            return o;
        }
        PluginRecord Need(string sig, string edid) => src.Records.FirstOrDefault(r => r.Signature == sig && r.EditorId == edid)
            ?? throw new InvalidOperationException($"{sig} {edid} isn't in {src.FileName}; this isn't a PCHT version this tool knows.");

        // Same order as the toolkit script, so the copies get the same new IDs it would give them.
        var mgef = Copy(Need("MGEF", "_MAGAF_VoiceEvents"));
        SetString(mgef.Subs, "FULL", fullName.Trim());
        var spel = Copy(Need("SPEL", "_MAGA_VoiceEvents"));
        SetString(spel.Subs, "FULL", fullName.Trim());
        foreach (var r in src.Records.Where(r => r.Signature == "SOPM")) Copy(r);
        foreach (var r in src.Records.Where(r => r.Signature == "SNDC")) Copy(r);
        var sndrs = src.Records.Where(r => r.Signature == "SNDR").Select(r => (Src: r, Out: Copy(r))).ToList();
        var souns = src.Records.Where(r => r.Signature == "SOUN").Select(Copy).ToList();
        var vtyps = src.Records.Where(r => r.Signature == "VTYP").Select(r => (Src: r, Out: Copy(r))).ToList();

        // Sound category parents (SNDC CNAM) point at the copies.
        foreach (var o in outRecs.Where(o => o.Signature == "SNDC")) Relink(o.Subs, "CNAM", map);
        // Sounds point at the copied descriptors.
        foreach (var o in souns) Relink(o.Subs, "SDSC", map);
        // Descriptor files: the pack's lines, or the base list moved into the prefix folder.
        foreach (var (s, o) in sndrs)
        {
            if (s.EditorId != null && files.TryGetValue(s.EditorId, out var list) && list.Count > 0)
                o.Subs = ReplaceFiles(o.Subs, list);
            else
                foreach (var a in o.Subs.Where(x => x.Type == "ANAM")) a.Data = FixPluginWriter.ZString(PrefixPath(PluginRecord.ZString(a.Data), prefix));
        }
        // The spell's first effect is the copied voice event effect; one gender keyword unless the set is for both.
        RelinkFirstEffect(spel.Subs, mgef.FormId);
        if (gender is "female" or "male")
        {
            var kw = Need("KYWD", gender == "female" ? "PCHT_female_voiceset" : "PCHT_male_voiceset");
            AddKeyword(spel.Subs, kw.FormId);
        }
        // The voice event script's properties point at the copied sounds.
        var vmad = mgef.Subs.FirstOrDefault(s => s.Type == "VMAD") ?? throw new InvalidOperationException("The voice event effect has no script; this isn't a PCHT version this tool knows.");
        vmad.Data = VmadRelink(vmad.Data, map);

        var female = vtyps.FirstOrDefault(v => (v.Src.EditorId ?? "").EndsWith("_FemaleBYO", StringComparison.Ordinal));
        var male = vtyps.FirstOrDefault(v => (v.Src.EditorId ?? "").EndsWith("_MaleBYO", StringComparison.Ordinal));
        if (female.Out == null || male.Out == null) throw new InvalidOperationException("The _FemaleBYO and _MaleBYO voice types aren't in the base plugin.");
        string vf = prefix + female.Src.EditorId, vm = prefix + male.Src.EditorId, sp = prefix + "_MAGA_VoiceEvents";

        var masters = new List<string>(src.Masters) { src.FileName };
        var bytes = Write(masters, outRecs, next, description);
        return new PchtPlugin
        {
            FileName = $"PCHTV_{prefix}.esp", Bytes = bytes, Masters = masters, NewRecords = outRecs.Count,
            FemaleVoiceType = vf, MaleVoiceType = vm,
            FlmLines = new List<string>
            {
                $"FormList=BYOVoiceSets|{sp}",
                $"FormList=BYOVoiceTypesM|{vm}",
                $"FormList=BYOVoiceTypesF|{vf}",
                $"FormList=_VoicePowerCustom|{vm}",
                $"FormList=_VoicePowerCustom|{vf}",
            },
        };
    }

    /// <summary>"data\Sound\FX\PC Head Tracking\Combat\Attack\01.wav" → "data\Sound\FX\PC Head Tracking\&lt;prefix&gt;\Combat\Attack\01.wav".</summary>
    public static string PrefixPath(string path, string prefix)
    {
        var p = path.Replace('/', '\\');
        const string marker = @"PC Head Tracking\";
        int i = p.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (i < 0) return p;
        if (p[(i + marker.Length)..].StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)) return p;
        return p[..(i + marker.Length)] + prefix + "\\" + p[(i + marker.Length)..];
    }

    private static void SetString(List<Subrecord> subs, string type, string text)
    {
        var s = subs.FirstOrDefault(x => x.Type == type);
        if (s != null) { s.Data = FixPluginWriter.ZString(text); return; }
        int at = subs.FindIndex(x => x.Type == "OBND") is var o && o >= 0 ? o + 1 : 1;
        subs.Insert(Math.Min(at, subs.Count), new Subrecord(type, FixPluginWriter.ZString(text)));
    }

    private static void Relink(List<Subrecord> subs, string type, Dictionary<uint, uint> map)
    {
        foreach (var s in subs.Where(x => x.Type == type && x.Data.Length == 4))
            if (map.TryGetValue(BitConverter.ToUInt32(s.Data, 0), out var n)) s.Data = BitConverter.GetBytes(n);
    }

    private static void RelinkFirstEffect(List<Subrecord> subs, uint mgef)
    {
        var e = subs.FirstOrDefault(x => x.Type == "EFID") ?? throw new InvalidOperationException("The voice event spell has no effects.");
        e.Data = BitConverter.GetBytes(mgef);
    }

    private static void AddKeyword(List<Subrecord> subs, uint keyword)
    {
        var kwda = subs.FirstOrDefault(x => x.Type == "KWDA");
        if (kwda != null)
        {
            var ids = Enumerable.Range(0, kwda.Data.Length / 4).Select(i => BitConverter.ToUInt32(kwda.Data, i * 4)).ToList();
            if (ids.Contains(keyword)) return;
            ids.Add(keyword);
            kwda.Data = ids.SelectMany(BitConverter.GetBytes).ToArray();
            var ksiz = subs.First(x => x.Type == "KSIZ");
            ksiz.Data = BitConverter.GetBytes((uint)ids.Count);
            return;
        }
        // SPEL layout: EDID, OBND, FULL, KSIZ, KWDA, MDOB, ETYP, DESC, SPIT, effects.
        int at = subs.FindIndex(x => x.Type is "MDOB" or "ETYP" or "DESC" or "SPIT");
        if (at < 0) at = subs.Count;
        subs.Insert(at, new Subrecord("KWDA", BitConverter.GetBytes(keyword)));
        subs.Insert(at, new Subrecord("KSIZ", BitConverter.GetBytes(1u)));
    }

    // ------------------------------------------------------------------ VMAD (script properties)

    /// <summary>The script data with every object property that points at a copied record pointed at the copy instead.</summary>
    public static byte[] VmadRelink(byte[] vmad, IReadOnlyDictionary<uint, uint> map)
    {
        var d = (byte[])vmad.Clone();
        foreach (var off in VmadObjectOffsets(d))
        {
            var id = BitConverter.ToUInt32(d, off);
            if (map.TryGetValue(id, out var n)) BitConverter.GetBytes(n).CopyTo(d, off);
        }
        return d;
    }

    /// <summary>Byte offsets of the FormIDs of every object property (and object array element) in a VMAD subrecord.</summary>
    public static List<int> VmadObjectOffsets(byte[] d)
    {
        var offs = new List<int>();
        short objFormat = BitConverter.ToInt16(d, 2);
        int count = BitConverter.ToUInt16(d, 4);
        int o = 6;
        int Str() { int n = BitConverter.ToUInt16(d, o); o += 2 + n; return n; }
        void Value(int type)
        {
            switch (type)
            {
                case 1: offs.Add(objFormat == 2 ? o + 4 : o); o += 8; break;
                case 2: Str(); break;
                case 3: case 4: o += 4; break;
                case 5: o += 1; break;
                default: throw new InvalidDataException($"Unknown script property type {type}.");
            }
        }
        for (int i = 0; i < count; i++)
        {
            Str();
            o += 1; // flags
            int props = BitConverter.ToUInt16(d, o); o += 2;
            for (int j = 0; j < props; j++)
            {
                Str();
                int type = d[o]; o += 2; // type, status
                if (type <= 5) Value(type);
                else if (type is >= 11 and <= 15)
                {
                    int n = (int)BitConverter.ToUInt32(d, o); o += 4;
                    for (int k = 0; k < n; k++) Value(type - 10);
                }
                else throw new InvalidDataException($"Unknown script property type {type}.");
            }
        }
        return offs;
    }

    // ------------------------------------------------------------------ writing

    private sealed class OutRecord
    {
        public OutRecord(string sig, uint flags, uint formId, byte[] tail, List<Subrecord> subs) { Signature = sig; Flags = flags; FormId = formId; Tail = tail; Subs = subs; }
        public string Signature { get; }
        public uint Flags { get; }
        public uint FormId { get; }
        public byte[] Tail { get; }
        public List<Subrecord> Subs { get; set; }
    }

    private static byte[] Write(List<string> masters, List<OutRecord> records, uint nextObjectId, string description)
    {
        using var body = new MemoryStream();
        int count = 0;
        foreach (var g in records.GroupBy(r => r.Signature).OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var i && i >= 0 ? i : 999))
        {
            using var grp = new MemoryStream();
            foreach (var r in g)
            {
                using var data = new MemoryStream();
                Subrecord.Write(data, r.Subs);
                var b = data.ToArray();
                Ascii(grp, r.Signature);
                grp.Write(BitConverter.GetBytes((uint)b.Length));
                grp.Write(BitConverter.GetBytes(r.Flags));
                grp.Write(BitConverter.GetBytes(r.FormId));
                grp.Write(r.Tail.Length == 8 ? r.Tail : new byte[] { 0, 0, 0, 0, 44, 0, 0, 0 });
                grp.Write(b);
                count++;
            }
            var gb = grp.ToArray();
            Ascii(body, "GRUP");
            body.Write(BitConverter.GetBytes((uint)(24 + gb.Length)));
            Ascii(body, g.Key);
            body.Write(BitConverter.GetBytes(0));
            body.Write(new byte[8]);
            body.Write(gb);
            count++;
        }
        var hsubs = new List<Subrecord>();
        var hedr = new byte[12];
        BitConverter.GetBytes(1.71f).CopyTo(hedr, 0);
        BitConverter.GetBytes((uint)count).CopyTo(hedr, 4);
        BitConverter.GetBytes(nextObjectId).CopyTo(hedr, 8);
        hsubs.Add(new Subrecord("HEDR", hedr));
        hsubs.Add(new Subrecord("CNAM", FixPluginWriter.ZString("DBVO Studio")));
        if (description.Length > 0) hsubs.Add(new Subrecord("SNAM", FixPluginWriter.ZString(description)));
        foreach (var m in masters)
        {
            hsubs.Add(new Subrecord("MAST", FixPluginWriter.ZString(m)));
            hsubs.Add(new Subrecord("DATA", new byte[8]));
        }
        using var hd = new MemoryStream();
        Subrecord.Write(hd, hsubs);
        var h = hd.ToArray();
        using var outS = new MemoryStream();
        Ascii(outS, "TES4");
        outS.Write(BitConverter.GetBytes((uint)h.Length));
        outS.Write(BitConverter.GetBytes(PluginFile.HeaderFlagLight));
        outS.Write(BitConverter.GetBytes(0u));
        outS.Write(new byte[4]);
        outS.Write(BitConverter.GetBytes((ushort)44));
        outS.Write(new byte[2]);
        outS.Write(h);
        body.Position = 0;
        body.CopyTo(outS);
        return outS.ToArray();
    }

    private static void Ascii(Stream s, string t) { foreach (char c in t) s.WriteByte((byte)c); }

    // ------------------------------------------------------------------ checks

    /// <summary>
    /// Reads a written plugin back and checks it: masters, ESL limits, unique IDs, every reference to a record of the plugin
    /// itself resolving, sounds pointing at descriptors, descriptors listing files. Returns the problems found (none = valid).
    /// </summary>
    public static List<string> Validate(byte[] bytes, string fileName, PluginFile source)
    {
        var problems = new List<string>();
        var tmp = Path.Combine(Path.GetTempPath(), "dbvo-check-" + Guid.NewGuid().ToString("N") + ".esp");
        try
        {
            File.WriteAllBytes(tmp, bytes);
            var all = new HashSet<string>(GroupOrder);
            var pf = PluginFile.Read(tmp, all);
            if (!pf.IsLight) problems.Add("The plugin isn't ESL-flagged.");
            var want = new List<string>(source.Masters) { source.FileName };
            if (!pf.Masters.SequenceEqual(want, StringComparer.OrdinalIgnoreCase)) problems.Add($"Masters are {string.Join(", ", pf.Masters)}, expected {string.Join(", ", want)}.");
            uint self = (uint)pf.Masters.Count;
            var ids = new HashSet<uint>();
            var srcIds = new HashSet<uint>(source.Records.Select(r => r.FormId));
            foreach (var r in pf.Records)
            {
                if (!ids.Add(r.FormId)) problems.Add($"FormID {r.FormId:X8} is used twice.");
                uint idx = r.FormId >> 24;
                if (idx == self)
                {
                    uint obj = r.FormId & 0xFFFFFF;
                    if (obj < 0x800 || obj > 0xFFF) problems.Add($"{r.Signature} {r.EditorId}: object ID {obj:X} is outside the ESL range 800–FFF.");
                }
                else if (idx == self - 1 && !srcIds.Contains(r.FormId)) problems.Add($"{r.Signature} {r.FormId:X8} overrides a record that isn't in {source.FileName}.");
                else if (idx > self) problems.Add($"{r.Signature} {r.FormId:X8} has an invalid load order index.");
                if (r.Signature == "SNDR" && !r.Subrecords.Any(s => s.Type == "ANAM")) problems.Add($"Sound descriptor {r.EditorId} lists no files.");
                if (r.Signature == "SOUN" && r.Get("SDSC") is { Data.Length: 4 } sd)
                {
                    uint target = BitConverter.ToUInt32(sd.Data, 0);
                    if (target >> 24 == self && !pf.Records.Any(x => x.FormId == target && x.Signature == "SNDR")) problems.Add($"Sound {r.EditorId} points at a missing descriptor.");
                }
                if (r.Signature == "MGEF" && r.Get("VMAD") is { } vm)
                {
                    try
                    {
                        foreach (var off in VmadObjectOffsets(vm.Data))
                        {
                            uint t = BitConverter.ToUInt32(vm.Data, off);
                            if (t >> 24 == self && !pf.Records.Any(x => x.FormId == t)) problems.Add($"Script property of {r.EditorId} points at a missing record {t:X8}.");
                        }
                    }
                    catch (InvalidDataException ex) { problems.Add($"Script data of {r.EditorId} can't be read: {ex.Message}"); }
                }
            }
            if (pf.Records.Count(r => (r.FormId >> 24) == self) > 0xFFF - 0x800 + 1) problems.Add("Too many new records for an ESL-flagged plugin.");
        }
        catch (Exception ex) { problems.Add($"{fileName} can't be read back: {ex.Message}"); }
        finally { try { File.Delete(tmp); } catch { } }
        return problems;
    }
}
