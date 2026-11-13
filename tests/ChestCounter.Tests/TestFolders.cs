namespace ChestCounter.Tests;

/// <summary>Temporary folders for tests, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ChestCounterTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(100);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(100);
            }
        }
    }
}

/// <summary>Locates the real Fortnite replay folder (integration tests are skipped when it is absent).</summary>
internal static class RealReplays
{
    public static string Folder =>
        Environment.GetEnvironmentVariable("CHESTCOUNTER_TEST_DEMOS")
        ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    /// <summary>Newest finished replays (header says complete), newest first.</summary>
    public static IReadOnlyList<string> Complete(int take)
    {
        if (!Directory.Exists(Folder)) return [];
        return Directory.GetFiles(Folder, "*.replay")
            .OrderByDescending(System.IO.File.GetLastWriteTimeUtc)
            .Where(f => ChestCounter.ReplayReader.ReplayHeaderProbe.Probe(f).State == ChestCounter.ReplayReader.ReplayFileState.Complete)
            .Take(take)
            .ToList();
    }
}
