using System.Text;
using DbvoStudio.Core.Plugins;

namespace DbvoStudio.Core.Revoice;

/// <summary>One condition (CTDA). Parameters are resolved to FormKeys where the function takes a form.</summary>
public sealed class Condition
{
    public int Function { get; init; }
    /// <summary>0 ==, 1 !=, 2 &gt;, 3 &gt;=, 4 &lt;, 5 &lt;=</summary>
    public int Op { get; init; }
    public float Value { get; init; }
    public bool Or { get; init; }
    /// <summary>0 subject, 1 target, 2 reference, 3 combat target, 4 linked ref, 5 quest alias, 6 package data, 7 event data.</summary>
    public int RunOn { get; init; }
    public uint Param1Raw { get; init; }
    public FormKey? Param1 { get; init; }

    public const int GetIsClass = 68, GetIsRace = 69, GetIsSex = 70, GetInFaction = 71, GetIsID = 72, GetIsReference = 136,
        GetIsVoiceType = 426, GetIsAliasRef = 566;

    /// <summary>True when the condition asks "is it X" (== 1 / != 0); false for "is not X".</summary>
    public bool Positive => (Op == 0 && Value >= 1) || (Op == 1 && Value < 1) || (Op == 3 && Value <= 1 && Value > 0) || (Op == 2 && Value < 1);

    public static Condition Parse(PluginFile file, byte[] d)
    {
        int fn = BitConverter.ToUInt16(d, 8);
        uint p1 = BitConverter.ToUInt32(d, 12);
        bool takesForm = fn is GetIsClass or GetIsRace or GetInFaction or GetIsID or GetIsReference or GetIsVoiceType;
        return new Condition
        {
            Op = d[0] >> 5,
            Or = (d[0] & 1) != 0,
            Value = BitConverter.ToSingle(d, 4),
            Function = fn,
            Param1Raw = p1,
            Param1 = takesForm && p1 != 0 ? FormKey.Resolve(file, p1) : null,
            RunOn = d.Length >= 24 ? (int)BitConverter.ToUInt32(d, 20) : 0,
        };
    }
}

public sealed class VoiceTypeInfo
{
    public FormKey Key { get; init; }
    public string EditorId { get; init; } = "";
    public byte Flags { get; init; }
    public bool Female => (Flags & 2) != 0;
}

public sealed class NpcInfo
{
    public FormKey Key { get; init; }
    public string EditorId { get; init; } = "";
    public string Name { get; init; } = "";
    public FormKey? VoiceType { get; init; }
    public FormKey? Template { get; init; }
    public bool UseTraits { get; init; }
    public bool Female { get; init; }
    public FormKey? Race { get; init; }
    public FormKey? Class { get; init; }
    public List<FormKey> Factions { get; init; } = new();
    /// <summary>The plugin whose version of the record won (last in the load order).</summary>
    public string WinningPlugin { get; init; } = "";
    public string Display => Name.Length > 0 ? Name : EditorId;
}

public sealed class AliasInfo
{
    public int Id { get; init; }
    public string Name { get; set; } = "";
    public FormKey? ForcedRef { get; set; }
    public FormKey? UniqueActor { get; set; }
    public FormKey? CreateObject { get; set; }
    public FormKey? ExternalQuest { get; set; }
    public int ExternalAlias { get; set; } = -1;
    public FormKey? VoiceTypes { get; set; }
    public List<Condition> Conditions { get; } = new();
}

public sealed class QuestInfo
{
    public FormKey Key { get; init; }
    public string EditorId { get; init; } = "";
    public List<Condition> DialogueConditions { get; } = new();
    public Dictionary<int, AliasInfo> Aliases { get; } = new();
}

public sealed class TopicInfo
{
    public FormKey Key { get; init; }
    public string EditorId { get; init; } = "";
    public FormKey? Quest { get; init; }
    public int Category { get; init; }
    public string Subtype { get; init; } = "";
}

public sealed class ResponseInfo
{
    public int Number { get; init; }
    public string Text { get; init; } = "";
    public int Emotion { get; init; }
    public int EmotionValue { get; init; }
    public string Notes { get; init; } = "";

