using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbvoStudio.Core.Plugins;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Grunts;

/// <summary>
/// Event definitions for PCHT and SPVS. The built-in catalogs are snapshots read from the mods' own files and shipped with
/// the app; when the mod's folder is set, the same readers build the catalog from the installed files instead, so a newer
/// version of the mod (new SPVS categories, more PCHT descriptors) shows up without a DBVO Studio update.
/// </summary>
public static partial class GruntCatalogs
{
    // ------------------------------------------------------------------ built-in snapshots

    public static GruntCatalog BuiltIn(string target)
    {
        var name = "DbvoStudio.Core.Grunts.Data." + target + ".json.gz";
        using var s = typeof(GruntCatalogs).Assembly.GetManifestResourceStream(name);
        if (s == null) return new GruntCatalog { Target = target, Source = "built-in", SourceInfo = "not in this build" };
        using var z = new GZipStream(s, CompressionMode.Decompress);
        var c = JsonSerializer.Deserialize<GruntCatalog>(z, Library.Json) ?? new GruntCatalog();
        c.Source = "built-in";
        return c;
    }

    public static byte[] Pack(GruntCatalog c)
    {
        using var ms = new MemoryStream();
        using (var z = new GZipStream(ms, CompressionLevel.SmallestSize))
            JsonSerializer.Serialize(z, c, Library.Json);
        return ms.ToArray();
    }

