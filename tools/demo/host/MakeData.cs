using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;
using DbvoStudio.Core.Revoice;
using DbvoStudio.Core.Tests;

// Neutral sample data for the user guide's screenshots: the base game as the mod to patch, a generic voice pack,
// and an invented example mod for NPC Revoice.
public static class MakeData
{
    const string R = "/srv/dbvo";
    static string W(string win) => R + "/" + win[0] + "/" + win[3..].Replace('\\', '/');

    static void Script(string path, string body)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\n" + body + "\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
    }

    static void Plugin(string path, IEnumerable<string> masters, (string edid, string text)[] lines, uint baseId)
    {
        var b = new TestPluginBuilder();
        b.Masters.Clear(); b.Masters.AddRange(masters);
        uint id = baseId;
        foreach (var (edid, text) in lines) { b.AddTopic(id, edid, text, (id + 1, null)); id += 2; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, b.Build());
    }

    public static void Run(bool fresh = false)
    {
        if (Directory.Exists(R)) Directory.Delete(R, true);
        var data = W(@"D:\SteamLibrary\steamapps\common\Skyrim Special Edition\Data");
        Directory.CreateDirectory(data);

        // ---- the base game plugins, with sample player lines (illustrative, not the game's own records)
        Plugin(Path.Combine(data, "Skyrim.esm"), Array.Empty<string>(), new[]
        {
            ("DialogueTownInfo", "What can you tell me about this town?"),
            ("DialogueWork", "I'm looking for work."),
            ("DialogueRumors", "Have you heard any rumors lately?"),
            ("DialogueDragons", "What do you know about the dragons?"),
            ("DialogueJarl", "Tell me about the Jarl."),
            ("DialogueRoom", "I'd like to rent a room for the night."),
            ("DialogueTrain", "Can you train me?"),
            ("DialogueName", "I'm <Alias=Player>. Remember it."),
            ("DialogueCourier", "Take this to <Alias=Recipient> in Whiterun."),
            ("DialogueSigh", "*sigh* Fine, I'll do it."),
            ("DialoguePersuade", "Let me through. I won't cause any trouble. (Persuade)"),
            ("DialogueLaugh", "(Laugh)"),
            ("DialogueSilent", "(Say nothing)"),
            ("DialogueRest", "  We should rest for a while.  "),
            ("DialogueJourney", "I came over the border, lived through the dragon attack at Helgen, crossed the mountains in a blizzard, and I'm still standing here, so tell me what you know about it."),
            ("DialogueGreybeards", "Where can I find the Greybeards?"),
            ("DialogueCompanions", "I'm ready to join the Companions."),
            ("DialoguePlace", "What's the story behind this place?"),
            ("DialogueBandits", "I've cleared out the bandit camp."),
            ("DialogueFollower", "I could use someone to watch my back."),
            ("DialogueHelp", "Is there anything I can do to help?"),
            ("DialogueFound", "I found this. Does it belong to you?"),
            ("DialogueNever", "Never mind."),
            ("DialogueLater", "Let's talk later."),
        }, 0x00010800);
        // Update.esm (a base game master) carries invented NPCs and voice types for the Voiced Books example.
        {
            var u = new BookPluginBuilder { HeaderFlags = 1 };
            u.Masters.Add("Skyrim.esm");
            u.VoiceTypes.Add(new(0x01000A01, "MaleNord"));
            u.VoiceTypes.Add(new(0x01000A02, "FemaleCommoner", true));
            u.VoiceTypes.Add(new(0x01000A03, "MaleOldKindly"));
            u.Npcs.Add(new(0x01000B01, "ExBrannoc", "Brannoc Stoneheart", 0x01000A01));
            u.Npcs.Add(new(0x01000B02, "ExLyra", "Lyra Vane", 0x01000A02, Female: true));
            u.Npcs.Add(new(0x01000B03, "ExOrvin", "Orvin the Archivist", 0x01000A03));
            u.Write(Path.Combine(data, "Update.esm"));
        }
        Plugin(Path.Combine(data, "Dawnguard.esm"), new[] { "Skyrim.esm", "Update.esm" }, new[]
        {
            ("DLC1Join", "I want to join the Dawnguard."),
            ("DLC1Vampires", "What can you tell me about vampires?"),
            ("DLC1Scroll", "I've brought the Elder Scroll."),
            ("DLC1Cure", "Is there a cure for vampirism?"),
            ("DLC1Crossbow", "Tell me about the crossbow."),
        }, 0x02000800);
        Plugin(Path.Combine(data, "HearthFires.esm"), new[] { "Skyrim.esm", "Update.esm" }, new[]
        {
            ("BYOHLand", "I'd like to buy a plot of land."),
            ("BYOHBuild", "What do I need to build a house?"),
            ("BYOHAdopt", "I'd like to adopt a child."),
            ("BYOHSteward", "I need a steward for my home."),
        }, 0x02000900);
        Plugin(Path.Combine(data, "Dragonborn.esm"), new[] { "Skyrim.esm", "Update.esm" }, new[]
        {
            ("DLC2Island", "What happened to this island?"),
            ("DLC2Stones", "Tell me about the stones."),
            ("DLC2Passage", "I need passage to Solstheim."),
            ("DLC2Shrines", "What are these strange shrines?"),
            ("DLC2Ash", "Where does all this ash come from?"),
        }, 0x02000A00);

        var fonix = Path.Combine(data, "Sound/Voice/Processing/FonixData.cdf");
        Directory.CreateDirectory(Path.GetDirectoryName(fonix)!); File.WriteAllBytes(fonix, new byte[4096]);
        var game = Path.GetDirectoryName(data)!;
        Script(Path.Combine(game, "Tools/Audio/xWMAEncode.exe"), "{ head -c 8 \"$3\"; printf XWMA; tail -c +13 \"$3\"; } > \"$4\"");
        Script(W(@"D:\Modding\Tools\FaceFXWrapper\FaceFXWrapper.exe"), "head -c 1536 /dev/urandom > \"$6\"");
        Script(W(@"C:\Program Files\DBVO Studio\Tools\ffmpeg\ffmpeg.exe"), "exec /usr/bin/ffmpeg \"$@\"");
        Script(W(@"C:\Program Files\DBVO Studio\Tools\7-Zip\7z.exe"), "exit 0");

        // ---- an invented example mod with books for the Voiced Books Tool
        {
            var m = new BookPluginBuilder();
            m.Masters.AddRange(new[] { "Skyrim.esm", "Update.esm" });
            string P(params string[] ps) => string.Join("\n\n", ps);
            var road = P(
                "<p align=\"center\">Roads of the Old North<br>Vol. II</p>[pagebreak]<p>by Orvin the Archivist</p>",
                "<img src='img://Textures/Interface/Books/Illuminated_Letters/T_letter.png' width=56>he old roads of the north were laid before the holds had names. Travellers still follow them without knowing it, for a road remembers the feet that made it long after the makers are gone. In the valleys the stones are worn smooth, and in the passes they are broken and grown over with moss, but the line of them holds true from one hill to the next.",
                "Merchants say a road is only as good as the inn at its end. That is a merchant's wisdom (and a hungry one). The truth is older: a road is as good as the reason to walk it. When the reason fades, the road fades with it. When a new reason comes, the road wakes again, as if it had only been sleeping under the snow.",
                "On the 17th of Last Seed, 4E 188, I walked the eastern road from the river ford to the stone bridge. Half a day's walk, the guides said. It took me two, for I stopped at every marker to copy what was carved there. Most were names. Some were warnings. One was a joke, and I will not repeat it here.",
                "The markers are of three kinds. The first are plain stones set upright at a crossing, with a mark for each road that leaves it. The second are cairns, built up by travellers who each add a stone as they pass. The third are the carved posts, and these are the rarest. Mr. Halvard of the trading post believes they were set by a single hand, over many years. Dr. Ennis of the college does not agree. I have no opinion, only notes.",
                "What I can say is this. Every carved post faces east. Every one stands where a traveller would first see the sunrise after a night in the hills. Whoever set them wanted the walker to look up at the right moment, and to see the road go on.",
                "In the next volume I will write of the western roads, which are younger, and of the ferry crossings, which are younger still. For now I leave the reader at the stone bridge, where the eastern road ends and the river takes over the work of carrying people where they need to go. Walk it if you can. Look up at the posts. And add a stone to the cairn at the ford, for luck, as I did.");
            m.Books.Add(new(0x02000801, "ExRoadsOldNorth2", "Roads of the Old North, Vol. II", road));
            m.Books.Add(new(0x02000802, "ExSkillRoads", "Roads of the Old North, Vol. II", road, Flags: 1));
            m.Books.Add(new(0x02000803, "ExLetterBrannoc", "Letter to Lyra", P("Lyra,", "The cart leaves at dawn. Bring the ledger and the spare wheel, and tell <Alias=Player> to meet us at the ford if they still want work.", "-Brannoc Stoneheart")));
            m.Books.Add(new(0x02000804, "ExLyraJournal", "Lyra's Journal", P("<font face='$HandwrittenFont'>Day one. Brannoc says the eastern road is safe. Brannoc says many things.</font>", "Day three. The bridge is still standing. I owe him a drink.")));
            m.Books.Add(new(0x02000805, "ExSpellTomeLantern", "Spell Tome: Lantern", "Lantern\nA small light rises from your hand and follows you through dark places, until you rest or the night ends.", Flags: 4));
            m.Books.Add(new(0x02000806, "ExSpellTomeSpark", "Spell Tome: Spark", "Spark", Flags: 4));
            m.Books.Add(new(0x02000807, "ExRecipeStew", "Recipe - Travel Stew", "Leek Salt Potato"));
            m.Books.Add(new(0x02000808, "ExInnNotice", "Notice", P("By order of the innkeeper:", "No weapons at the table. No songs after midnight. No goats (again).")));
            m.Books.Add(new(0x02000809, "ExMapNote", "Map Fragment", ""));
            m.Write(W(@"D:\Modding\Downloads\Example Library\Example Library.esp"));
        }

        // ---- an invented example mod for NPC Revoice
        {
            var t = new BookPluginBuilder();
            t.Masters.Add("Skyrim.esm");
            t.VoiceTypes.Add(new(0x01000C01, "FemaleNord", true));
            t.VoiceTypes.Add(new(0x01000C02, "MaleNord"));
            t.Npcs.Add(new(0x01000D02, "ExTownHilde", "Hilde", 0x01000C01, Female: true));
            t.Npcs.Add(new(0x01000D04, "ExTownGunnar", "Gunnar", 0x01000C02));
            t.Npcs.Add(new(0x01000D06, "ExTownIngrid", "Ingrid", 0x01000C01, Female: true));
            t.Npcs.Add(new(0x01000D08, "ExTownSigrun", "Sigrun", 0x01000C01, Female: true));
            t.Write(W(@"D:\Modding\Downloads\Example Town\Example Town.esp"));
        }

        // ---- library, settings, voice pack, reference
        var pointer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DBVOStudio", "library.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        if (fresh)
        {
            // The first launch: the default library in Documents, only the included ffmpeg and 7-Zip set.
            var fl = new Library(W(@"C:\Users\You\Documents\DBVO Studio"));
            var fs = fl.LoadSettings();
            fs.FfmpegPath = W(@"C:\Program Files\DBVO Studio\Tools\ffmpeg\ffmpeg.exe");
            fs.SevenZipPath = W(@"C:\Program Files\DBVO Studio\Tools\7-Zip\7z.exe");
            fs.Ui["theme"] = "github-dark";
            fl.Save(fs);
            File.WriteAllText(pointer, fl.Root);
            Directory.CreateDirectory(W(@"D:\DBVO"));
            Console.WriteLine("fresh library " + fl.Root);
            return;
        }
        var lib = new Library(W(@"D:\DBVO Studio Library"));
        File.WriteAllText(pointer, lib.Root);
        var s = lib.LoadSettings();
        s.ElevenLabsApiKey = "sk_demo";
        s.FonixDataPath = fonix;
        s.XwmaEncodePath = Path.Combine(game, "Tools/Audio/xWMAEncode.exe");
        s.FfmpegPath = W(@"C:\Program Files\DBVO Studio\Tools\ffmpeg\ffmpeg.exe");
        s.SevenZipPath = W(@"C:\Program Files\DBVO Studio\Tools\7-Zip\7z.exe");
        s.FaceFxWrapperPath = W(@"D:\Modding\Tools\FaceFXWrapper\FaceFXWrapper.exe");
        s.BaseGameFolder = data;
        s.AiProvider = new AiProviderSettings { Endpoint = "http://localhost:11434/v1", Model = "llama3.1" };
        s.Ui["theme"] = "github-dark";
        var refFile = Path.Combine(lib.Root, "references", "Shared lines.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(refFile)!);
        var refLines = new[] { "Never mind.", "Let's talk later.", "Wait here.", "Follow me.", "I need you to do something." };
        File.WriteAllText(refFile, string.Join("\r\n", refLines));
        var rs = new ReferenceSet { Name = "Lines every user has (example)", StoredFile = refFile, LineCount = refLines.Length };
        s.References.Add(rs);
        var voice = new VoicePack
        {
            Name = "Sample Voice", PackId = "SampleVoice", Author = "Your Name", Naming = NamingMode.Dbvo2Hash, Color = "#58a6ff", Engine = "elevenlabs",
            Eleven = new ElevenSettings { VoiceId = "sample01", VoiceName = "Sample Voice", ModelId = "eleven_v4", Stability = 0.5, Similarity = 0.75, Speed = 1, OutputFormat = "pcm_44100", Concurrency = 5 },
            Tags = new() { "[whispers]", "[sighs]", "[laughs]", "[sarcastic]", "[curious]", "[excited]" },
        };
        voice.Destinations.WavLipFolder = W(@"D:\DBVO\Sample Voice\WAV + LIP\{mod}");
        voice.Destinations.FinalRoot = W(@"D:\DBVO\Sample Voice\FOMOD");
        voice.Destinations.FuzTemplate = @"{mod}\Sound\DBVO\{packId}";
        voice.Destinations.FixFolder = W(@"D:\DBVO\Sample Voice\DBVO Fix\{mod}");
        voice.Destinations.ReleaseFolder = W(@"D:\DBVO\Releases");
        voice.Pronunciations.Add(new Pronunciation { Word = "Solstheim", Kind = "alias", Replacement = "SOLS-thyme", WholeWord = true });
        lib.Save(voice);
        var voice2 = new VoicePack
        {
            Name = "Second Voice", PackId = "SecondVoice", Author = "Your Name", Naming = NamingMode.Legacy, Color = "#d29922", Engine = "elevenlabs",
            Eleven = new ElevenSettings { VoiceId = "sample02", VoiceName = "Second Voice", ModelId = "eleven_v4", Stability = 0.5, Similarity = 0.75, Speed = 1, OutputFormat = "pcm_44100", Concurrency = 5 },
            Tags = new() { "[whispers]", "[sighs]", "[laughs]", "[sarcastic]", "[curious]", "[excited]" },
        };
        voice2.Destinations.WavLipFolder = W(@"D:\DBVO\Second Voice\WAV + LIP\{mod}");
        voice2.Destinations.FinalRoot = W(@"D:\DBVO\Second Voice\FOMOD");
        voice2.Destinations.FuzTemplate = @"{mod}\Sound\DBVO\{packId}";
        voice2.Destinations.FixFolder = W(@"D:\DBVO\Second Voice\DBVO Fix\{mod}");
        voice2.Destinations.ReleaseFolder = W(@"D:\DBVO\Releases");
        lib.Save(voice2);
        s.LastVoicePackId = voice.Id;
        s.Pronunciations.Add(new Pronunciation { Word = "Jarl", Kind = "alias", Replacement = "Yarl", WholeWord = true, CaseSensitive = true });
        s.Pronunciations.Add(new Pronunciation { Word = "Whiterun", Kind = "alias", Replacement = "White-run", WholeWord = true });
        lib.Save(s);

        // ---- an NPC Revoice project for the example mod, already scanned
        var rv = new RevoiceProject { Name = "Example Town revoiced", OutputFolder = W(@"D:\Modding\Mods\Example Town Revoiced"), EmotionTags = true };
        rv.Plugins.Add(new RevoicePlugin { Path = W(@"D:\Modding\Downloads\Example Town\Example Town.esp"), FileName = "Example Town.esp", Target = true });
        var bsa = W(@"D:\Modding\Downloads\Example Town\Example Town.bsa");
        File.WriteAllBytes(bsa, new byte[64]);
        rv.Sources.Add(new RevoiceSource { Kind = "bsa", Path = bsa, Priority = 0 });
        rv.Voice.Eleven = new ElevenSettings { ModelId = "eleven_v4", Stability = 0.5, Similarity = 0.75, Speed = 1, OutputFormat = "pcm_44100", Concurrency = 5 };
        rv.Voice.Tags = new() { "[angry]", "[softly]", "[laughs]", "[determined]", "[sighs]", "[whispers]" };
        var hilde = "npc:example town.esp|000D02";
        var gunnar = "npc:example town.esp|000D04";
        var shared = "vt:example town.esp|femalenord";
        rv.Speakers[hilde] = new SpeakerAssignment { Engine = "elevenlabs", VoiceId = "smith01", VoiceName = "Smith (example)" };
        rv.Speakers[gunnar] = new SpeakerAssignment();
        rv.Speakers[shared] = new SpeakerAssignment();
        var npcLines = new (string spk, string name, string npcs, string vt, string topic, string text, int emo, int emoV, string cert, string via)[]
        {
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeGreet", "Need something sharpened? Speak up.", 0, 0, "Exact", "the only speaker of this line"),
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeGreet", "Forge is hot today. Mind your sleeves.", 0, 0, "Exact", "the only speaker of this line"),
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeWork", "Steel takes patience. Most people don't have any.", 0, 0, "Exact", "the only speaker of this line"),
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeWork", "My father ran this forge for forty years.", 4, 40, "Exact", "the only speaker of this line"),
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeTrade", "Iron, steel, a little silver. Take a look.", 0, 0, "Exact", "the only speaker of this line"),
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeThief", "Put that back before I lose my temper!", 1, 80, "Exact", "the only speaker of this line"),
            (hilde, "Hilde", "Hilde (ExTownHilde)", "FemaleNord", "HildeBye", "Come back when it breaks. It always breaks.", 5, 40, "Exact", "the only speaker of this line"),
            (gunnar, "Gunnar", "Gunnar (ExTownGunnar)", "MaleNord", "GunnarGreet", "Welcome to the town. Keep out of trouble.", 0, 0, "Exact", "the only speaker of this line"),
            (gunnar, "Gunnar", "Gunnar (ExTownGunnar)", "MaleNord", "GunnarRoad", "The north road isn't safe after dark.", 3, 35, "Exact", "the only speaker of this line"),
            (gunnar, "Gunnar", "Gunnar (ExTownGunnar)", "MaleNord", "GunnarWolves", "Wolves took two sheep last week.", 4, 30, "Exact", "the only speaker of this line"),
            (shared, "FemaleNord (shared)", "Hilde, Ingrid, Sigrun", "FemaleNord", "TownHello", "Hello there.", 0, 0, "Probable", "several NPCs share this voice type"),
            (shared, "FemaleNord (shared)", "Hilde, Ingrid, Sigrun", "FemaleNord", "TownWeather", "Cold one today, isn't it?", 0, 0, "Probable", "several NPCs share this voice type"),
            (shared, "FemaleNord (shared)", "Hilde, Ingrid, Sigrun", "FemaleNord", "TownGuards", "The guards are always watching.", 2, 30, "Probable", "several NPCs share this voice type"),
            (shared, "FemaleNord (shared)", "Hilde, Ingrid, Sigrun", "FemaleNord", "TownMarket", "Market day is the busiest day of the week.", 0, 0, "Probable", "several NPCs share this voice type"),
        };
        uint info = 0x000D20;
        foreach (var x in npcLines)
        {
            var stem = $"ExTownDialog_{x.topic}_{info:X8}_1";
            var l = new RevoiceLine
            {
                LineId = NewId.FromKey("rv" + stem), Info = $"example town.esp|{info:X6}", Response = 1, PluginFolder = "Example Town.esp",
                VoiceType = x.vt, VoiceTypeKey = "skyrim.esm|" + x.vt.ToLowerInvariant(), FileStem = stem, Key = stem.ToUpperInvariant(),
                FinalLine = x.text, OriginalLine = x.text, Spoken = x.text, Emotion = x.emo, EmotionValue = x.emoV,
                SpeakerKey = x.spk, SpeakerName = x.name, Npcs = x.npcs, Certainty = x.cert, Via = x.via,
                Quest = "ExTownDialogue", Topic = x.topic, Plugin = "Example Town.esp",
                Original = $"Example Town.bsa: sound\\voice\\example town.esp\\{x.vt.ToLowerInvariant()}\\{stem.ToLowerInvariant()}.fuz",
            };
            if (RevoiceService.EmotionTagFor(x.emo, x.emoV) is { } tag) l.TagSuggestion = tag + " " + x.text;
            rv.Lines.Add(l);
            info += 2;
        }
        rv.LastScan = new RevoiceScanReport { Infos = 14, Responses = 14, Lines = 14, WithOriginal = 14, VoiceFiles = 14, ScannedUtc = DateTime.UtcNow.AddMinutes(-20) };
        lib.Save(rv);
        BigRevoice(lib);
        Console.WriteLine("library " + lib.Root);
    }

    // A big invented quest mod, already scanned: many speakers, many lines.
    static void BigRevoice(Library lib)
    {
        var rv = new RevoiceProject { Name = "The Hollow Crown revoiced", OutputFolder = W(@"D:\Modding\Mods\The Hollow Crown Revoiced"), EmotionTags = true };
        var esp = W(@"D:\Modding\Downloads\The Hollow Crown\The Hollow Crown.esp");
        Plugin(esp, new[] { "Skyrim.esm" }, Array.Empty<(string, string)>(), 0x01000E00);
        rv.Plugins.Add(new RevoicePlugin { Path = esp, FileName = "The Hollow Crown.esp", Target = true });
        var bsa = W(@"D:\Modding\Downloads\The Hollow Crown\The Hollow Crown.bsa");
        File.WriteAllBytes(bsa, new byte[64]);
        rv.Sources.Add(new RevoiceSource { Kind = "bsa", Path = bsa, Priority = 0 });
        rv.Voice.Eleven = new ElevenSettings { ModelId = "eleven_v4", Stability = 0.5, Similarity = 0.75, Speed = 1, OutputFormat = "pcm_44100", Concurrency = 5 };
        rv.Voice.Tags = new() { "[angry]", "[softly]", "[laughs]", "[determined]", "[sighs]", "[whispers]" };
        var npcs = new (string name, string vt, int n)[]
        {
            ("Queen Ysolde", "FemaleCommander", 164), ("Lord Varric", "MaleNobleNord", 142), ("Sister Maelis", "FemaleElfHaughty", 121), ("Captain Hrolf", "MaleCommander", 118),
            ("Tamsin the Fence", "FemaleSultry", 96), ("Old Bjarni", "MaleOldGrumpy", 88), ("Merchant Ja'zirr", "MaleKhajiit", 74), ("Archmage Corvane", "MaleElfHaughty", 71),
            ("Wenna", "FemaleYoungEager", 66), ("The Hollow King", "MaleDrained", 63), ("Brother Aldo", "MaleCondescending", 58), ("Innkeeper Svana", "FemaleNord", 52),
            ("Ser Galen", "MaleBrute", 47), ("Little Pip", "FemaleChild", 41), ("Guard Captain Orla", "FemaleCommander", 38),
        };
        var subj = new[] { "The crown", "The old king", "The vault", "My brother", "The council", "That ring", "The northern road", "The temple", "Your sword", "The ferry", "This city", "The letter", "The prisoner", "The ritual", "Our banner" };
        var verb = new[] { "was never meant for", "belongs to", "will not wait for", "has nothing to do with", "was stolen from", "is the last hope of", "was promised to", "will be the ruin of", "still remembers", "has been waiting for" };
        var obj = new[] { "people like us.", "the Hollow King.", "anyone in this hall.", "a common thief.", "the dead.", "the Jarl's court.", "you, traveler.", "the mages of the college.", "my family.", "the old gods." };
        var emo = new[] { 0, 0, 0, 1, 2, 3, 4, 5, 0, 0 };
        int seed = 7, info = 0x100;
        int Next() { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed; }
        foreach (var (name, vt, n) in npcs)
        {
            var ed = "HC" + new string(name.Where(char.IsLetter).ToArray());
            var key = $"npc:the hollow crown.esp|{0x000F00 + info:X6}";
            rv.Speakers[key] = new SpeakerAssignment();
            for (int i = 0; i < n; i++)
            {
                var text = $"{subj[Next() % subj.Length]} {verb[Next() % verb.Length]} {obj[Next() % obj.Length]}";
                var e = emo[Next() % emo.Length];
                var topic = ed + "Topic" + (i / 6);
                var stem = $"HCDialog_{topic}_{info:X8}_1";
                var l = new RevoiceLine
                {
                    LineId = NewId.FromKey("hc" + stem), Info = $"the hollow crown.esp|{info:X6}", Response = 1, PluginFolder = "The Hollow Crown.esp",
                    VoiceType = vt, VoiceTypeKey = "skyrim.esm|" + vt.ToLowerInvariant(), FileStem = stem, Key = stem.ToUpperInvariant(),
                    FinalLine = text, OriginalLine = text, Spoken = text, Emotion = e, EmotionValue = e == 0 ? 0 : 50,
                    SpeakerKey = key, SpeakerName = name, Npcs = $"{name} ({ed})", Certainty = "Exact", Via = "the only speaker of this line",
                    Quest = "HCMainQuest", Topic = topic, Plugin = "The Hollow Crown.esp",
                    Original = $"The Hollow Crown.bsa: sound\\voice\\the hollow crown.esp\\{vt.ToLowerInvariant()}\\{stem.ToLowerInvariant()}.fuz",
                };
                if (RevoiceService.EmotionTagFor(e, l.EmotionValue) is { } tag) l.TagSuggestion = tag + " " + text;
                rv.Lines.Add(l);
                info++;
            }
        }
        rv.LastScan = new RevoiceScanReport { Infos = rv.Lines.Count, Responses = rv.Lines.Count, Lines = rv.Lines.Count, WithOriginal = rv.Lines.Count, VoiceFiles = rv.Lines.Count, ScannedUtc = DateTime.UtcNow.AddMinutes(-35) };
        lib.Save(rv);
    }
}
