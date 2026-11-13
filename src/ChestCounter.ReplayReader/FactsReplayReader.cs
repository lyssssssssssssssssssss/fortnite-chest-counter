using System.Diagnostics;
using System.Security.Cryptography;
using Unreal.Core;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace ChestCounter.ReplayReader;

/// <summary>
/// The replay's spectator controller sends an extra NetPlayerIndex byte when its channel opens.
/// Unreal.Core needs this marker to skip it (otherwise that channel's first bunch is misread).
/// </summary>
[PlayerController("BP_ReplayPC_Athena_C")]
internal sealed class ReplayPlayerControllerMarker
{
}

/// <summary>
/// Reads a Fortnite .replay file (read-only) and records raw facts: container property updates,
/// container channel closes, the recorder's PlayerState, pawn ownership, playlist and match placement.
///
/// Every replicated property is self-described in the replay (handle -> name, payload bit count), so
/// this reader decodes the handful of properties it needs *by name* and skips everything else. No
/// hard-coded class layouts, which keeps it working when Epic adds new chest blueprints.
/// </summary>
public sealed class FactsReplayReader : ReplayReader<Replay>
{
    private static readonly string[] DiagnosticKeywords =
    [
        "Chest", "Container", "Playlist", "Gum", "Gomu", "Fruit", "OnePiece", "Luffy", "Devil",
        "Treasure", "Relic", "Sprite", "Poster", "Booty", "Extraction", "Cannon", "Ammo", "Food",
        "Ranked", "Habanero", "Showdown", "Tournament", "Arena", "Blastberry", "Reload", "Figment",
        "NoBuild", "NoBuildBR", "Juno", "Creative", "Respawn",
    ];

    private readonly bool _diagnostics;
    private ReplayFacts _facts = null!;

    // Per-replay state
    private readonly HashSet<uint> _channelsWithData = new();
    private readonly Dictionary<uint, string> _containerClassByActor = new();
    private readonly Dictionary<string, string> _classNameCache = new(StringComparer.Ordinal);
    private readonly HashSet<uint> _pendingPlaylistIds = new();
    private readonly List<(float Time, uint Pawn, uint Weapon)> _weapons = new();
    private readonly Dictionary<uint, uint> _weaponItemIds = new();

    public FactsReplayReader(bool diagnostics = false) : base(null!, ParseMode.Minimal)
    {
        _diagnostics = diagnostics;
    }

    /// <summary>Reads one replay. Never throws: problems are reported in <see cref="ReplayFacts.Error"/>.</summary>
    public ReplayFacts Read(string path)
    {
        var sw = Stopwatch.StartNew();
        var info = new FileInfo(path);
        _facts = new ReplayFacts
        {
            FilePath = path,
            FileSize = info.Exists ? info.Length : 0,
            FileLastWriteUtc = info.Exists ? info.LastWriteTimeUtc : DateTime.MinValue,
            Diagnostics = _diagnostics ? new ReplayDiagnostics() : null,
        };
        _channelsWithData.Clear();
        _containerClassByActor.Clear();
        _pendingPlaylistIds.Clear();
        _weapons.Clear();
        _weaponItemIds.Clear();

        try
        {
            var bytes = ReadShared(path);
            using var archive = new Unreal.Core.BinaryReader(bytes.AsMemory());
            ReadReplay(archive); // calls Cleanup() at the end, which snapshots what we need
            _facts.Completed = !archive.IsError;
            if (archive.IsError)
            {
                _facts.Error ??= "The replay ended unexpectedly (truncated or still being written).";
            }
        }
        catch (Exception ex)
        {
            _facts.Error = ex.Message;
            try { Cleanup(); } catch { /* state is reset on the next read anyway */ }
        }
        finally
        {
            sw.Stop();
            _facts.ParseDuration = sw.Elapsed;
        }

        return _facts;
    }