    /// <summary>
    /// The catalog for a target: the installed mod's when its folder is set and readable, otherwise the built-in snapshot,
    /// plus the user's custom events. Problems reading the installed files are reported, never thrown.
    /// </summary>
    public static GruntCatalog Resolve(string target, string? modFolder, IEnumerable<GruntEventDef> custom, List<string> notes)
    {
        GruntCatalog c;
        if (!string.IsNullOrWhiteSpace(modFolder) && Directory.Exists(modFolder))
        {
            try { c = target == GruntTargets.Spvs ? ReadSpvs(modFolder) : ReadPcht(modFolder); }
            catch (Exception ex)
            {
                notes.Add($"The installed {GruntTargets.Short(target)} files couldn't be read ({ex.Message}). Using the built-in event list.");
                c = BuiltIn(target);
            }
        }
        else c = BuiltIn(target);
        if (c.Source == "installed")
        {
            // Events the install doesn't show but the built-in list has, e.g. the DLC shouts when PC Head Tracking - Patch.esp
            // sits in another mod folder than the active MCM plugin (Multi Voice Packs installed over PCHT in MO2).
            var have = new HashSet<string>(c.Events.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
            foreach (var e in BuiltIn(target).Events.Where(e => e.Kind == "slots" && !have.Contains(e.Id))) c.Events.Add(e);
        }
        var known = new HashSet<string>(c.Events.Select(e => e.Id), StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(c.Events.Where(e => e.Key.Length > 0).Select(e => e.Key), StringComparer.OrdinalIgnoreCase);
        foreach (var e in custom.Where(e => e.Target == target))
        {
            if (known.Contains(e.Id)) continue;
            // A custom SPVS event the installed SPVS now defines itself: the installed definition wins.
            if (e.Key.Length > 0 && keys.Contains(e.Key)) { notes.Add($"Custom event \"{e.Label}\" ({e.Key}) is now part of the installed SPVS; its built-in definition is used."); continue; }
            var copy = Clone(e);
            copy.Origin = "custom";
            c.Events.Add(copy);
            known.Add(copy.Id);
        }
        return c;
    }

    public static GruntEventDef Clone(GruntEventDef e) => JsonSerializer.Deserialize<GruntEventDef>(JsonSerializer.Serialize(e, Library.Json), Library.Json)!;

    // ------------------------------------------------------------------ voice file names

    /// <summary>
    /// The file name the game looks for a dialogue response under: the quest's editor ID cut to 10 characters, "_", the
    /// topic's editor ID cut so both together are at most 25 characters, "_", the INFO's object ID (8 hex digits, no load
    /// order), "_", the response number. E.g. Combat_____Attack__00000811_1.
    /// </summary>
    public static string VoiceFileName(string questEdid, string topicEdid, uint infoFormId, int response, bool light)
    {
        var q = questEdid.Length > 10 ? questEdid[..10] : questEdid;
        int room = Math.Max(0, 25 - q.Length);
        var t = topicEdid.Length > room ? topicEdid[..room] : topicEdid;
        uint obj = light ? infoFormId & 0xFFF : infoFormId & 0xFFFFFF;
        return $"{q}_{t}_{obj:X8}_{response}";
    }

    // ------------------------------------------------------------------ PCHT

    public const string PchtPlugin = "PC Head Tracking - MCM.esp";
    public const string PchtPatchPlugin = "PC Head Tracking - Patch.esp";
    public const string PchtSoundRoot = @"Sound\FX\PC Head Tracking";

    /// <summary>What each PCHT event folder is for (the text files PCHT ships in each folder).</summary>
    private static readonly Dictionary<string, string> PchtWhen = new(StringComparer.OrdinalIgnoreCase)
    {
        [@"Action\Chair"] = "When sit on a chair",
        [@"Action\Dragon Riding"] = "When climb on a dragon",
        [@"Action\Horse Riding"] = "When climb on a horse",
        [@"Action\Jump"] = "When jump",
        [@"Action\Sneak"] = "When start sneaking",
        ["Bleedout"] = "When entered bleedout state",
        [@"Breath\Eagle Eye Enter"] = "When enter the Eagle Eye mode",
        [@"Breath\Eagle Eye Exit"] = "When exit the Eagle Eye mode",
        [@"Breath\Exhaustion"] = "When Stamina is exhausted",
        [@"Breath\Sprint"] = "When sprinting",
        [@"Breath\Surfacing"] = "When surfaced from the water",
        [@"Combat\Attack"] = "When attack with melee weapons",
        [@"Combat\Attack Fatigued"] = "When attack with melee weapons (Stamina 35% or less)",
        [@"Combat\Attack Ranged"] = "When shooting a bow or a crossbow",
        [@"Combat\Attack Ranged Full Draw"] = "When shooting a bow at full draw",
        [@"Combat\Bash"] = "When perform power bash",
        [@"Combat\Bash Fatigued"] = "When perform power bash (Stamina 35% or less)",
        [@"Combat\Block"] = "When blocked an attack with a shield, a weapon or a ward spell",
        [@"Combat\Friendly Heal"] = "When received a heal spell from a team mate",
        [@"Combat\Friendly Hit"] = "When get hit by a team mate",
        [@"Combat\Health Low"] = "When get hit by an enemy (Health below the 'Low' threshold)",
        [@"Combat\Health Very Low"] = "When get hit by an enemy (Health below the 'Very Low' threshold)",
        [@"Combat\Hit"] = "When get hit by an enemy",
        [@"Combat\Killmove"] = "When perform a killmove",
        [@"Combat\Killmove Finish"] = "When finished performing a killmove",
        [@"Combat\Power Attack"] = "When perform power attack",
        [@"Combat\Power Attack Fatigued"] = "When perform power attack (Stamina 35% or less)",
        [@"Combat\Ready Weapon"] = "When draw weapons or spells in combat",
        [@"Combat\Shield Charge"] = "When perform Shield Charge",
        [@"Combat\Trap Hit"] = "When get hit by a trap",
        ["Death"] = "When enter dead state",
        [@"Greeting\Goodbye"] = "When the 'Goodbye' event is triggered",
        [@"Greeting\Hello"] = "When the 'Hello' event is triggered",
        [@"Greeting\Random"] = "When the 'Random' hotkey is pressed",
        [@"Location\Dungeon"] = "When entered a dungeon",
        [@"Location\House"] = "When entered a house",
        [@"Location\Inn"] = "When entered an inn",
        [@"Location\Player House"] = "When entered a player house",
        [@"Location\Store"] = "When entered a store",
        [@"Location\Weather Clear"] = "When clear weather (in 3rd person view)",
        [@"Location\Weather Cloudy"] = "When cloudy weather (in 3rd person view)",
        [@"Location\Weather Rainy"] = "When rainy weather (in 3rd person view)",
        [@"Location\Weather Snowy"] = "When snowy weather (in 3rd person view)",
        [@"Magic\Blizzard"] = "When cast Blizzard spell",
        [@"Magic\Cloak"] = "When cast a cloak spell",
        [@"Magic\Eclipse Blast"] = "When fire a Bloodcursed arrow with Auriel's Bow at the sun",
        [@"Magic\Fire Storm"] = "When cast Fire Storm spell",
        [@"Magic\Fire"] = "When cast a fire spell",
        [@"Magic\Frost"] = "When cast a frost spell",
        [@"Magic\Generic"] = "When cast the other spells",
        [@"Magic\Guard"] = "When cast a ward spell or an armor spell",
        [@"Magic\Heal"] = "When cast a heal spell",
        [@"Magic\Influence"] = "When cast a fear or frenzy spell",
        [@"Magic\Invisibility"] = "When cast Invisibility spell",
        [@"Magic\Lightning Storm"] = "When cast Lightning Storm spell",
        [@"Magic\Necromancy"] = "When cast a necromantic spell",
        [@"Magic\Ritual"] = "When charge a ritual spell",
        [@"Magic\Rune"] = "When cast a rune spell",
        [@"Magic\Shock"] = "When cast a shock spell",
        [@"Magic\Soul Trap"] = "When cast Soul Trap spell",
        [@"Magic\Summon"] = "When cast a summoning spell",
        [@"Magic\Sun Blast"] = "When fire a Sunhallowed arrow with Auriel's Bow at the sun",
        [@"Magic\Vampiric Drain"] = "When cast Vampiric Drain spell",
        [@"Menu\Book"] = "When opened a book or a note",
        [@"Menu\Crafting"] = "When started crafting",
        [@"Menu\Journal"] = "When opened the Journal Menu",
        [@"Menu\Level Up"] = "When opened the Level Up Menu",
        [@"Menu\Lockpicking Success"] = "When successfully unlocked a container",
        [@"Menu\Lockpicking"] = "When opened the lockpicking window",
        [@"Menu\Map"] = "When opened the Map Menu",
        [@"Menu\Sleep Wait"] = "When opened the Sleep Menu or the Wait Menu",
        [@"Menu\Training Finish"] = "When finished a training",
    };

    private static readonly HashSet<string> SpokenCategories = new(StringComparer.OrdinalIgnoreCase) { "Greeting", "Location", "Menu" };

    /// <summary>Shout names by their first word (the label of a shout event).</summary>
    private static readonly Dictionary<string, string> ShoutNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fus"] = "Unrelenting Force", ["faas"] = "Dismay", ["feim"] = "Become Ethereal", ["su"] = "Elemental Fury",
        ["lok"] = "Clear Skies", ["strun"] = "Storm Call", ["joor"] = "Dragonrend", ["hun"] = "Call of Valor",
        ["od"] = "Call Dragon", ["raan"] = "Animal Allegiance", ["krii"] = "Marked for Death", ["kaan"] = "Kyne's Peace",
        ["iiz"] = "Ice Form", ["laas"] = "Aura Whisper", ["tiid"] = "Slow Time", ["wuld"] = "Whirlwind Sprint",
        ["zun"] = "Disarm", ["yol"] = "Fire Breath", ["fo"] = "Frost Breath", ["rii"] = "Soul Tear", ["gaan"] = "Drain Vitality",
        ["dur"] = "Summon Durnehviir", ["mul"] = "Dragon Aspect", ["ven"] = "Cyclone", ["gol"] = "Bend Will", ["mid"] = "Battle Fury",
        ["zul"] = "Throw Voice", ["vun"] = "Throw Voice",
    };

    /// <summary>The four parts of a shout, by the topic that plays them.</summary>
    private static (string Id, string Label, int Order)? ShoutPhase(string topic)
    {
        var t = topic.ToLowerInvariant();
        if (t.Contains("01a")) return ("01a", "One word (tap)", 0);
        if (t.Contains("01b")) return ("01b", "First word, held", 1);
        if (t.Contains("02")) return ("02", "Second word", 2);
        if (t.Contains("03")) return ("03", "Second and third words", 3);
        return null;
    }

    /// <param name="Shout">The shout the response is for, as "Master.esm:XXXXXX" (stable across plugins with different master lists), or "".</param>
    private sealed record InfoRow(string Quest, string Topic, uint FormId, string Shout, int Response, string Text, string Plugin, bool Light, int FirstCondition);

    private static List<InfoRow> ReadInfos(string pluginPath)
    {
        var pf = PluginFile.Read(pluginPath, new HashSet<string> { "DIAL", "INFO", "QUST" });
        var quests = pf.Records.Where(r => r.Signature == "QUST").ToDictionary(r => r.FormId, r => r.EditorId ?? "");
        var topics = pf.Records.Where(r => r.Signature == "DIAL").ToDictionary(r => r.FormId, r => r);
        var rows = new List<InfoRow>();
        foreach (var info in pf.Records.Where(r => r.Signature == "INFO" && !r.IsDeleted))
        {
            if (info.ParentTopic is not { } tid || !topics.TryGetValue(tid, out var dial)) continue;
            var q = dial.Get("QNAM") is { Data.Length: 4 } qn ? BitConverter.ToUInt32(qn.Data, 0) : 0;
            var questEdid = quests.TryGetValue(q, out var qe) ? qe : "";
            string shout = "";
            int firstCond = info.Subrecords.FirstOrDefault(s => s.Type == "CTDA" && s.Data.Length >= 10) is { } fc ? BitConverter.ToUInt16(fc.Data, 8) : -1;
            foreach (var c in info.Subrecords.Where(s => s.Type == "CTDA" && s.Data.Length >= 16))
            {
                // GetEquippedShout (function 579): the shout this response plays for.
                if (BitConverter.ToUInt16(c.Data, 8) == 579) { var f = BitConverter.ToUInt32(c.Data, 12); shout = $"{pf.OwnerOf(f)}:{f & 0xFFFFFF:X6}"; break; }
            }
            int resp = 1;
            foreach (var s in info.Subrecords)
            {
                if (s.Type == "TRDT" && s.Data.Length >= 13) resp = s.Data[12];
                else if (s.Type == "NAM1")
                    rows.Add(new InfoRow(questEdid, dial.EditorId ?? "", info.FormId, shout, resp, pf.DecodeLString(s.Data) ?? "", pf.FileName, pf.IsLight, firstCond));
            }
        }
        return rows;
    }

    /// <summary>Reads a PCHT install (or any folder that holds its plugins): one pool event per sound descriptor, and the shouts.</summary>
    public static GruntCatalog ReadPcht(string folder)
    {
        var mcm = Path.Combine(folder, PchtPlugin);
        if (!File.Exists(mcm)) throw new FileNotFoundException($"{PchtPlugin} isn't in {folder}.");
        var pf = PluginFile.Read(mcm, new HashSet<string> { "SNDR", "KYWD" });
        var cat = new GruntCatalog { Target = GruntTargets.Pcht, Source = "installed", SourceInfo = mcm };
        foreach (var r in pf.Records.Where(r => r.Signature == "SNDR" && !r.IsDeleted))
        {
            var files = r.Subrecords.Where(s => s.Type == "ANAM").Select(s => PluginRecord.ZString(s.Data)).ToList();
            var rel = PchtFolderOf(files.FirstOrDefault() ?? "");
            if (rel == null) continue; // not one of PCHT's voice descriptors
            var segs = rel.Split('\\');
            cat.Events.Add(new GruntEventDef
            {
                Id = "pcht:" + rel,
                Target = GruntTargets.Pcht,
                Category = segs[0],
                Label = segs.Length > 1 ? string.Join(" · ", segs[1..]) : segs[0],
                Description = PchtWhen.TryGetValue(rel, out var w) ? w + "." : "",
                Folder = rel,
                Sndr = r.EditorId ?? "",
                BaseSlots = files.Count,
                Spoken = SpokenCategories.Contains(segs[0]),
                Origin = "installed",
            });
        }
        cat.Events.Sort((a, b) => PchtCategoryOrder(a.Category).CompareTo(PchtCategoryOrder(b.Category)) is var c && c != 0 ? c : string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase));
        var infos = ReadInfos(mcm);
        var patch = Path.Combine(folder, PchtPatchPlugin);
        if (File.Exists(patch)) infos.AddRange(ReadInfos(patch).Where(i => i.Quest is "_VPDG" or "_VPDB")); // only the DLC shouts live there
        cat.Events.AddRange(ShoutEvents(GruntTargets.Pcht, infos.Where(i => i.Shout.Length > 0 && i.Text.Trim().Length > 0 && i.Quest.StartsWith("_VP", StringComparison.Ordinal))));
        return cat;
    }

