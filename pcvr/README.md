# SnipeFeed PCVR

Managed C# / BSIPA port of the Quest SnipeFeed mod for **Beat Saber 1.40.8 PCVR**.

The Quest project remains untouched in the repository root. This folder is a separate PC build target.

## Features

- Adds **Snipe Feed** to the gameplay setup panel's **Mods** tabs (`MenuType.All`).
- Reads the same BeatLeader friends feed used by the Quest version.
- If the official **BeatLeader PC mod** is logged in, SnipeFeed copies its in-memory BeatLeader cookies read-only and calls `/user/friendScores`.
- Falls back to the public BeatLeader API using the configured player ID/alias.
- Loads followed players' recent scores in parallel (maximum 4 requests at once), sorts newest first, and keeps a 2-minute in-memory cache.
- Player filter, configurable feed size (10-100), score details, PP/accuracy/FC/modifier formatting.
- Detects installed maps through SongCore.
- Downloads missing custom maps from BeatSaver, safely extracts them to `Beat Saber_Data/CustomLevels`, then asks SongCore to refresh.
- If a song picker is currently open, **Select / Download & Select** selects the map there. In a lobby without a picker open it intentionally stays **download-only** instead of dismissing or corrupting the multiplayer flow.
- Official OST/DLC scores without a custom-song hash are shown but cannot be downloaded.

## Dependencies

- BSIPA
- BeatSaberMarkupLanguage (BSML)
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

If SnipeFeed cannot reuse an authenticated BeatLeader PC session, enter your BeatLeader player ID or alias in the **BeatLeader ID fallback** field and press **Refresh**.

## Port notes

This is a real PC port rather than a C++ cross-compile. Quest-specific QPM, `custom-types`, `beatsaber-hook`, Android/ARM64 codegen, native libcurl, `.so` packaging, and APK assumptions are not used here. PCVR loads this as a managed C# BSIPA assembly and uses the PC versions of BSML and SongCore.

The first PC pass deliberately uses BSML's standard list cells instead of recreating the Quest custom C++ `FeedCell`. That keeps the port small and maintainable while preserving the important information and behavior. Cover/avatar thumbnail parity can be added later without changing the feed or installer services.
