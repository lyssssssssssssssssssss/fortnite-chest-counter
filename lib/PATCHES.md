# Vendored replay parser – changes

`lib/Unreal.Core`, `lib/Unreal.Encryption` and `lib/OozSharp` are copied from
[FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) by Shiqan,
**MIT licence** (see `LICENSE-FortniteReplayDecompressor.txt`), commit `76f74c1f640069afdba71b6468e2f819327893c2`
(the same commit published as the `Unreal.Core` / `Unreal.Encryption` 3.1.0 NuGet packages).

`OozSharp` is a managed Oodle (Kraken) decoder, so no Fortnite DLL is ever loaded or read.

Every change is marked `PATCH (ChestCounter #n)` in the source:

| # | File | Change | Why |
|---|------|--------|-----|
| 1 | `Unreal.Core/BitReader.cs` | `SetTempEnd` saves the current end *before* validating. | Upstream returned early on overflow without saving, so the matching `RestoreTempEnd` restored a stale end and could loop forever on a corrupt block. |
| 2 | `Unreal.Core/ReplayReader.cs` | `CurrentFrameTime` property (demo time of the frame being read). | Timestamp each chest event. |
| 3 | `Unreal.Core/ReplayReader.cs` | Channel close passes the closed actor and the close reason (`OnChannelClosed(ch, actor, reason)`). | Upstream nulled the channel before reading its actor, so the callback always got `null`. |
| 4 | `Unreal.Core/ReplayReader.cs` | `TryReceiveClassNetCacheField` hook. | Read `GameState.CurrentPlaylistInfo` (playlist) raw, skip everything else cheaply. |
| 5 | `Unreal.Core/ReplayReader.cs` | `OnUnresolvedReplicator` hook. | Diagnostics: count objects whose class could not be resolved. |
| 6 | `Unreal.Core/Models/NetGuidCache.cs` | Adding a new export group clears the "failed paths" cache. | An actor sent a moment before its class's export group (typical for the first chest of a type) was never decoded for the rest of the replay. |
| 7 | `Unreal.Core/Models/NetGuidCache.cs` | Exact class lookup for decorated level-actor names (`Tiered_Chest_Athena47_UAID_…`, `…_<16 hex>_<n>`). | World-Partition chests were never mapped to the `Tiered_Chest_Athena_C` export group, so their updates were lost. |

To update the parser after a Fortnite change: compare upstream with this folder, re-apply the patches above, run
`dotnet test` and `tools\ChestCounter.Probe --analyze --count 20`.
