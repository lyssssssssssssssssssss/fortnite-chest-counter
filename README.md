# Chest Counter

A Windows 11 desktop widget and local web dashboard that count the Fortnite chests **you** open and destroy, including chests destroyed with the **Gum Gum Devil Fruit**. It works by reading the replay files Fortnite saves on your PC.

* **Widget**: a small, always-available card (total, last match, session, start/stop session).
* **Dashboard**: `http://localhost:48231`, with full history, charts, per-match breakdowns and sessions.
* **One local database**: both views read the same data from the same process, so they always agree.

> **Download:** get **ChestCounter-Standalone-win-x64.zip** from the [Releases page](https://github.com/lyssssssssssssssssssss/fortnite-chest-counter/releases/latest), extract it and run `Install Chest Counter.cmd`. On first run the app asks for permission before it reads your replay folder.
>
> **Licence:** All rights reserved. This is **not** open-source software: the code is published for viewing only. You may run the official releases, but you may not copy, modify, redistribute or claim the code. See [LICENSE](LICENSE).

## Account safety: read-only and external

Chest Counter **only opens `.replay` files for reading**, from `%LOCALAPPDATA%\FortniteGame\Saved\Demos`. It never:

* injects code, loads DLLs into, or reads or writes the memory of Fortnite;
* touches Fortnite's install folder, game files, or replay files (it opens them read-only and shared);
* intercepts network traffic or sends any input to the game (no hooks, no macros, no hotkeys);
* goes near Easy Anti-Cheat or BattlEye.

It decodes the replays itself, with a bundled open-source parser and a managed Oodle decompressor. No Fortnite DLL is used. The dashboard server listens on `localhost` only, so other devices can't reach it. The app makes no internet connections at all.

> **Turn replays on in Fortnite:** *Settings → Game → Replays → Record Replays: **On***. Without replays there is nothing to read.

## Install

Requirements: Windows 10/11 x64 and the **.NET 10 Desktop Runtime**, which is already present on the PC this was built on. If it's missing, download it from <https://dotnet.microsoft.com/download/dotnet/10.0>.

1. Open the `ChestCounter` folder (or extract `ChestCounter-win-x64.zip`).
2. Double-click **`Install Chest Counter.cmd`**. No admin rights are needed. It:
   * copies the app to `%LOCALAPPDATA%\Programs\ChestCounter`;
   * adds **Chest Counter** to the Start Menu, plus a desktop shortcut;
   * starts the app.

On first launch the app scans your replay folder and imports your **most recent 100 eligible matches played since the season start** (1 November 2026 by default). This takes a minute or two at low priority. Progress shows in the widget and on the dashboard.

## Using it

**Games played.** Both the widget (*Games* row) and the dashboard (under *matches analysed*) show how many games you've played since the season start, in any mode, plus today's count. It updates after every game. Only eligible matches count towards your chest stats.

**Widget.** Drag it anywhere. Drag the bottom-right corner to resize, and it scales proportionally. The padlock locks its position. Right-click for more options: keep on top, opacity, reset size, settings, minimise to tray and exit. Double-click it to open the dashboard. Closing it only minimises it to the tray icon; use **Exit** to quit.

* It never takes keyboard focus away from the game and never captures your input.
* To see it over Fortnite, use **Windowed Fullscreen**. Exclusive fullscreen hides every other window.

**Dashboard.** Open it from the widget (**Dashboard ↗**), from the tray menu, or at <http://localhost:48231>. It updates automatically a few seconds after each match. Click a match for its full breakdown.

**Sessions.** Press **Start session** before you play and **Stop session** when you're done. A session counts the eligible matches you *finish* while it is running. An active session survives closing and reopening the app. Past sessions are listed on the dashboard.

**Never missing a match.** Chest Counter starts quietly in the tray when you sign in to Windows; you can turn this off in Settings. Fortnite keeps only about 100 replays, so the app has to read them before Fortnite deletes them:

* If the app was closed while you played, it counts those matches the next time it starts and shows *Caught up on N matches* on the widget and the dashboard.
* If Fortnite already deleted some replays before they could be read, a warning says which period may be missing.
* The app records how it last closed, in `chestcounter.log`: from its menu, by the installer, by a Windows shutdown, or not normally (for example ended from Task Manager).

**When are chests counted?** Fortnite only writes a replay's decryption key when the match ends. Chests are therefore added a few seconds after each match, not during it; the widget shows *In a match…* in the meantime.

## What is counted, and how

Every rule below was verified against real Fortnite 43.00 replays.

| Statistic | Source in the replay |
|---|---|
| **Opened chest** | The chest's `bAlreadySearched` turns true and its `SearchingPawn` is your character (the recorder is identified by `GameState.RecorderPlayerState` → `Pawn.PlayerState`). |
| **Destroyed chest** | The chest's `bDestroyed` turns true with `SearchingPawn` = you and the loot spill recorded (`SearchAnimationCount`). The game credits whoever broke it, including chests inside a building you smash. **Each chest counts individually, never per building.** |
| **Gum Gum Devil Fruit** | Fortnite's internal name is `WID_TackyGoofiness`, part of this season's fruit system (`DelilahPaaB`), with abilities such as `TackyGoofiness_Rocket`, `StretchLaunch` and `Grab`. A destroyed chest credited to you while this was your equipped item (within 3 s) is labelled *with the Gum Gum fruit*. |
| **Total chests** | Opened + destroyed. A chest that was opened is never counted again as destroyed. A chest refilled mid-match and opened again counts again. |
| **CPG** | Total chests ÷ eligible matches. |
| **Chest types** | Chest (`Tiered_Chest_Athena`, `AlwaysSpawn_NormalChest`), rare (`AlwaysSpawn_RareChest`), buried (`B_Athena_BuriedChest_*`), extraction / relic (`B_TieredChest_Relics`, `BGA_ExtractionPointGameplay_Chest`), sprite poster (`B_SpritePosters_Booty_Chest`). Ammo boxes, food boxes and loot cannons are shown separately and are **not** in the chest total. |

**Eligible matches.** Only public **Battle Royale** and **Zero Build** playlists count (`Playlist_Default*`, `Playlist_NoBuildBR_*`), taken from the playlist recorded in the replay. Ranked (`Habanero`), tournaments (`Showdown…`), Reload, Creative/UEFN and any unknown playlist are excluded everywhere. Unknown playlists are listed as *unclassified*. Every excluded replay is listed on the dashboard with the reason.

**Pickaxe-only matches.** A match is not counted when its replay shows you only ever held a pickaxe **and** got no chests: you were AFK, spectating your squad, or eliminated before picking anything up. This keeps your chests-per-match average honest. A match where you held anything else, or got even one chest, always counts. You can turn this off in **Settings**; skipped matches are listed on the dashboard.

**Season start.** Matches are dated by the timestamp **inside** the replay, in your local time, never by the file date. Matches before the season start (default **1 November 2026**) are ignored everywhere. Change it in **Settings → Season start date** when a new season begins; every statistic updates instantly.

### Honest limitations

* **Treasures** are shown as "–". The replay references the treasure-hunt abilities, but does not reliably say which treasures were yours, so nothing is counted rather than guessing.
* **Chests destroyed with no culprit on record** are never credited. The dashboard shows how many happened near you, and how many while you held the Gum Gum fruit, for transparency.
* **Relic-finder buried chests** (`B_Athena_BuriedChest_RelicFinder`) don't record who dug them, so they are not credited.
* **Teammates' chests** are shown for reference only.
* A replay only records what happened near your character. If you are eliminated early and spectate your squad, those later chests are theirs, not yours.
* A Fortnite update can change the replay format. If replays become unreadable, they're listed as *Unreadable* on the dashboard and stored results are kept.

## Settings

Open them from the widget (right-click → **Settings…**) or the tray icon:

* season start date;
* replay folder: auto-detected, or choose it manually;
* display name for the dashboard header (Fortnite scrambles player names inside replays);
* Epic account filter: `auto` counts the account that recorded most replays;
* Gum Gum item id;
* extra eligible or excluded playlists;
* dashboard port;
* keep on top, start with Windows, open dashboard on start;
* **Rescan now** and **Re-analyse all**.

## Data and files

| What | Where |
|---|---|
| App | `%LOCALAPPDATA%\Programs\ChestCounter` |
| Statistics database (SQLite) | `%LOCALAPPDATA%\ChestCounter\chestcounter.db` |
| Settings | `%LOCALAPPDATA%\ChestCounter\settings.json` |
| Log | `%LOCALAPPDATA%\ChestCounter\chestcounter.log` |

Fortnite keeps only about 100 replays and deletes old ones. Chest Counter stores each match's results permanently, so your history outlives the replay files.

## Updating

Build or download the new version, then run **`Install Chest Counter.cmd`** again. It closes the running app, replaces the program files and restarts it. Your statistics and settings are kept. When the replay analyser itself improves, replays still on disk are re-analysed automatically; matches whose replays are already gone keep their stored results.

## Uninstalling

Run **`Uninstall Chest Counter.cmd`** from the install folder, or `uninstall.ps1 -RemoveData` to also delete your statistics.

## Troubleshooting

* **"No replay folder"**: turn on *Record Replays* in Fortnite, or choose the folder in Settings.
* **Nothing new after a match**: wait about 10 seconds after the match ends, or use *Rescan now*. Check the *Excluded & unclassified* section of the dashboard.
* **Dashboard doesn't open**: the app must be running (tray icon). If port 48231 is busy, the app uses the next free port. The widget's tooltip shows the address.
* **Problems**: see `%LOCALAPPDATA%\ChestCounter\chestcounter.log`.

## Building from source (developers)

```
build.ps1            # tests + publish to dist\ChestCounter (+ zip)
dotnet test tests\ChestCounter.Tests
dotnet run --project tools\ChestCounter.Probe -- --analyze --count 20   # per-match analysis of real replays
dotnet run --project tools\ChestCounter.Probe -- --count 5 --out probe-out   # raw replay facts as JSON
```

| Path | Contents |
|---|---|
| `src/ChestCounter.ReplayReader` | Replay → raw facts. Container updates, closes, recorder, pawns, playlist, equipped items. |
| `src/ChestCounter.Core` | Counting rules (`Analysis/MatchAnalyzer.cs`), eligibility, SQLite database, replay watcher, statistics, local web server and the embedded dashboard (`Web/wwwroot`). |
| `src/ChestCounter.App` | WPF widget, tray icon and settings window (one process hosts everything). |
| `lib/` | Vendored MIT replay parser (FortniteReplayDecompressor) with the patches documented in `lib/PATCHES.md`. |
| `tests/` | 103 automated tests: synthetic fixtures for every rule, plus integration tests on real replays when present. |

## Licence

Copyright © 2026 [lyssssssssssssssssssss](https://github.com/lyssssssssssssssssssss). **All rights reserved.** See [LICENSE](LICENSE). The code is viewable on GitHub, but you may not copy, modify, redistribute, use commercially or present it as your own. That includes feeding it to an AI tool to recreate it.

The only exception is the replay-decoding library in `lib/`, which is based on [FortniteReplayDecompressor](https://github.com/Shiqan/FortniteReplayDecompressor) by Shiqan and stays under its own MIT licence (`lib/LICENSE-FortniteReplayDecompressor.txt`).

This project is not affiliated with Epic Games. Fortnite is a trademark of Epic Games, Inc.
