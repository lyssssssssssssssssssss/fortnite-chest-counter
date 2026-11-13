using System.Text;
using ChestCounter.ReplayReader;

namespace ChestCounter.Tests;

/// <summary>Corrupt, empty, truncated and in-progress files must never crash anything.</summary>
public class RobustnessTests
{
    /// <summary>Builds a SYNTHETIC replay file-info header (no data chunks).</summary>
    private static byte[] Header(DateTime recorded, bool live, byte[] key)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write(0x1CA2E27Fu);      // magic
        w.Write(7u);               // file version (custom versions)
        w.Write(1);                // custom version count
        w.Write(new byte[16]); w.Write(7);
        w.Write(900_000u);         // length ms
        w.Write(1u); w.Write(1u);  // network version, changelist
        var name = Encoding.ASCII.GetBytes("Unsaved Replay\0");
        w.Write(name.Length); w.Write(name);
        w.Write(live ? 1u : 0u);
        w.Write(recorded.Ticks);
        w.Write(1u);               // compressed
        w.Write(1u);               // encrypted
        w.Write(key.Length); w.Write(key);
        w.Write(new byte[64]);     // padding where chunks would start
        return ms.ToArray();
    }

    [Fact]
    public void Header_probe_reads_the_recorded_match_date()
    {
        var key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
        var info = ReplayHeaderProbe.Parse(Header(new DateTime(2026, 11, 9, 20, 45, 20), false, key));
        Assert.Equal(ReplayFileState.Complete, info.State);
        Assert.Equal(new DateTime(2026, 11, 9, 20, 45, 20), info.RecordedLocalTime);
        Assert.Equal(900_000u, info.LengthMs);
    }

    [Fact]
    public void Header_probe_flags_matches_still_in_progress()
    {
        Assert.Equal(ReplayFileState.InProgress, ReplayHeaderProbe.Parse(Header(DateTime.Now, true, new byte[32])).State);
        Assert.Equal(ReplayFileState.InProgress, ReplayHeaderProbe.Parse(Header(DateTime.Now, false, new byte[32])).State); // key not written yet
    }

    [Fact]
    public void Header_probe_rejects_garbage_and_handles_empty_files()
    {
        using var dir = new TempDir();
        var rnd = new Random(1234);
        var junk = new byte[4096];
        rnd.NextBytes(junk);
        System.IO.File.WriteAllBytes(dir.File("junk.replay"), junk);
        System.IO.File.WriteAllBytes(dir.File("empty.replay"), []);

        Assert.Equal(ReplayFileState.Invalid, ReplayHeaderProbe.Probe(dir.File("junk.replay")).State);
        Assert.Equal(ReplayFileState.InProgress, ReplayHeaderProbe.Probe(dir.File("empty.replay")).State);
        Assert.Equal(ReplayFileState.Invalid, ReplayHeaderProbe.Probe(dir.File("missing.replay")).State);
    }

    [Fact]
    public void Reader_never_throws_on_corrupt_files()
    {
        using var dir = new TempDir();
        var rnd = new Random(42);
        var reader = new FactsReplayReader();
        for (var i = 0; i < 5; i++)
        {
            var bytes = new byte[rnd.Next(10, 200_000)];
            rnd.NextBytes(bytes);
            var path = dir.File($"c{i}.replay");
            System.IO.File.WriteAllBytes(path, bytes);
            var facts = reader.Read(path);
            Assert.NotNull(facts.Error);
        }

        // Valid header followed by garbage
        var fake = Header(new DateTime(2026, 11, 9), false, Enumerable.Repeat((byte)7, 32).ToArray())
            .Concat(Enumerable.Range(0, 5000).Select(i => (byte)(i * 31))).ToArray();
        System.IO.File.WriteAllBytes(dir.File("fake.replay"), fake);
        var f = reader.Read(dir.File("fake.replay"));
        Assert.Empty(f.ContainerUpdates);
    }

    [Fact]
    public void Reader_survives_truncated_real_replays()
    {
        var real = RealReplays.Complete(1);
        if (real.Count == 0) return; // no Fortnite replays on this machine
        using var dir = new TempDir();
        var bytes = System.IO.File.ReadAllBytes(real[0]);
        var reader = new FactsReplayReader();
        foreach (var fraction in new[] { 0.01, 0.3, 0.77 })
        {
            var path = dir.File($"cut{fraction}.replay");
            System.IO.File.WriteAllBytes(path, bytes.AsSpan(0, (int)(bytes.Length * fraction)).ToArray());
            var facts = reader.Read(path); // must not throw
            Assert.NotNull(facts);
        }
    }
}