    /// <summary>"data\Sound\FX\PC Head Tracking\Combat\Attack\01.wav" → "Combat\Attack" (null when the path is elsewhere).</summary>
    public static string? PchtFolderOf(string anam)
    {
        var p = anam.Replace('/', '\\');
        int i = p.IndexOf(@"PC Head Tracking\", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return null;
        var rest = p[(i + @"PC Head Tracking\".Length)..];
        int last = rest.LastIndexOf('\\');
        return last <= 0 ? null : rest[..last];
    }

    private static int PchtCategoryOrder(string c) => Array.IndexOf(new[] { "Combat", "Breath", "Death", "Bleedout", "Action", "Magic", "Greeting", "Location", "Menu", "Shouts" }, c) is var i && i >= 0 ? i : 50;

    private static List<GruntEventDef> ShoutEvents(string target, IEnumerable<InfoRow> rows)
    {
        var events = new List<GruntEventDef>();
        foreach (var g in rows.GroupBy(r => r.Shout))
        {
            var ev = new GruntEventDef
            {
                Id = $"{target}:shout:{g.Key}",
                Target = target,
                Category = "Shouts",
                Kind = "slots",
                Origin = "installed",
                Spoken = true,
            };
            foreach (var pg in g.Select(r => (Row: r, Phase: ShoutPhase(r.Topic))).Where(x => x.Phase != null).GroupBy(x => x.Phase!.Value.Id).OrderBy(x => x.First().Phase!.Value.Order))
            {
                var ph = pg.First().Phase!.Value;
                // One slot per distinct text (SPVS lists some shouts twice: as a short and a long version of the same word).
                foreach (var tg in pg.GroupBy(x => x.Row.Text.Trim()))
                {
                    var slot = new GruntSlotDef
                    {
                        Id = ph.Id + (pg.Select(x => x.Row.Text.Trim()).Distinct().Count() > 1 ? "-" + Slug(tg.Key) : ""),
                        Label = ph.Label,
                        Text = tg.Key,
                        Link = $"shout:{g.Key}:{ph.Id}",
                    };
                    foreach (var x in tg)
                    {
                        var name = VoiceFileName(x.Row.Quest, x.Row.Topic, x.Row.FormId, x.Row.Response, x.Row.Light);
                        var vt = target == GruntTargets.Spvs ? "SPVS_Replace{slot}" : "{vt}";
                        var path = $@"Sound\Voice\{x.Row.Plugin}\{vt}\{name}";
                        if (!slot.Files.Contains(path, StringComparer.OrdinalIgnoreCase)) slot.Files.Add(path);
                    }
                    ev.Slots.Add(slot);
                }
            }
            if (ev.Slots.Count == 0) continue;
            string Word(string id) => Regex.Replace(ev.Slots.FirstOrDefault(s => s.Id.StartsWith(id, StringComparison.Ordinal))?.Text ?? "", @"[!.]+", " ").Trim();
            var words = string.Join(" ", new[] { Word("01b"), Word("03") }.Where(w => w.Length > 0));
            if (words.Length == 0) words = Word("01a");
            words = string.Join(" ", words.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            var firstWords = new[] { Word("01a"), Word("01b") }.Select(w => (w.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "").ToLowerInvariant());
            ev.Label = firstWords.Select(w => ShoutNames.TryGetValue(w, out var nm) ? nm : null).FirstOrDefault(n => n != null) ?? words;
            ev.Description = $"Shout words: {words}. Each part is its own file (the game plays the one that matches how long you hold the key).";
            events.Add(ev);
        }
        // Two shouts with one name (a DLC variant): tell them apart by their words.
        foreach (var dup in events.GroupBy(e => e.Label, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var e in dup.Skip(1)) e.Label += " (" + e.Description.Split(':', '.')[1].Trim() + ")";
        return events.OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string Slug(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "");

    // ------------------------------------------------------------------ SPVS

    public const string SpvsPlugin = "SmartPlayerVoiceSystem.esp";
    public const string SpvsShoutPlugin = "SPVS_VanillaShout.esp";
    public const string SpvsAdditionalRoot = @"Sound\SPVS_AdditionalVoice\CustomVoiceType";
    public static readonly string[] SpvsSlots =
    {
        "FemaleCustom01", "FemaleCustom02", "FemaleCustom03", "FemaleCustom04", "FemaleCustom05",
        "MaleCustom01", "MaleCustom02", "MaleCustom03", "MaleCustom04", "MaleCustom05",
    };

    /// <summary>What each SPVS event folder is for (SPVS's readme files and mod page).</summary>
    private static readonly Dictionary<string, string> SpvsWhat = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Bleedout"] = "When the player enters a critical health state. An alternative to death voices with essential or invulnerability mods.",
        ["Book"] = "Whenever the player reads a book.",
        ["Bow"] = "Whenever the player fires an arrow.",
        ["CombatStart"] = "When entering combat, by enemy type. No audio plays while sneaking.",
        ["Craft"] = "Crafting: Start plays when opening the crafting menu, Done after crafting an item.",
        ["Discovered"] = "When discovering a location. General plays for any location without its own line.",
        [@"Discovered\Specific\Voice"] = "When discovering a specific location: name the file after the location's English name (e.g. Whiterun). SPVS's own config maps vanilla locations to these names.",
        ["Disease"] = "Diseases: Onset when contracting one, General when changing locations or waking up while diseased.",
        ["DragonSoul"] = "When absorbing a dragon soul.",
        ["Dungeon"] = "When entering a dungeon, by dungeon type.",
        ["DungeonsCleared"] = "Upon clearing a dungeon.",
        ["Fall"] = "Upon taking damage from a high fall.",
        ["Food"] = "When eating food or drinking beverages.",
        ["FriendlyFire"] = "When hit by friendly fire.",
        ["FriendlyHeal"] = "When receiving healing magic from an ally.",
        ["Greeting"] = "When the crosshair rests on an NPC for a few seconds (or with the hotkey): followers, friends, neutral NPCs, lovers and animals.",
        ["Harvest"] = "When harvesting ingredients or materials.",
        [@"Health\Low"] = "When health falls to 50% or below.",
        [@"Health\VeryLow"] = "When health falls to 20% or below.",
        [@"Horse\Mount"] = "When mounting a horse.",
        [@"Horse\Sprint"] = "When sprinting on a horse.",
        ["Inn"] = "When entering an inn.",
        ["Jail"] = "When sent to jail.",
        ["Journal"] = "When opening the journal menu.",
        ["Jump"] = "When jumping.",
        ["KillMove"] = "When triggering a killmove.",
        ["Levelup"] = "When leveling up.",
        [@"Lockpick\Start"] = "When starting lockpicking.",
        [@"Lockpick\Success"] = "When successfully picking a lock.",
        [@"Lockpick\Failure"] = "When failing to pick a lock.",
        [@"Lockpick\BarelySuccess"] = "When succeeding after breaking several lockpicks.",
        ["Magic"] = "When casting a spell of this kind.",
        ["Map"] = "When opening the map menu.",
        [@"NPCKilled\Player"] = "When combat ends and the player killed the last enemy.",
        [@"NPCKilled\Follower"] = "When combat ends and a follower killed the last enemy.",
        ["Overencumbered"] = "When becoming encumbered.",
        ["PlayerHouse"] = "When entering your home.",
        ["Potion"] = "When drinking a potion.",
        ["Random"] = "At set intervals (MCM Cooldown tab), by where the player is.",
        ["Sit"] = "When sitting down.",
        ["Sleep"] = "When going to sleep.",
        [@"Sneak\Start"] = "When entering sneak mode.",
        [@"Sneak\Detected"] = "When detected by an enemy while sneaking, triggering combat.",
        ["Store"] = "When entering a shop.",
        ["Training"] = "When receiving skill training from an NPC.",
        ["Wait"] = "When finishing waiting.",
        ["Wakeup"] = "When waking up.",
        ["Weather"] = "When the weather changes, by weather type.",
        ["_Vanilla"] = "Replaces the game's own player sound (script-based method: any file names and counts). With the vanilla-based method on, the lines are also copied into the game's fixed file names.",
    };

    /// <summary>PCHT folders whose lines suit each SPVS folder (SPVS's own PCHT → SPVS rename tool).</summary>
    private static readonly Dictionary<string, string[]> SpvsFromPcht = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Sit"] = new[] { @"Action\Chair" }, ["Jump"] = new[] { @"Action\Jump" }, [@"Horse\Mount"] = new[] { @"Action\Horse Riding" },
        [@"Sneak\Start"] = new[] { @"Action\Sneak" }, ["Bleedout"] = new[] { "Bleedout" },
        [@"_Vanilla\EnterEagleEye"] = new[] { @"Breath\Eagle Eye Enter" }, [@"_Vanilla\ExitEagleEye"] = new[] { @"Breath\Eagle Eye Exit" },
        [@"_Vanilla\LeaveWater"] = new[] { @"Breath\Surfacing" }, [@"_Vanilla\OutofBreath"] = new[] { @"Breath\Exhaustion" },
        [@"_Vanilla\Sprint"] = new[] { @"Breath\Sprint" }, [@"_Vanilla\Attack"] = new[] { @"Combat\Attack" },
        [@"_Vanilla\AttackFatigued"] = new[] { @"Combat\Attack Fatigued" }, [@"_Vanilla\Bash"] = new[] { @"Combat\Bash" },
        [@"_Vanilla\BashFatigued"] = new[] { @"Combat\Bash Fatigued" }, [@"_Vanilla\Block"] = new[] { @"Combat\Block" },
        [@"_Vanilla\BlockFatigued"] = new[] { @"Combat\Block" }, [@"_Vanilla\Hit"] = new[] { @"Combat\Hit" },
        [@"_Vanilla\PowerAttack"] = new[] { @"Combat\Power Attack" }, [@"_Vanilla\PowerAttackFatigued"] = new[] { @"Combat\Power Attack Fatigued" },
        [@"Health\Low"] = new[] { @"Combat\Health Low" }, [@"Health\VeryLow"] = new[] { @"Combat\Health Very Low" },
        ["Bow"] = new[] { @"Combat\Attack Ranged" }, ["FriendlyFire"] = new[] { @"Combat\Friendly Hit" },
        ["FriendlyHeal"] = new[] { @"Combat\Friendly Heal" }, [@"NPCKilled\Follower"] = new[] { @"Combat\Killmove Finish" },
        [@"NPCKilled\Player"] = new[] { @"Combat\Killmove Finish" }, ["KillMove"] = new[] { @"Combat\Killmove" },
        [@"_Vanilla\Death"] = new[] { "Death" }, [@"Greeting\Friend"] = new[] { @"Greeting\Random" },
        ["Inn"] = new[] { @"Location\Inn" }, ["Store"] = new[] { @"Location\Store" }, ["PlayerHouse"] = new[] { @"Location\Player House" },
        [@"Weather\Cloudy"] = new[] { @"Location\Weather Cloudy" }, [@"Weather\Rainy"] = new[] { @"Location\Weather Rainy" },
        [@"Weather\Snow"] = new[] { @"Location\Weather Snowy" }, [@"Weather\Sunny"] = new[] { @"Location\Weather Clear" },
        [@"Magic\Armor"] = new[] { @"Magic\Guard" }, [@"Magic\Cloak"] = new[] { @"Magic\Cloak" }, [@"Magic\DamageFire"] = new[] { @"Magic\Fire" },
        [@"Magic\DamageFrost"] = new[] { @"Magic\Frost" }, [@"Magic\DamageShock"] = new[] { @"Magic\Shock" }, [@"Magic\Heal"] = new[] { @"Magic\Heal" },
        [@"Magic\Influence"] = new[] { @"Magic\Influence" }, [@"Magic\Invisibility"] = new[] { @"Magic\Invisibility" },
        [@"Magic\MasterFire"] = new[] { @"Magic\Fire Storm" }, [@"Magic\MasterFrost"] = new[] { @"Magic\Blizzard" },
        [@"Magic\MasterShock"] = new[] { @"Magic\Lightning Storm" }, [@"Magic\Other"] = new[] { @"Magic\Generic" },
        [@"Magic\Ritual"] = new[] { @"Magic\Ritual" }, [@"Magic\Rune"] = new[] { @"Magic\Rune" }, [@"Magic\SummonGeneral"] = new[] { @"Magic\Summon" },
        [@"Magic\SummonUndead"] = new[] { @"Magic\Necromancy" }, [@"Magic\VampiricDrain"] = new[] { @"Magic\Vampiric Drain" },
        ["Book"] = new[] { @"Menu\Book" }, [@"Craft\Start"] = new[] { @"Menu\Crafting" }, ["Journal"] = new[] { @"Menu\Journal" },
        ["Levelup"] = new[] { @"Menu\Level Up" }, [@"Lockpick\BarelySuccess"] = new[] { @"Menu\Lockpicking Success" },
        [@"Lockpick\Start"] = new[] { @"Menu\Lockpicking" }, [@"Lockpick\Success"] = new[] { @"Menu\Lockpicking Success" },
        ["Map"] = new[] { @"Menu\Map" }, ["Sleep"] = new[] { @"Menu\Sleep Wait" }, ["Training"] = new[] { @"Menu\Training Finish" },
    };

    /// <summary>The vanilla-based dialogue topics of SmartPlayerVoiceSystem.esp, by the _Vanilla folder they replace.</summary>
    private static readonly Dictionary<string, string> SpvsVanillaTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Attack_"] = "Attack", ["AttackS"] = "AttackFatigued", ["Bash_"] = "Bash", ["BashS"] = "BashFatigued",
        ["Block_"] = "Block", ["BlockS"] = "BlockFatigued", ["PowerAttack_"] = "PowerAttack", ["PowerAttackS"] = "PowerAttackFatigued",
        ["Hit_"] = "Hit", ["Death_"] = "Death", ["EnterBowZoomBreath_"] = "EnterEagleEye", ["ExitBowZoomBreath_"] = "ExitEagleEye",
        ["OutofBreath_"] = "OutofBreath", ["LeaveWaterBreath_"] = "LeaveWater", ["EnterSprintBreath"] = "Sprint",
    };

    /// <summary>The [slot.categories] of an Audio Util config: key → folder or file list (data-relative).</summary>
    public static List<(string Key, List<string> Values)> ReadAudioUtilCategories(string toml)
    {
        var result = new List<(string, List<string>)>();
        bool inCats = false;
        foreach (var raw in toml.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith('['))
            {
                inCats = line.Equals("[slot.categories]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inCats) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim().Trim('"', '\'');
            var val = line[(eq + 1)..].Trim();
            var values = new List<string>();
            foreach (Match m in Regex.Matches(val, @"'([^']*)'|""((?:[^""\\]|\\.)*)"""))
                values.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Replace(@"\\", @"\"));
            if (key.Length > 0 && values.Count > 0) result.Add((key, values));
        }
        return result;
    }

    /// <summary>Reads an SPVS install: the Audio Util categories of the custom slots, the vanilla-based names and the shouts.</summary>
    public static GruntCatalog ReadSpvs(string folder)
    {
        var tomlPath = Path.Combine(folder, "SKSE", "Plugins", "AudioUtil", "config", "SPVS_FemaleCustom01.toml");
        if (!File.Exists(tomlPath)) throw new FileNotFoundException(@"SKSE\Plugins\AudioUtil\config\SPVS_FemaleCustom01.toml isn't in " + folder + ".");
        var cat = new GruntCatalog { Target = GruntTargets.Spvs, Source = "installed", SourceInfo = folder };
        var root = SpvsAdditionalRoot + @"\FemaleCustom01\";
        foreach (var (key, values) in ReadAudioUtilCategories(File.ReadAllText(tomlPath)))
        {
            var v = values[0].Replace('/', '\\');
            if (!v.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // a file list or a path outside the slot
            cat.Events.Add(SpvsEvent(key, v[root.Length..].TrimEnd('\\'), "installed"));
        }
        // Discovered\Specific is read by SPVS's own scripts (file names from the location config), not through a category.
        if (!cat.Events.Any(e => e.Folder.Equals(@"Discovered\Specific\Voice", StringComparison.OrdinalIgnoreCase)))
            cat.Events.Add(SpvsEvent("", @"Discovered\Specific\Voice", "installed"));

        var main = Path.Combine(folder, SpvsPlugin);
        if (File.Exists(main))
        {
            var infos = ReadInfos(main);
            // The topics also hold SPVS's own fillers (a random-chance blank, silent resets); they start with GetRandomPercent
            // (77) or condition 640, while the documented responses don't.
            foreach (var g in infos.Where(i => i.Quest.EndsWith("StandalonePlayerVoice", StringComparison.Ordinal) && SpvsVanillaTopics.ContainsKey(i.Topic) && i.FirstCondition is not (77 or 640) && i.Text.Length > 0).GroupBy(i => SpvsVanillaTopics[i.Topic]))
            {
                var ev = cat.Events.FirstOrDefault(e => e.Folder.Equals(@"_Vanilla\" + g.Key, StringComparison.OrdinalIgnoreCase));
                if (ev == null) continue;
                // A blank response next to written ones is a silent filler too (the fatigued topics are all blank).
                bool anyText = g.Any(i => i.Text.Trim().Length > 0);
                // The documented responses are one run of consecutive IDs; stray fillers sit elsewhere (0xDEx).
                ev.VanillaNames = LongestRun(g.Where(i => !anyText || i.Text.Trim().Length > 0).OrderBy(i => i.FormId).ToList()).Select(i => VoiceFileName(i.Quest, i.Topic, i.FormId, i.Response, i.Light)).ToList();
            }
            var shoutRows = infos.Where(i => i.Shout.Length > 0 && i.Text.Trim().Length > 0 && i.Quest.StartsWith("Shout", StringComparison.Ordinal)).ToList();
            var vs = Path.Combine(folder, SpvsShoutPlugin);
            if (File.Exists(vs)) shoutRows.AddRange(ReadInfos(vs).Where(i => i.Shout.Length > 0 && i.Text.Trim().Length > 0));
            cat.Events.AddRange(ShoutEvents(GruntTargets.Spvs, shoutRows));
        }
        cat.Events = cat.Events.OrderBy(e => SpvsCategoryOrder(e.Category)).ThenBy(e => e.Kind == "slots" ? "" : e.Category, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.Kind == "slots" ? e.Label : e.Folder.Split('\\').Length > 1 ? e.Label : "", StringComparer.OrdinalIgnoreCase).ToList();
        var meta = Path.Combine(folder, "meta.ini");
        if (File.Exists(meta) && Regex.Match(File.ReadAllText(meta), @"^version=([^\r\n]+)", RegexOptions.Multiline) is { Success: true } mv)
            cat.SourceInfo = $"SPVS {mv.Groups[1].Value.Trim()} · {folder}";
        return cat;
    }

    private static List<InfoRow> LongestRun(List<InfoRow> sorted)
    {
        int bestStart = 0, bestLen = 0;
        for (int i = 0; i < sorted.Count;)
        {
            int j = i + 1;
            while (j < sorted.Count && (sorted[j].FormId & 0xFFFFFF) == (sorted[j - 1].FormId & 0xFFFFFF) + 1) j++;
            if (j - i > bestLen) { bestLen = j - i; bestStart = i; }
            i = j;
        }
        return sorted.GetRange(bestStart, bestLen);
    }

    private static int SpvsCategoryOrder(string c) => c switch { "Grunts (_Vanilla)" => 0, "Shouts" => 99, _ => 10 };

    /// <summary>An SPVS pool event from its category key and folder (relative to the voice slot).</summary>
    public static GruntEventDef SpvsEvent(string key, string folder, string origin)
    {
        var segs = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        bool vanilla = segs.Length > 0 && segs[0] == "_Vanilla";
        var top = segs.Length > 0 ? segs[0] : key;
        string label = vanilla ? Words(segs.Length > 1 ? segs[1] : key) : segs.Length > 1 ? string.Join(" · ", segs.Skip(1).Select(Words)) : Words(top);
        if (folder.Equals(@"Discovered\Specific\Voice", StringComparison.OrdinalIgnoreCase)) label = "Specific location";
        string desc = SpvsWhat.TryGetValue(folder, out var d) ? d : SpvsWhat.TryGetValue(top, out var d2) ? d2 : "";
        return new GruntEventDef
        {
            Id = "spvs:" + (key.Length > 0 ? key : folder),
            Target = GruntTargets.Spvs,
            Category = vanilla ? "Grunts (_Vanilla)" : Words(top),
            Label = label,
            Description = desc,
            Folder = folder,
            Key = key,
            Spoken = !vanilla,
            PchtFolders = SpvsFromPcht.TryGetValue(folder, out var pf) ? pf.ToList() : new List<string>(),
            Origin = origin,
            Notes = folder.Equals(@"Discovered\Specific\Voice", StringComparison.OrdinalIgnoreCase) ? "Give every line a file name: the location's English name, e.g. Whiterun." : "",
        };
    }

    /// <summary>"VampiricDrain" → "Vampiric Drain", "NPCKilled" → "NPC Killed", "Levelup" stays.</summary>
    public static string Words(string s) => s switch
    {
        "OutofBreath" => "Out of Breath", "Levelup" => "Level Up", "Wakeup" => "Wake Up", "BarelySuccess" => "Barely Success",
        "NPCKilled" => "NPC Killed", "DragonSoul" => "Dragon Soul", "KillMove" => "Killmove",
        _ => Regex.Replace(s, @"(?<=[a-z])(?=[A-Z])|(?<=[A-Z])(?=[A-Z][a-z])", " "),
    };

    // ------------------------------------------------------------------ custom events

    /// <summary>
    /// Reads custom definitions to import: DBVO Studio's own format, a JSON list of events, or an SPVS Audio Util .toml
    /// (its categories become SPVS events). Returns the events and what was skipped.
    /// </summary>
    public static (List<GruntEventDef> Events, List<string> Problems) ParseImport(string fileName, string text, string target, GruntCatalog current)
    {
        var problems = new List<string>();
        var found = new List<GruntEventDef>();
        if (fileName.EndsWith(".toml", StringComparison.OrdinalIgnoreCase))
        {
            if (target != GruntTargets.Spvs) { problems.Add("A .toml file holds SPVS (Audio Util) categories; open an SPVS project to import it."); return (found, problems); }
            foreach (var (key, values) in ReadAudioUtilCategories(text))
            {
                var v = values[0].Replace('/', '\\');
                var m = Regex.Match(v, @"^Sound\\SPVS_AdditionalVoice\\[^\\]+\\[^\\]+\\(.+)$", RegexOptions.IgnoreCase);
                if (!m.Success) { problems.Add($"{key}: not a folder of a voice slot ({v}); skipped."); continue; }
                found.Add(SpvsEvent(key, m.Groups[1].Value.TrimEnd('\\'), "custom"));
            }
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var rootEl = doc.RootElement;
                JsonElement list = default;
                bool hasList = false;
                if (rootEl.ValueKind == JsonValueKind.Array) { list = rootEl; hasList = true; }
                else if (rootEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var prop in rootEl.EnumerateObject())
                    {
                        if (prop.Name.Equals("events", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Array) { list = prop.Value; hasList = true; }
                        if (prop.Name.Equals("version", StringComparison.OrdinalIgnoreCase) && prop.Value.ValueKind == JsonValueKind.Number && prop.Value.GetInt32() > GruntCatalog.CurrentVersion)
                            problems.Add($"The file was made by a newer DBVO Studio (format {prop.Value.GetInt32()}); fields this version doesn't know are ignored.");
                    }
                }
                if (!hasList) { problems.Add("The file has no list of events."); return (found, problems); }
                foreach (var el in list.EnumerateArray())
                {
                    GruntEventDef? e;
                    try { e = el.Deserialize<GruntEventDef>(Library.Json); }
                    catch (JsonException ex) { problems.Add("An event couldn't be read: " + ex.Message); continue; }
                    if (e != null) found.Add(e);
                }
            }
            catch (JsonException ex) { problems.Add("The file isn't valid JSON: " + ex.Message); return (found, problems); }
        }
        var ok = new List<GruntEventDef>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in found)
        {
            e.Target = string.IsNullOrWhiteSpace(e.Target) ? target : e.Target.ToLowerInvariant();
            if (e.Target != target) { problems.Add($"{e.Label}: made for {GruntTargets.Short(e.Target)}, not {GruntTargets.Short(target)}; skipped."); continue; }
            var why = Validate(e, current, ok);
            if (why != null) { problems.Add($"{(e.Label.Length > 0 ? e.Label : e.Id)}: {why}; skipped."); continue; }
            if (!ids.Add(e.Id)) { problems.Add($"{e.Label}: listed twice; the first is used."); continue; }
            e.Origin = "custom";
            ok.Add(e);
        }
        return (ok, problems);
    }

    [GeneratedRegex(@"^[A-Za-z0-9_]+$")] private static partial Regex KeyChars();
    [GeneratedRegex(@"^[A-Za-z0-9 _\-\\]+$")] private static partial Regex FolderChars();

    /// <summary>Why a custom event can't be used (null when it can). Fills in an ID when missing.</summary>
    public static string? Validate(GruntEventDef e, GruntCatalog current, IEnumerable<GruntEventDef>? alsoAdding = null)
    {
        e.Folder = (e.Folder ?? "").Replace('/', '\\').Trim().Trim('\\');
        e.Key = (e.Key ?? "").Trim();
        e.Label = (e.Label ?? "").Trim();
        e.Category = string.IsNullOrWhiteSpace(e.Category) ? "Custom" : e.Category.Trim();
        if (e.Label.Length == 0) return "no name";
        if (e.Kind != "pool") return "only pool events (a folder of lines) can be added by hand";
        if (e.Folder.Length == 0) return "no folder";
        if (!FolderChars().IsMatch(e.Folder) || e.Folder.Split('\\').Any(s => s.Trim().Length == 0 || s.Trim() is "." or "..")) return "the folder has characters a game path can't use";
        if (e.Target == GruntTargets.Spvs)
        {
            if (e.Key.Length == 0) return "no Audio Util category key (the name SPVS's scripts ask for, e.g. MagicHeal)";
            if (!KeyChars().IsMatch(e.Key)) return "the category key may only have letters, digits and _";
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = "spvs:" + e.Key;
            if (current.Events.Any(x => x.Origin != "custom" && x.Key.Equals(e.Key, StringComparison.OrdinalIgnoreCase))) return $"SPVS already has the category {e.Key}";
        }
        else
        {
            if (e.Sndr.Length == 0) return "no sound descriptor (SNDR editor ID) to override";
            if (string.IsNullOrWhiteSpace(e.Id)) e.Id = "pcht:" + e.Folder;
            if (current.Events.Any(x => x.Origin != "custom" && x.Sndr.Equals(e.Sndr, StringComparison.OrdinalIgnoreCase))) return $"PCHT already has the descriptor {e.Sndr}";
        }
        if (current.Events.Any(x => x.Origin != "custom" && x.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase))) return "an event with this ID is built in";
        if (current.Events.Any(x => x.Origin != "custom" && x.Kind == "pool" && x.Folder.Equals(e.Folder, StringComparison.OrdinalIgnoreCase))) return $"the folder {e.Folder} belongs to the built-in event \"{current.Events.First(x => x.Folder.Equals(e.Folder, StringComparison.OrdinalIgnoreCase)).Label}\"";
        if (alsoAdding != null && alsoAdding.Any(x => x.Folder.Equals(e.Folder, StringComparison.OrdinalIgnoreCase))) return "another imported event uses the same folder";
        return null;
    }
}
