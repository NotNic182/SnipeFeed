# SnipeFeed PCVR

Managed C# / BSIPA port of the Quest SnipeFeed mod for **Beat Saber 1.40.8 PCVR**.

The Quest project remains untouched in the repository root. This folder is a separate PC build target that mirrors the Quest mod's v2.0.0 feature set and UI.

## Features

- Adds **Snipe Feed** to the gameplay setup panel's **Mods** tabs (`MenuType.All`), sized to fit the tab area.
- Reads the same BeatLeader friends feed used by the Quest version.
- If the official **BeatLeader PC mod** is installed, SnipeFeed reuses its automatic sign-in: it waits briefly for the mod's login to finish, copies the session cookies read-only, and calls `/user/friendScores` on whichever BeatLeader server (`.com` or `.net`) the mod is configured to use.
- Falls back to the public BeatLeader API using the `PlayerId` (ID or alias) configured in `UserData/SnipeFeedPC.json`.
- Loads followed players' recent scores in parallel (maximum 4 requests at once), sorts newest first, and keeps a 2-minute in-memory cache.
- BeatLeader-style score rows: rank, cover art, "Song - Artist [mapper]" with colored difficulty and stars, avatar, player name, accuracy/PP/FC/modifiers, and time-ago. Cover art and avatars are cached, coalesced, and retried after the next successful refresh.
- One control row above the list: player filter dropdown, adjustable score count (10-100), and Refresh.
- Selecting a row opens a **detail modal** (cover art, song info, stats, avatar + player, and the Play button), matching the Quest mod's flow.
- Detects installed maps through SongCore; downloads missing custom maps from BeatSaver, safely extracts them to `Beat Saber_Data/CustomLevels`, then asks SongCore to refresh.
- **Play / Download & Play** in solo re-enters the game's own solo flow with the map pre-selected (the same mechanism the BeatLeader mod uses). In multiplayer's song picker the map is selected in place. In a lobby without a picker it intentionally stays **download-only** instead of dismissing or corrupting the multiplayer flow.
- Official OST/DLC scores without a custom-song hash are shown but cannot be downloaded.

## Dependencies

- BSIPA
- BeatSaberMarkupLanguage (BSML) 1.12.3 or newer
- SongCore
- BeatLeader is optional; when present and logged in it enables the authenticated one-request friends-feed path.

## Build

The project uses the same PC Beat Saber mod structure as other BSIPA/BSML mods.

1. Install/mod **Beat Saber 1.40.8** on PC and make sure BSIPA, BSML and SongCore are installed.
2. Either set the `BeatSaberDir` MSBuild property to the Beat Saber install directory or create a repository-level `Refs` directory containing the normal Beat Saber reference layout.
3. From this folder run:

```powershell
dotnet restore .\SnipeFeed.PC.csproj
dotnet build .\SnipeFeed.PC.csproj -c Release -p:BeatSaberDir="C:\Program Files (x86)\Steam\steamapps\common\Beat Saber"
```

With `BeatSaberModdingTools.Tasks`, a normal local Beat Saber development setup can copy/package the plugin in the usual way. The resulting plugin assembly is `SnipeFeed.PC.dll`.

## Install

Put the built `SnipeFeed.PC.dll` in Beat Saber's `Plugins` folder alongside BSML and SongCore, then start the game.

Open Solo/Party/Multiplayer song selection, open the left gameplay setup panel's **Mods** section, then choose **Snipe Feed**.

If SnipeFeed cannot reuse an authenticated BeatLeader PC session, set `PlayerId` (your BeatLeader ID, alias, or pasted profile URL) in `UserData/SnipeFeedPC.json`, then press **Refresh**. There is deliberately no ID input inside the tab — the Quest mod removed its on-panel input, and the PC port matches.

## Port notes

This is a real PC port rather than a C++ cross-compile. Quest-specific QPM, `custom-types`, `beatsaber-hook`, Android/ARM64 codegen, native libcurl, `.so` packaging, and APK assumptions are not used here. PCVR loads this as a managed C# BSIPA assembly and uses the PC versions of BSML and SongCore.

The UI mirrors the Quest mod's v2.0.0 tab: BSML `custom-list` cells recreate the Quest `FeedCell` row layout (rank, cover, avatar, colored stats, chevron), and score details live in a modal instead of inline text. The feed, filter and cache state survive menu rebuilds because BSML re-parses the same host object, matching the Quest mod's persistent state.
