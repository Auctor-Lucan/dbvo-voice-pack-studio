using DbvoStudio.Core.Audio;
using DbvoStudio.Core.Naming;
using DbvoStudio.Core.Projects;

namespace DbvoStudio.Core.Tests;

public class AdoptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbvo-adopt-" + Guid.NewGuid().ToString("N")[..8]);
    public AdoptTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void Existing_wav_and_lip_become_takes_on_sync()
    {
        var lib = new Library(Path.Combine(_dir, "lib"));
        var settings = lib.LoadSettings();
        var mod = new Mod { Name = "Serana" };
        mod.Plugins.Add(new ModPlugin
        {
            FileName = "Serana.esp",
            Lines =
            {
                new Phase1Line { FormId = 1, Original = "Follow me.", Final = "Follow me.", Kind = Rules.LineKind.Valid },
                new Phase1Line { FormId = 2, Original = "Wait here.", Final = "Wait here.", Kind = Rules.LineKind.Valid },
                new Phase1Line { FormId = 3, Original = "Only a wav.", Final = "Only a wav.", Kind = Rules.LineKind.Valid },
            },
        });
        lib.Save(mod);
        var voice = new VoicePack { Name = "Vampira", PackId = "vampira" };
        var wavLip = Path.Combine(_dir, "wavlip");
        Directory.CreateDirectory(wavLip);
        var wav = new WavFile { SampleRate = 44100, Channels = 1, Samples = new short[44100] };
        wav.Write(Path.Combine(wavLip, LineNaming.Stem("Follow me.", NamingMode.Legacy) + ".wav"));
        File.WriteAllBytes(Path.Combine(wavLip, LineNaming.Stem("Follow me.", NamingMode.Legacy) + ".lip"), new byte[] { 1, 2, 3 });
        // the other naming mode is found too
        wav.Write(Path.Combine(wavLip, LineNaming.Stem("Wait here.", NamingMode.Dbvo2Hash) + ".wav"));
        File.WriteAllBytes(Path.Combine(wavLip, LineNaming.Stem("Wait here.", NamingMode.Dbvo2Hash) + ".lip"), new byte[] { 1 });
        wav.Write(Path.Combine(wavLip, LineNaming.Stem("Only a wav.", NamingMode.Legacy) + ".wav"));

        var patch = new DbvoPatch { Name = "Serana", VoicePackId = voice.Id, ModIds = { mod.Id }, WavLipFolder = wavLip };
        var rep = new Phase2Service(lib, settings).Sync(patch, voice, new[] { mod });

        Assert.Equal(2, rep.Adopted);
        var follow = patch.Lines.Single(l => l.FinalLine == "Follow me.");
        Assert.Equal(Phase2Status.Generated, follow.Status);
        Assert.True(File.Exists(Path.Combine(lib.PatchFolder(patch.Id), follow.SelectedTake!.WavPath)));
        Assert.Equal(1.0, follow.SelectedTake.DurationSeconds, 2);
        Assert.Empty(patch.Lines.Single(l => l.FinalLine == "Only a wav.").Takes);
        // a second sync does not add the same files again
        Assert.Equal(0, new Phase2Service(lib, settings).Sync(patch, voice, new[] { mod }).Adopted);
        Assert.Single(follow.Takes);
    }
}