    public static readonly string[] EmotionNames = { "Neutral", "Anger", "Disgust", "Fear", "Sad", "Happy", "Surprise", "Puzzled" };
    public string EmotionName => Emotion >= 0 && Emotion < EmotionNames.Length ? EmotionNames[Emotion] : "Neutral";
}

public sealed class InfoRecord
{
    public FormKey Key { get; init; }
    public FormKey Topic { get; init; }
    public FormKey? Speaker { get; init; }
    public FormKey? SharedInfo { get; init; }
    public List<Condition> Conditions { get; init; } = new();
    public List<ResponseInfo> Responses { get; init; } = new();
    public string WinningPlugin { get; init; } = "";
    public bool HasNoLip { get; init; }
}

public sealed class SceneInfo
{
    public FormKey Key { get; init; }
    public FormKey? Quest { get; init; }
    /// <summary>(topic, alias id) for each dialogue action.</summary>
    public List<(FormKey Topic, int Alias)> DialogueActions { get; } = new();
}

public enum SpeakerCertainty { Exact, Probable, Unknown }

/// <summary>Who can say a line: the voice types (decides the folder) and, where known, the NPCs.</summary>
public sealed class SpeakerResult
{
    public HashSet<FormKey> VoiceTypes { get; } = new();
    public HashSet<FormKey> Npcs { get; } = new();
    public SpeakerCertainty Certainty { get; set; } = SpeakerCertainty.Unknown;
    public string Via { get; set; } = "";
}

/// <summary>
/// Everything about dialogue that voice files depend on, read from a load order of plugins: lines and their
/// responses, topics, quests and aliases, scenes, NPCs, voice types, form lists, factions.
/// Speaker resolution follows xEdit's "Export dialogues" script (speaker, GetIsID first, voice types and form lists,
/// quest aliases with forced reference / unique actor / created object / external alias / fill conditions,
/// alias voice-type limits, quest dialogue conditions, scene actions), with proper AND/OR evaluation added.
/// </summary>
public sealed class DialogueIndex
{
    public static readonly HashSet<string> Signatures = new() { "DIAL", "INFO", "QUST", "NPC_", "VTYP", "FLST", "LVLN", "TACT", "SCEN", "ACHR", "FACT", "CLAS" };