    /// <summary>Fortnite may still hold the file open while writing; allow concurrent writers and never lock it.</summary>
    internal static byte[] ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[fs.Length];
        fs.ReadExactly(buffer);
        return buffer;
    }

    // ------------------------------------------------------------------------------------------------
    // Info / header
    // ------------------------------------------------------------------------------------------------

    public override void ReadReplayInfo(FArchive archive)
    {
        base.ReadReplayInfo(archive); // throws for live+encrypted (match still running)
        var info = Replay.Info;
        _facts.RecordedLocalTime = info.Timestamp == default ? null : DateTime.SpecifyKind(info.Timestamp, DateTimeKind.Unspecified);
        _facts.LengthMs = info.LengthInMs;
        _facts.IsLive = info.IsLive;
        _facts.IsEncrypted = info.IsEncrypted;
        _facts.HasEncryptionKey = info.EncryptionKey.Length > 0 && info.EncryptionKey.Span.IndexOfAnyExcept((byte)0) >= 0;
        _facts.FriendlyName = info.FriendlyName?.Trim();

        if (info.IsEncrypted && !_facts.HasEncryptionKey)
        {
            throw new InvalidDataException("The replay has no decryption key yet (the match is still in progress or the game closed mid-match).");
        }
    }

    public override void ReadReplayHeader(FArchive archive)
    {
        base.ReadReplayHeader(archive);
        var h = Replay.Header;
        _facts.Branch = h.Branch;
        _facts.ReplayGuid = string.IsNullOrWhiteSpace(h.Guid) ? null : h.Guid;
        _facts.MapPath = h.LevelNamesAndTimes?.FirstOrDefault().Item1;
        if (h.GameSpecificData is not null)
        {
            _facts.GameSpecificData.AddRange(h.GameSpecificData);
        }
        _facts.EngineNetworkVersion = h.EngineNetworkVersion.ToString();
        _facts.NetworkVersion = h.NetworkVersion.ToString();
    }

    // ------------------------------------------------------------------------------------------------
    // Events (encrypted): placement / total players / eliminations of the recorder
    // ------------------------------------------------------------------------------------------------

    public override void ReadEvent(FArchive archive)
    {
        var id = archive.ReadFString();
        var group = archive.ReadFString();
        var metadata = archive.ReadFString();
        archive.ReadUInt32(); // start time
        archive.ReadUInt32(); // end time
        var size = archive.ReadInt32();

        _facts.Diagnostics?.EventGroups.Add($"{group}/{metadata}");

        if (size <= 0)
        {
            return;
        }

        if (metadata is not ("AthenaMatchStats" or "AthenaMatchTeamStats"))
        {
            archive.SkipBytes(size);
            return;
        }

        try
        {
            using var decrypted = DecryptBuffer(archive, size);
            if (metadata == "AthenaMatchTeamStats")
            {
                decrypted.ReadUInt32(); // unknown
                _facts.Placement = (int)decrypted.ReadUInt32();
                _facts.TotalPlayers = (int)decrypted.ReadUInt32();
            }
            else
            {
                decrypted.ReadUInt32(); // unknown
                decrypted.ReadSingle(); // accuracy
                decrypted.ReadUInt32(); // assists
                _facts.Eliminations = (int)decrypted.ReadUInt32();
            }
        }
        catch
        {
            // Optional information; never fail the replay because of it.
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Actor channels
    // ------------------------------------------------------------------------------------------------

    protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor)
    {
        _channelsWithData.Remove(channelIndex);
        if (_facts.Diagnostics is { } d && actor is not null)
        {
            var cls = ClassNameOfChannel(channelIndex);
            if (cls is not null)
            {
                d.ActorChannels[cls] = d.ActorChannels.GetValueOrDefault(cls) + 1;
            }
        }
    }

    protected override void OnChannelClosed(uint channelIndex, NetworkGUID? actor, ChannelCloseReason reason)
    {
        _channelsWithData.Remove(channelIndex);
        if (_facts.Diagnostics is { } d)
        {
            var key = actor is null ? $"{reason}:noactor" : _containerClassByActor.ContainsKey(actor.Value) ? $"{reason}:container" : reason.ToString();
            d.CloseReasons[key] = d.CloseReasons.GetValueOrDefault(key) + 1;
        }

        if (actor is null || !_containerClassByActor.TryGetValue(actor.Value, out var cls))
        {
            return;
        }

        _facts.ContainerCloses.Add(new ContainerClose(CurrentFrameTime, ContainerKey(actor.Value), cls, reason.ToString()));
    }

    protected override void OnUnresolvedReplicator(uint channelIndex, uint? repObject)
    {
        if (_facts.Diagnostics is not { } d)
        {
            return;
        }

        var path = repObject is uint r && _netGuidCache.TryGetPathName(r, out var p) ? p : $"#{repObject}";
        d.Unresolved[path] = d.Unresolved.GetValueOrDefault(path) + 1;
    }

    // ------------------------------------------------------------------------------------------------
    // Replicated properties (decoded by name; everything else skipped)
    // ------------------------------------------------------------------------------------------------

#pragma warning disable CS8765 // nullability of out parameter differs from base
    public override bool ReceiveProperties(FBitArchive archive, NetFieldExportGroup group, uint channelIndex,
        out INetFieldExportGroup? exportGroup, bool enablePropertyChecksum = true, bool netDeltaUpdate = false)
#pragma warning restore CS8765
    {
        exportGroup = null;
        var channel = Channels[channelIndex];
        if (channel is null)
        {
            return false;
        }

        if (enablePropertyChecksum)
        {
            archive.ReadBit();
        }

        var isActorGroup = channel.Actor is not null && IsActorOwnGroup(channel, group);
        var className = ClassName(group.PathName);
        var looksLikeContainer = isActorGroup && LooksLikeContainer(group.PathName, className);

        uint? searchingPawn = null, searchAnim = null, playerState = null, recorder = null, currentWeapon = null, weaponData = null;
        bool? searched = null, destroyed = null;
        int? teamIndex = null;
        string? playerName = null, accountId = null;
        var sawContainerField = false;

        while (true)
        {
            var handle = archive.ReadIntPacked();
            if (handle == 0)
            {
                break;
            }

            handle--;
            if (!group.IsValidIndex(handle))
            {
                return false;
            }

            var numBits = (int)archive.ReadIntPacked();
            if (numBits == 0)
            {
                continue;
            }

            if (archive.IsError || numBits > archive.GetBitsLeft())
            {
                return false;
            }

            var name = group.NetFieldExports[handle]?.Name;
            var start = archive.GetBitsLeft();

            if (isActorGroup && name is not null)
            {
                switch (name)
                {
                    case "SearchingPawn":
                        searchingPawn = archive.ReadIntPacked();
                        sawContainerField = true;
                        break;
                    case "bAlreadySearched":
                        searched = archive.ReadBit();
                        sawContainerField = true;
                        break;
                    case "SearchAnimationCount" when numBits <= 32:
                        searchAnim = (uint)archive.ReadBitsToInt(numBits);
                        sawContainerField = true;
                        break;
                    case "bDestroyed" when looksLikeContainer:
                        destroyed = archive.ReadBit();
                        break;
                    case "PlayerState":
                        playerState = archive.ReadIntPacked();
                        break;
                    case "RecorderPlayerState":
                        recorder = archive.ReadIntPacked();
                        break;
                    case "TeamIndex" when numBits <= 32:
                        teamIndex = archive.ReadBitsToInt(numBits);
                        break;
                    case "CurrentWeapon":
                        currentWeapon = archive.ReadIntPacked();
                        break;
                    case "WeaponData":
                        weaponData = archive.ReadIntPacked();
                        break;
                    case "PlayerNamePrivate" when numBits >= 32:
                        try
                        {
                            var nb = new NetBitReader(archive.ReadBits(numBits).ToArray(), numBits);
                            var s = nb.ReadFString();
                            playerName = nb.IsError ? null : s;
                        }
                        catch
                        {
                            playerName = null;
                        }
                        break;
                    case "UniqueID" or "UniqueId" when numBits >= 8:
                        try
                        {
                            var nb = new NetBitReader(archive.ReadBits(numBits).ToArray(), numBits)
                            {
                                EngineNetworkVersion = archive.EngineNetworkVersion,
                                NetworkVersion = archive.NetworkVersion,
                            };
                            accountId = nb.SerializePropertyNetId();
                        }
                        catch
                        {
                            accountId = null;
                        }
                        break;
                }

                if (_facts.Diagnostics is { } d && (looksLikeContainer || sawContainerField) && archive.GetBitsLeft() == start)
                {
                    // Sample raw field sizes of container groups so unknown fields can be investigated.
                    var key = $"{className}.{name}";
                    if (!d.PropertySamples.TryGetValue(key, out var list))
                    {
                        d.PropertySamples[key] = list = new List<string>();
                    }
                    if (list.Count < 3)
                    {
                        list.Add($"{numBits}b");
                    }
                }
            }

            var consumed = start - archive.GetBitsLeft();
            if (consumed > numBits || archive.IsError)
            {
                return false;
            }
            if (consumed < numBits)
            {
                archive.SkipBits(numBits - consumed);
            }
        }

        if (!isActorGroup)
        {
            return true;
        }

        var actorId = channel.ActorId!.Value;

        if (looksLikeContainer || sawContainerField)
        {
            var firstSeen = _channelsWithData.Add(channelIndex);
            _containerClassByActor.TryAdd(actorId, className);
            if (firstSeen && _facts.Diagnostics is { } dd)
            {
                dd.ContainerChannelOpens++;
            }
            if (searchingPawn is not null || searched is not null || destroyed is not null || searchAnim is not null || firstSeen)
            {
                var loc = channel.Actor!.Location;
                var dynamic = channel.Actor.ActorNetGUID.IsDynamic();
                _facts.ContainerUpdates.Add(new ContainerUpdate(
                    CurrentFrameTime, ContainerKey(actorId), className, dynamic,
                    dynamic ? (float?)loc?.X : null, dynamic ? (float?)loc?.Y : null, dynamic ? (float?)loc?.Z : null,
                    searchingPawn, searched, destroyed, searchAnim, firstSeen));
            }
        }
        else
        {
            _channelsWithData.Add(channelIndex);
        }

        if (playerState is > 0)
        {
            if (className.Contains("ReplayPC", StringComparison.Ordinal))
            {
                _facts.ControllerPlayerState ??= playerState;
            }
            else if (!className.Contains("Controller", StringComparison.Ordinal))
            {
                _facts.PawnToPlayerState[actorId] = playerState.Value;
            }
        }

        if (recorder is > 0)
        {
            _facts.RecorderPlayerState ??= recorder;
        }

        if (teamIndex is not null)
        {
            _facts.PlayerStateTeam[actorId] = teamIndex.Value;
        }

        if (!string.IsNullOrWhiteSpace(playerName) && playerName.Length <= 64)
        {
            _facts.PlayerStateName[actorId] = playerName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(accountId) && accountId.Length <= 128)
        {
            _facts.PlayerStateAccount[actorId] = accountId;
        }

        if (currentWeapon is not null)
        {
            _weapons.Add((CurrentFrameTime, actorId, currentWeapon.Value));
        }

        if (weaponData is > 0)
        {
            _weaponItemIds[actorId] = weaponData.Value;
        }

        return true;
    }

    /// <summary>
    /// True when the group describes the channel's actor itself (not one of its components/sub-objects).
    /// </summary>
    private bool IsActorOwnGroup(UChannel channel, NetFieldExportGroup group)
    {
        var id = channel.ArchetypeId ?? channel.ActorId;
        return id is uint guid && ReferenceEquals(_netGuidCache.GetNetFieldExportGroup(guid), group);
    }

    // ------------------------------------------------------------------------------------------------
    // ClassNetCache fields: playlist (custom struct); everything else (RPCs, fast arrays) is skipped.
    // ------------------------------------------------------------------------------------------------

    protected override bool TryReceiveClassNetCacheField(FBitArchive archive, NetFieldExportGroup classNetCache, NetFieldExport field, uint channelIndex)
    {
        if (_facts.Diagnostics is { } d)
        {
            var path = classNetCache.PathName;
            if (path.Contains("GameState", StringComparison.Ordinal) || path.Contains("Chest", StringComparison.Ordinal)
                || path.Contains("Container", StringComparison.Ordinal) || path.Contains("PlayerPawn", StringComparison.Ordinal))
            {
                var key = $"{path}.{field.Name}";
                d.ClassNetCacheFields[key] = d.ClassNetCacheFields.GetValueOrDefault(key) + 1;
            }
        }

        if (field.Name == "CurrentPlaylistInfo" && classNetCache.PathName.Contains("GameState", StringComparison.Ordinal))
        {
            try
            {
                // FPlaylistPropertyArray::NetSerialize (same layout as FortniteReplayReader's PlaylistInfo)
                if (archive.EngineNetworkVersion >= EngineNetworkVersionHistory.HISTORY_FAST_ARRAY_DELTA_STRUCT)
                {
                    archive.ReadBit();
                }
                archive.ReadBit();
                var id = archive.ReadIntPacked();
                if (!archive.IsError && id > 0)
                {
                    _pendingPlaylistIds.Add(id);
                    ResolvePlaylist(id);
                }
            }
            catch
            {
                // bounded by the field payload; ignore malformed data
            }
        }

        return true; // consumed (the base restores the read position to the end of the payload)
    }

    private void ResolvePlaylist(uint id)
    {
        if (_netGuidCache.TryGetPathName(id, out var path) && !string.IsNullOrWhiteSpace(path))
        {
            if (!_facts.Playlists.Contains(path))
            {
                _facts.Playlists.Add(path);
            }
            _pendingPlaylistIds.Remove(id);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // End of replay: resolve NetGUIDs while the cache is still populated
    // ------------------------------------------------------------------------------------------------

    protected override void Cleanup()
    {
        try
        {
            foreach (var id in _pendingPlaylistIds.ToArray())
            {
                ResolvePlaylist(id);
            }

            foreach (var (actor, itemId) in _weaponItemIds)
            {
                if (_netGuidCache.TryGetPathName(itemId, out var item))
                {
                    _facts.WeaponActorItem[actor] = item;
                }
            }

            foreach (var (time, pawn, weapon) in _weapons)
            {
                _facts.PawnWeapons.Add(new PawnWeaponChange(time, pawn, weapon));
            }

            if (_facts.Diagnostics is { } d)
            {
                foreach (var (_, path) in _netGuidCache.NetGuidToPathName)
                {
                    d.AllPaths.Add(path);
                    foreach (var k in DiagnosticKeywords)
                    {
                        if (path.Contains(k, StringComparison.OrdinalIgnoreCase))
                        {
                            d.KeywordPaths.Add(path);
                            break;
                        }
                    }
                }

                foreach (var (path, group) in _netGuidCache.NetFieldExportGroupMap)
                {
                    var names = group.NetFieldExports.Where(e => e is not null).Select(e => e!.Name).ToList();
                    var interesting = DiagnosticKeywords.Any(k => path.Contains(k, StringComparison.OrdinalIgnoreCase))
                        || path.Contains("GameState", StringComparison.Ordinal) || path.Contains("PlayerState", StringComparison.Ordinal)
                        || names.Contains("SearchingPawn") || names.Contains("bAlreadySearched");
                    if (interesting)
                    {
                        d.Groups[path] = names;
                    }
                }
            }
        }
        finally
        {
            base.Cleanup();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------------------------------------

    private string ContainerKey(uint actor) =>
        _netGuidCache.TryGetPathName(actor, out var name) && (actor & 1) == 1
            ? name                                   // static (map placed): stable actor name
            : $"dyn#{actor}";                        // dynamic: id only valid while relevant

    private string? ClassNameOfChannel(uint channelIndex)
    {
        var ch = Channels[channelIndex];
        var id = ch?.ArchetypeId ?? ch?.ActorId;
        return id is uint g && _netGuidCache.TryGetPathName(g, out var p) ? ClassName(p) : null;
    }

    private string ClassName(string path)
    {
        if (_classNameCache.TryGetValue(path, out var name))
        {
            return name;
        }

        var dot = path.LastIndexOf('.');
        name = dot >= 0 ? path[(dot + 1)..] : path;
        var slash = name.LastIndexOf('/');
        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }
        _classNameCache[path] = name;
        return name;
    }

    private static bool LooksLikeContainer(string path, string className) =>
        className.Contains("Chest", StringComparison.OrdinalIgnoreCase)
        || path.Contains("/Containers/", StringComparison.OrdinalIgnoreCase)
        || className.Contains("Container", StringComparison.OrdinalIgnoreCase)
        || className.Contains("LootCannon", StringComparison.OrdinalIgnoreCase);

    protected override void OnExportRead(uint channelIndex, INetFieldExportGroup? exportGroup)
    {
    }

    protected override void OnExternalDataRead(uint channelIndex, IExternalData? exportGroup)
    {
    }

    protected override void OnNetDeltaRead(uint channelIndex, NetDeltaUpdate update)
    {
    }

    protected override FArchive DecryptBuffer(FArchive archive, int size)
    {
        if (!Replay.Info.IsEncrypted)
        {
            return new Unreal.Core.BinaryReader(archive.ReadMemory(size))
            {
                EngineNetworkVersion = Replay.Header.EngineNetworkVersion,
                NetworkVersion = Replay.Header.NetworkVersion,
                ReplayHeaderFlags = Replay.Header.Flags,
                ReplayVersion = Replay.Info.FileVersion,
            };
        }

        using var aes = Aes.Create();
        aes.Key = Replay.Info.EncryptionKey.ToArray();
        var decrypted = aes.DecryptEcb(archive.ReadMemory(size).Span, PaddingMode.PKCS7);

        return new Unreal.Core.BinaryReader(decrypted.AsMemory())
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion,
        };
    }

    protected override FArchive Decompress(FArchive archive)
    {
        if (!Replay.Info.IsCompressed)
        {
            return archive;
        }

        var decompressedSize = archive.ReadInt32();
        var compressedSize = archive.ReadInt32();
        var compressed = archive.ReadBytes(compressedSize);
        var output = Unreal.Encryption.Oodle.DecompressReplayData(compressed, decompressedSize);

        return new Unreal.Core.BinaryReader(output)
        {
            EngineNetworkVersion = archive.EngineNetworkVersion,
            NetworkVersion = archive.NetworkVersion,
            ReplayHeaderFlags = archive.ReplayHeaderFlags,
            ReplayVersion = archive.ReplayVersion,
        };
    }
}
