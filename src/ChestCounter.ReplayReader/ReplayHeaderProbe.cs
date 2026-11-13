using System.Text;

namespace ChestCounter.ReplayReader;

public enum ReplayFileState
{
    /// <summary>Finished replay with its decryption key: can be analysed.</summary>
    Complete,
    /// <summary>Still being recorded, or the game closed before writing the key.</summary>
    InProgress,
    /// <summary>Not a replay file / truncated header.</summary>
    Invalid,
}

public sealed record ReplayHeaderInfo(
    ReplayFileState State,
    DateTime? RecordedLocalTime,
    uint LengthMs,
    bool IsLive,
    bool IsEncrypted,
    bool HasKey,
    string? Problem);

/// <summary>
/// Reads only the small file-info block at the start of a .replay file (a few hundred bytes), to get the
/// match date and completion state cheaply. Read-only, shared access, never throws.
/// </summary>
public static class ReplayHeaderProbe
{
    private const uint FileMagic = 0x1CA2E27F;

    public static ReplayHeaderInfo Probe(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length < 32)
            {
                return Invalid(fs.Length == 0 ? "The file is empty (recording just started)." : "The file is too small to be a replay.", inProgress: fs.Length == 0);
            }

            var size = (int)Math.Min(fs.Length, 64 * 1024);
            var buffer = new byte[size];
            fs.ReadExactly(buffer);
            return Parse(buffer);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Invalid("The file no longer exists.");
        }
        catch (Exception ex)
        {
            // Sharing violations etc.: the game is probably still writing it - try again later.
            return Invalid("Could not open the file: " + ex.Message, inProgress: ex is IOException);
        }
    }

    public static ReplayHeaderInfo Parse(ReadOnlySpan<byte> data)
    {
        var r = new SpanReader(data);
        try
        {
            if (r.U32() != FileMagic)
            {
                return Invalid("Not a Fortnite replay (wrong file signature).");
            }

            var fileVersion = r.U32();
            if (fileVersion >= 7)
            {
                var customVersions = r.I32();
                if (customVersions is < 0 or > 1000) return Invalid("Corrupt header (custom versions).");
                r.Skip(customVersions * 20);
            }

            var lengthMs = r.U32();
            r.U32(); // network version
            r.U32(); // changelist
            r.FString(); // friendly name
            var isLive = r.U32() != 0;

            DateTime? timestamp = null;
            if (fileVersion >= 3)
            {
                var ticks = r.I64();
                if (ticks > DateTime.MinValue.Ticks && ticks < DateTime.MaxValue.Ticks)
                {
                    timestamp = new DateTime(ticks & 0x3FFFFFFFFFFFFFFF, DateTimeKind.Unspecified);
                }
            }

            if (fileVersion >= 2)
            {
                r.U32(); // compressed
            }

            var encrypted = false;
            var hasKey = false;
            if (fileVersion >= 6)
            {
                encrypted = r.U32() != 0;
                var keyLen = r.I32();
                if (keyLen is < 0 or > 1024) return Invalid("Corrupt header (key).");
                var key = r.Bytes(keyLen);
                hasKey = keyLen > 0 && key.IndexOfAnyExcept((byte)0) >= 0;
            }

            var inProgress = isLive || (encrypted && !hasKey);
            return new ReplayHeaderInfo(inProgress ? ReplayFileState.InProgress : ReplayFileState.Complete,
                timestamp, lengthMs, isLive, encrypted, hasKey,
                inProgress ? "The match is still being recorded (or Fortnite closed before finishing the replay)." : null);
        }
        catch (EndOfStreamException)
        {
            return Invalid("The replay header is truncated.", inProgress: true);
        }
    }

    private static ReplayHeaderInfo Invalid(string problem, bool inProgress = false) =>
        new(inProgress ? ReplayFileState.InProgress : ReplayFileState.Invalid, null, 0, false, false, false, problem);

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public SpanReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || _pos + n > _data.Length) throw new EndOfStreamException();
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }

        public uint U32() => BitConverter.ToUInt32(Take(4));
        public int I32() => BitConverter.ToInt32(Take(4));
        public long I64() => BitConverter.ToInt64(Take(8));
        public void Skip(int n) => Take(n);
        public ReadOnlySpan<byte> Bytes(int n) => Take(n);

        public string FString()
        {
            var len = I32();
            if (len == 0) return "";
            if (len < 0)
            {
                if (len < -100_000) throw new EndOfStreamException();
                return Encoding.Unicode.GetString(Take(-len * 2)).TrimEnd('\0');
            }
            if (len > 100_000) throw new EndOfStreamException();
            return Encoding.ASCII.GetString(Take(len)).TrimEnd('\0');
        }
    }
}