    public List<string> LoadOrder { get; } = new();
    /// <summary>Plugin file name as spelled in the load order, by lower-case name.</summary>
    public Dictionary<string, string> Spelling { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<FormKey, InfoRecord> Infos { get; } = new();
    public Dictionary<FormKey, TopicInfo> Topics { get; } = new();
    public Dictionary<FormKey, QuestInfo> Quests { get; } = new();
    public Dictionary<FormKey, NpcInfo> Npcs { get; } = new();
    public Dictionary<FormKey, VoiceTypeInfo> VoiceTypes { get; } = new();
    public Dictionary<FormKey, List<FormKey>> FormLists { get; } = new();
    public Dictionary<FormKey, string> ListNames { get; } = new();
    public Dictionary<FormKey, List<FormKey>> LeveledNpcs { get; } = new();
    public Dictionary<FormKey, FormKey> TalkingActivators { get; } = new();
    public Dictionary<FormKey, FormKey> PlacedActors { get; } = new();
    public Dictionary<FormKey, SceneInfo> Scenes { get; } = new();
    public HashSet<FormKey> Factions { get; } = new();
    public HashSet<FormKey> Classes { get; } = new();
    /// <summary>Plugins named as masters that were not loaded.</summary>
    public HashSet<string> MissingMasters { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Plugins that are localized but have no strings: their text could not be read.</summary>
    public List<string> MissingStrings { get; } = new();

    private Dictionary<FormKey, List<FormKey>>? _factionMembers, _classMembers;
    private Dictionary<FormKey, List<(FormKey Quest, int Alias)>>? _scenesByTopic;

    /// <summary>What-if: NPCs given another voice type (as SkyPatcher would).</summary>
    public Dictionary<FormKey, FormKey> NpcVoiceOverride { get; } = new();
    /// <summary>What-if: extra members added to form lists (as FormList Manipulator would).</summary>
    public Dictionary<FormKey, HashSet<FormKey>> ExtraListMembers { get; } = new();

    /// <summary>Kind of record each form is, from the winning version.</summary>
    public Dictionary<FormKey, string> Kinds { get; } = new();

    /// <summary>
    /// Add plugins in load order (masters first). Later plugins override earlier ones record by record.
    /// <paramref name="strings"/> gives localized strings for a plugin (from Strings folders or BSAs).
    /// </summary>
    public static DialogueIndex Build(IEnumerable<PluginFile> loadOrder, Func<PluginFile, StringsTable?>? strings = null)
    {
        var idx = new DialogueIndex();
        var files = loadOrder.ToList();
        foreach (var f in files) { idx.LoadOrder.Add(f.FileName); idx.Spelling[f.FileName] = f.FileName; }
        foreach (var f in files)
        {
            foreach (var m in f.Masters) if (!idx.Spelling.ContainsKey(m)) idx.MissingMasters.Add(m);
            if (f.IsLocalized && f.Strings == null)
            {
                var st = strings?.Invoke(f);
                if (st != null) f.AttachStrings(st); else idx.MissingStrings.Add(f.FileName);
            }
            idx.Add(f);
        }
        return idx;
    }

    private void Add(PluginFile f)
    {
        string Text(byte[]? d) => d == null ? "" : f.DecodeLString(d) ?? "";
        static string Z(byte[]? d) => d == null ? "" : PluginRecord.ZString(d);
        FormKey? Ref(byte[]? d, int offset = 0)
        {
            if (d == null || d.Length < offset + 4) return null;
            uint raw = BitConverter.ToUInt32(d, offset);
            return raw == 0 ? null : FormKey.Resolve(f, raw);
        }

        foreach (var r in f.Records)
        {
            var key = FormKey.Resolve(f, r.FormId);
            if (r.IsDeleted) continue;
            Kinds[key] = r.Signature;
            switch (r.Signature)
            {
                case "VTYP":
                    VoiceTypes[key] = new VoiceTypeInfo { Key = key, EditorId = r.EditorId ?? "", Flags = r.Get("DNAM")?.Data is { Length: > 0 } dn ? dn[0] : (byte)0 };
                    break;
                case "NPC_":
                {
                    var acbs = r.Get("ACBS")?.Data;
                    uint flags = acbs != null && acbs.Length >= 4 ? BitConverter.ToUInt32(acbs, 0) : 0;
                    ushort tflags = acbs != null && acbs.Length >= 20 ? BitConverter.ToUInt16(acbs, 18) : (ushort)0;
                    var factions = r.Subrecords.Where(s => s.Type == "SNAM" && s.Data.Length >= 4).Select(s => FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0))).ToList();
                    Npcs[key] = new NpcInfo
                    {
                        Key = key, EditorId = r.EditorId ?? "", Name = Text(r.Get("FULL")?.Data),
                        VoiceType = Ref(r.Get("VTCK")?.Data), Template = Ref(r.Get("TPLT")?.Data), UseTraits = (tflags & 1) != 0,
                        Female = (flags & 1) != 0, Race = Ref(r.Get("RNAM")?.Data), Class = Ref(r.Get("CNAM")?.Data),
                        Factions = factions, WinningPlugin = f.FileName,
                    };
                    break;
                }
                case "FLST":
                    ListNames[key] = r.EditorId ?? "";
                    FormLists[key] = r.Subrecords.Where(s => s.Type == "LNAM" && s.Data.Length >= 4).Select(s => FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0))).ToList();
                    break;
                case "LVLN":
                    LeveledNpcs[key] = r.Subrecords.Where(s => s.Type == "LVLO" && s.Data.Length >= 8).Select(s => FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 4))).ToList();
                    break;
                case "TACT":
                    if (Ref(r.Get("VNAM")?.Data) is { } tv) TalkingActivators[key] = tv;
                    break;
                case "ACHR":
                    if (Ref(r.Get("NAME")?.Data) is { } baseObj) PlacedActors[key] = baseObj;
                    break;
                case "FACT": Factions.Add(key); break;
                case "CLAS": Classes.Add(key); break;
                case "DIAL":
                {
                    var data = r.Get("DATA")?.Data;
                    Topics[key] = new TopicInfo
                    {
                        Key = key, EditorId = r.EditorId ?? "", Quest = Ref(r.Get("QNAM")?.Data),
                        Category = data != null && data.Length >= 2 ? data[1] : 0, Subtype = Z(r.Get("SNAM")?.Data),
                    };
                    break;
                }
                case "QUST":
                    Quests[key] = ParseQuest(f, r, key);
                    break;
                case "SCEN":
                    Scenes[key] = ParseScene(f, r, key);
                    break;
                case "INFO":
                {
                    if (r.ParentTopic is not { } parentRaw) break;
                    var responses = new List<ResponseInfo>();
                    ResponseInfo? cur = null;
                    int emotion = 0, value = 0, number = 0;
                    string text = "", notes = "";
                    void Flush()
                    {
                        if (cur == null) return;
                        responses.Add(new ResponseInfo { Number = number, Emotion = emotion, EmotionValue = value, Text = text, Notes = notes });
                        cur = null;
                    }
                    var conds = new List<Condition>();
                    foreach (var s in r.Subrecords)
                    {
                        switch (s.Type)
                        {
                            case "TRDT":
                                Flush();
                                cur = new ResponseInfo();
                                emotion = s.Data.Length >= 4 ? (int)BitConverter.ToUInt32(s.Data, 0) : 0;
                                value = s.Data.Length >= 8 ? (int)BitConverter.ToUInt32(s.Data, 4) : 0;
                                number = s.Data.Length >= 13 ? s.Data[12] : responses.Count + 1;
                                text = ""; notes = "";
                                break;
                            case "NAM1": if (cur != null) text = Text(s.Data); break;
                            case "NAM2": if (cur != null) notes = Z(s.Data); break;
                            case "CTDA": if (s.Data.Length >= 24) conds.Add(Condition.Parse(f, s.Data)); break;
                        }
                    }
                    Flush();
                    var enam = r.Get("ENAM")?.Data;
                    Infos[key] = new InfoRecord
                    {
                        Key = key, Topic = FormKey.Resolve(f, parentRaw), Speaker = Ref(r.Get("ANAM")?.Data), SharedInfo = Ref(r.Get("DNAM")?.Data),
                        Conditions = conds, Responses = responses, WinningPlugin = f.FileName,
                        HasNoLip = enam != null && enam.Length >= 2 && (BitConverter.ToUInt16(enam, 0) & 0x800) != 0,
                    };
                    break;
                }
            }
        }
        _factionMembers = null; _classMembers = null; _scenesByTopic = null;
    }

    private static QuestInfo ParseQuest(PluginFile f, PluginRecord r, FormKey key)
    {
        var q = new QuestInfo { Key = key, EditorId = r.EditorId ?? "" };
        bool beforeNext = true;
        AliasInfo? alias = null;
        foreach (var s in r.Subrecords)
        {
            switch (s.Type)
            {
                case "NEXT": beforeNext = false; break;
                case "ALST":
                case "ALLS":
                    alias = s.Type == "ALST" && s.Data.Length >= 4 ? new AliasInfo { Id = (int)BitConverter.ToUInt32(s.Data, 0) } : null;
                    if (alias != null) q.Aliases[alias.Id] = alias;
                    if (s.Type == "ALLS") alias = null;
                    break;
                case "ALED": alias = null; break;
                case "ALID": if (alias != null) alias.Name = PluginRecord.ZString(s.Data); break;
                case "ALFR": if (alias != null && s.Data.Length >= 4) alias.ForcedRef = FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0)); break;
                case "ALUA": if (alias != null && s.Data.Length >= 4) alias.UniqueActor = FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0)); break;
                case "ALCO": if (alias != null && s.Data.Length >= 4) alias.CreateObject = FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0)); break;
                case "ALEQ": if (alias != null && s.Data.Length >= 4) alias.ExternalQuest = FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0)); break;
                case "ALEA": if (alias != null && s.Data.Length >= 4) alias.ExternalAlias = (int)BitConverter.ToUInt32(s.Data, 0); break;
                case "VTCK": if (alias != null && s.Data.Length >= 4 && BitConverter.ToUInt32(s.Data, 0) != 0) alias.VoiceTypes = FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0)); break;
                case "CTDA":
                    if (s.Data.Length < 24) break;
                    if (alias != null) alias.Conditions.Add(Condition.Parse(f, s.Data));
                    else if (beforeNext && q.Aliases.Count == 0) q.DialogueConditions.Add(Condition.Parse(f, s.Data));
                    break;
            }
        }
        return q;
    }

    private static SceneInfo ParseScene(PluginFile f, PluginRecord r, FormKey key)
    {
        FormKey? quest = null;
        var sc = new SceneInfo { Key = key };
        bool inAction = false;
        int aliasId = -1;
        FormKey? topic = null;
        ushort type = 0;
        foreach (var s in r.Subrecords)
        {
            if (s.Type == "PNAM" && s.Data.Length >= 4 && !inAction) quest = FormKey.Resolve(f, BitConverter.ToUInt32(s.Data, 0));
            if (s.Type == "ANAM")
            {
                if (!inAction && s.Data.Length >= 2) { inAction = true; type = BitConverter.ToUInt16(s.Data, 0); aliasId = -1; topic = null; }
                else
                {
                    if (type == 0 && topic != null && aliasId >= 0) sc.DialogueActions.Add((topic.Value, aliasId));
                    inAction = false;
                }
                continue;
            }
            if (!inAction) continue;
            if (s.Type == "ALID" && s.Data.Length >= 4) aliasId = (int)BitConverter.ToUInt32(s.Data, 0);
            if (s.Type == "DATA" && s.Data.Length >= 4 && type == 0) { uint raw = BitConverter.ToUInt32(s.Data, 0); if (raw != 0) topic = FormKey.Resolve(f, raw); }
        }
        return new SceneInfo { Key = key, Quest = quest }.With(sc.DialogueActions);
    }

    // ---------------------------------------------------------------- voice file names

    /// <summary>
    /// The engine's voice file name (without extension), from xEdit's InfoFileName: quest and topic editor IDs are
    /// shortened when together longer than 25 characters (quest to 10, topic to 15; or topic to 25 minus a short quest),
    /// then the 8-digit FormID without the plugin index, then the response number.
    /// </summary>
    public static string FileName(string questId, string topicId, uint infoId, int response)
    {
        int ql = questId.Length, dl = topicId.Length;
        if (ql + dl > 25)
        {
            if (ql > 10) { ql = 10; dl = 15; }
            else dl = 25 - ql;
        }
        return $"{questId[..Math.Min(ql, questId.Length)]}_{topicId[..Math.Min(dl, topicId.Length)]}_{infoId & 0xFFFFFF:X8}_{response}";
    }

    /// <summary>File name of one response, or null when the topic or its quest is unknown (missing master).</summary>
    public string? FileName(InfoRecord info, int response)
    {
        if (!Topics.TryGetValue(info.Topic, out var t)) return null;
        string q = "";
        if (t.Quest is { } qk)
        {
            if (!Quests.TryGetValue(qk, out var quest)) return null;
            q = quest.EditorId;
        }
        return FileName(q, t.EditorId, info.Key.Id, response);
    }

    /// <summary>The folder under Sound\Voice: the plugin that defines the line (checked against Hearthfire's own voice files).</summary>
    public string PluginFolder(InfoRecord info) => Spelling.TryGetValue(info.Key.Plugin, out var s) ? s : info.Key.Plugin;

    public string VoiceTypeFolder(FormKey vt) => VoiceTypes.TryGetValue(vt, out var v) && v.EditorId.Length > 0 ? v.EditorId : vt.ToString();

    // ---------------------------------------------------------------- speaker resolution

    private sealed class Guard { public readonly HashSet<FormKey> Seen = new(); }

    /// <summary>Voice types a form can speak with: VTYP itself, NPC (or its template with Use Traits), leveled list, form list, talking activator, placed actor, faction or class members.</summary>
    public HashSet<FormKey> VoiceTypesOf(FormKey k) { var r = new HashSet<FormKey>(); VoiceTypesOf(k, r, new Guard()); return r; }

    private void VoiceTypesOf(FormKey k, HashSet<FormKey> into, Guard g)
    {
        if (!g.Seen.Add(k)) return;
        if (VoiceTypes.ContainsKey(k)) { into.Add(k); return; }
        if (Npcs.TryGetValue(k, out var n))
        {
            if (NpcVoiceOverride.TryGetValue(k, out var ov)) { VoiceTypesOf(ov, into, g); return; }
            if (n.UseTraits && n.Template is { } t) VoiceTypesOf(t, into, g);
            else if (n.VoiceType is { } v) VoiceTypesOf(v, into, g);
            else if (n.Template is { } t2) VoiceTypesOf(t2, into, g);
            return;
        }
        if (LeveledNpcs.TryGetValue(k, out var lv)) { foreach (var e in lv) VoiceTypesOf(e, into, g); return; }
        if (FormLists.TryGetValue(k, out var fl) || ExtraListMembers.ContainsKey(k))
        {
            if (fl != null) foreach (var e in fl) VoiceTypesOf(e, into, g);
            if (ExtraListMembers.TryGetValue(k, out var extra)) foreach (var e in extra) VoiceTypesOf(e, into, g);
            return;
        }
        if (TalkingActivators.TryGetValue(k, out var tv)) { VoiceTypesOf(tv, into, g); return; }
        if (PlacedActors.TryGetValue(k, out var b)) { VoiceTypesOf(b, into, g); return; }
        if (Factions.Contains(k)) { foreach (var m in FactionMembers(k)) VoiceTypesOf(m, into, g); return; }
        if (Classes.Contains(k)) { foreach (var m in ClassMembers(k)) VoiceTypesOf(m, into, g); }
    }

    /// <summary>NPCs a form stands for (for showing who speaks).</summary>
    public HashSet<FormKey> NpcsOf(FormKey k) { var r = new HashSet<FormKey>(); NpcsOf(k, r, new Guard()); return r; }

    private void NpcsOf(FormKey k, HashSet<FormKey> into, Guard g)
    {
        if (!g.Seen.Add(k)) return;
        if (Npcs.ContainsKey(k)) { into.Add(k); return; }
        if (LeveledNpcs.TryGetValue(k, out var lv)) { foreach (var e in lv) NpcsOf(e, into, g); return; }
        if (FormLists.TryGetValue(k, out var fl)) { foreach (var e in fl) NpcsOf(e, into, g); return; }
        if (PlacedActors.TryGetValue(k, out var b)) { NpcsOf(b, into, g); return; }
        if (Factions.Contains(k)) { foreach (var m in FactionMembers(k)) into.Add(m); }
    }

    public IReadOnlyList<FormKey> FactionMembers(FormKey faction)
    {
        _factionMembers ??= Npcs.Values.SelectMany(n => n.Factions.Select(fk => (fk, n.Key))).GroupBy(x => x.fk).ToDictionary(x => x.Key, x => x.Select(y => y.Key).ToList());
        return _factionMembers.TryGetValue(faction, out var l) ? l : Array.Empty<FormKey>();
    }

    private IReadOnlyList<FormKey> ClassMembers(FormKey cls)
    {
        _classMembers ??= Npcs.Values.Where(n => n.Class != null).GroupBy(n => n.Class!.Value).ToDictionary(x => x.Key, x => x.Select(n => n.Key).ToList());
        return _classMembers.TryGetValue(cls, out var l) ? l : Array.Empty<FormKey>();
    }

    /// <summary>A constraint from conditions: null = says nothing about the speaker.</summary>
    private sealed class Constraint
    {
        public HashSet<FormKey>? Vt;
        public HashSet<FormKey>? Npc;
        public HashSet<FormKey> ExcludeVt = new();
        public HashSet<FormKey> ExcludeNpc = new();
        public bool Female, Male;
        public string Via = "";
    }

    /// <summary>
    /// Evaluate a condition list. Skyrim binds OR tighter than AND: (a OR b) AND c. Each OR-group's speaker sets are
    /// united; if any member of a group says nothing about the speaker, the group says nothing. Groups are intersected.
    /// </summary>
    private Constraint Evaluate(IReadOnlyList<Condition> conds, FormKey? quest, Guard g)
    {
        var result = new Constraint();
        var group = new List<Condition>();
        void Close()
        {
            if (group.Count == 0) return;
            HashSet<FormKey>? vt = new(), npc = new();
            bool unconstrained = false;
            string via = "";
            foreach (var c in group)
            {
                var (cv, cn, cvia) = One(c, quest, g);
                if (cv == null) { unconstrained = true; continue; }
                if (!c.Positive)
                {
                    // "is not X": only meaningful on its own (an AND condition).
                    if (group.Count == 1) { result.ExcludeVt.UnionWith(cn.Count > 0 ? new HashSet<FormKey>() : cv); result.ExcludeNpc.UnionWith(cn); }
                    unconstrained = true;
                    continue;
                }
                vt.UnionWith(cv);
                npc.UnionWith(cn);
                via = cvia;
            }
            group.Clear();
            if (unconstrained) return;
            result.Vt = result.Vt == null ? vt : new HashSet<FormKey>(result.Vt.Intersect(vt));
            result.Npc = result.Npc == null ? npc : (npc.Count == 0 ? result.Npc : new HashSet<FormKey>(result.Npc.Count == 0 ? npc : result.Npc.Intersect(npc)));
            if (via.Length > 0) result.Via = result.Via.Length == 0 ? via : result.Via + " + " + via;
        }
        foreach (var c in conds)
        {
            if (c.RunOn != 0) { if (!c.Or) Close(); continue; }
            if (c.Function == Condition.GetIsSex && c.Positive) { if (c.Param1Raw == 1) result.Female = true; else result.Male = true; }
            group.Add(c);
            if (!c.Or) Close();
        }
        Close();
        return result;
    }

    /// <summary>One condition's speakers: (voice types, npcs, how) or (null, ...) when it does not constrain the speaker.</summary>
    private (HashSet<FormKey>? Vt, HashSet<FormKey> Npc, string Via) One(Condition c, FormKey? quest, Guard g)
    {
        var none = new HashSet<FormKey>();
        switch (c.Function)
        {
            case Condition.GetIsID when c.Param1 is { } id:
                return (VoiceTypesOf(id), NpcsOf(id), "GetIsID");
            case Condition.GetIsReference when c.Param1 is { } rf:
                return (VoiceTypesOf(rf), NpcsOf(rf), "GetIsReference");
            case Condition.GetIsVoiceType when c.Param1 is { } v:
                return (VoiceTypesOf(v), none, "GetIsVoiceType");
            case Condition.GetInFaction when c.Param1 is { } fa:
                return (VoiceTypesOf(fa), NpcsOf(fa), "GetInFaction");
            case Condition.GetIsClass when c.Param1 is { } cl:
                return (VoiceTypesOf(cl), none, "GetIsClass");
            case Condition.GetIsAliasRef when quest is { } q:
            {
                var r = AliasSpeakers(q, (int)c.Param1Raw, g);
                return r.Vt.Count == 0 && r.Npc.Count == 0 ? (null, none, "") : (r.Vt, r.Npc, "quest alias");
            }
            default:
                return (null, none, "");
        }
    }

    /// <summary>Voice types and NPCs that can fill a quest alias.</summary>
    public (HashSet<FormKey> Vt, HashSet<FormKey> Npc) AliasSpeakers(FormKey quest, int aliasId) => AliasSpeakers(quest, aliasId, new Guard());

    private (HashSet<FormKey> Vt, HashSet<FormKey> Npc) AliasSpeakers(FormKey quest, int aliasId, Guard g)
    {
        var vt = new HashSet<FormKey>();
        var npc = new HashSet<FormKey>();
        if (!Quests.TryGetValue(quest, out var q) || !q.Aliases.TryGetValue(aliasId, out var a)) return (vt, npc);
        var guardKey = new FormKey(quest.Plugin + "#alias", (uint)aliasId ^ quest.Id);
        if (!g.Seen.Add(guardKey)) return (vt, npc);
        FormKey? who = a.ForcedRef ?? a.UniqueActor ?? a.CreateObject;
        if (who is { } w) { vt.UnionWith(VoiceTypesOf(w)); npc.UnionWith(NpcsOf(w)); }
        else if (a.ExternalQuest is { } eq && a.ExternalAlias >= 0) { var r = AliasSpeakers(eq, a.ExternalAlias, g); vt.UnionWith(r.Vt); npc.UnionWith(r.Npc); }
        else if (a.Conditions.Count > 0)
        {
            var c = Evaluate(a.Conditions, quest, g);
            if (c.Vt != null) vt.UnionWith(c.Vt);
            if (c.Npc != null) npc.UnionWith(c.Npc);
        }
        if (a.VoiceTypes is { } limitForm)
        {
            var limit = VoiceTypesOf(limitForm);
            if (vt.Count > 0) vt.IntersectWith(limit); else vt.UnionWith(limit);
        }
        return (vt, npc);
    }

    private Dictionary<FormKey, List<(FormKey Quest, int Alias)>> ScenesByTopic()
    {
        if (_scenesByTopic != null) return _scenesByTopic;
        _scenesByTopic = new();
        foreach (var s in Scenes.Values)
            foreach (var (topic, alias) in s.DialogueActions)
            {
                if (s.Quest is not { } q) continue;
                if (!_scenesByTopic.TryGetValue(topic, out var l)) _scenesByTopic[topic] = l = new();
                l.Add((q, alias));
            }
        return _scenesByTopic;
    }

    /// <summary>Who can say this line.</summary>
    public SpeakerResult Speakers(InfoRecord info)
    {
        var res = new SpeakerResult();
        var g = new Guard();
        if (info.Speaker is { } sp)
        {
            res.VoiceTypes.UnionWith(VoiceTypesOf(sp));
            res.Npcs.UnionWith(NpcsOf(sp));
            res.Certainty = SpeakerCertainty.Exact;
            res.Via = "speaker";
            return res;
        }
        Topics.TryGetValue(info.Topic, out var topic);
        FormKey? quest = topic?.Quest;
        var own = Evaluate(info.Conditions, quest, g);
        Constraint? qc = null;
        if (quest is { } qk && Quests.TryGetValue(qk, out var qi) && qi.DialogueConditions.Count > 0) qc = Evaluate(qi.DialogueConditions, quest, g);
        HashSet<FormKey>? vt = own.Vt;
        HashSet<FormKey>? npc = own.Npc;
        string via = own.Via;
        if (qc?.Vt != null)
        {
            vt = vt == null ? qc.Vt : new HashSet<FormKey>(vt.Intersect(qc.Vt));
            if (via.Length == 0) via = "quest conditions";
        }
        if (vt == null && topic != null && ScenesByTopic().TryGetValue(topic.Key, out var actions))
        {
            vt = new HashSet<FormKey>();
            npc = new HashSet<FormKey>();
            foreach (var (q, alias) in actions) { var r = AliasSpeakers(q, alias, g); vt.UnionWith(r.Vt); npc.UnionWith(r.Npc); }
            via = "scene";
        }
        if (vt != null)
        {
            vt.ExceptWith(own.ExcludeVt);
            if (own.Female || own.Male)
                vt.RemoveWhere(v => VoiceTypes.TryGetValue(v, out var vi) && (own.Female ? !vi.Female : vi.Female));
            res.VoiceTypes.UnionWith(vt);
            if (npc != null) { npc.ExceptWith(own.ExcludeNpc); res.Npcs.UnionWith(npc); }
            res.Certainty = res.VoiceTypes.Count == 0 ? SpeakerCertainty.Unknown
                : via.StartsWith("GetIsID") || (res.VoiceTypes.Count == 1 && res.Npcs.Count <= 1) ? SpeakerCertainty.Exact : SpeakerCertainty.Probable;
            res.Via = via;
        }
        return res;
    }

    /// <summary>NPCs that use a voice type (directly or through Use Traits templates).</summary>
    public List<NpcInfo> NpcsWithVoiceType(FormKey vt) =>
        Npcs.Values.Where(n => VoiceTypesOf(n.Key).Contains(vt)).ToList();

    /// <summary>Form lists that contain the form (directly).</summary>
    public List<FormKey> ListsContaining(FormKey form) => FormLists.Where(kv => kv.Value.Contains(form)).Select(kv => kv.Key).ToList();

    public string Describe(FormKey k)
    {
        if (Npcs.TryGetValue(k, out var n)) return n.Display;
        if (VoiceTypes.TryGetValue(k, out var v)) return v.EditorId;
        return k.ToString();
    }
}

internal static class SceneExt
{
    public static SceneInfo With(this SceneInfo s, List<(FormKey Topic, int Alias)> actions) { s.DialogueActions.AddRange(actions); return s; }
}
