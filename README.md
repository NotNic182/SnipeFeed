# SnipeFeed

Your BeatLeader following feed inside Beat Saber — on **Quest standalone and PCVR**. Adds a **Snipe Feed** tab to the gameplay setup panel's **Mods** section — the left screen of song selection, next to tabs like ReeSabers and Qounters++ — that lists the most recent scores of every player you follow on BeatLeader — newest first — so you know exactly which maps to snipe without taking the headset off.

<img width="3840" height="2160" alt="13da867f7f904380a0319fcbcde4550e" src="https://github.com/user-attachments/assets/5086db22-b704-4fec-b021-d133d8abe3ee" />

<img width="3840" height="2160" alt="35a20023bfd743aebc921cbb030e8ce9" src="https://github.com/user-attachments/assets/a93091b1-e995-4f55-abf9-2fc43ccd1181" />

## Two versions, one repo
| Platform | Source | Download |
|---|---|---|
| **Quest standalone** | this directory (C++ / Scotland2 qmod) | [Releases](https://github.com/NotNic182/SnipeFeed/releases) — `SnipeFeed.qmod` |
| **PCVR** | [`pcvr/`](pcvr/) (C# / BSIPA plugin) | [Releases](https://github.com/NotNic182/SnipeFeed/releases) — `SnipeFeed.PC-*.zip` |

Both versions share the same v2.0.0 feature set and UI: the Snipe Feed tab in every gameplay setup panel, BeatLeader-style score rows, the detail modal with **Play / Download & Play**, and automatic reuse of your BeatLeader mod login. PC requirements, build and install instructions live in [pcvr/README.md](pcvr/README.md).

## v2.0.1 fixes

- **Quest TLS verification**: peer verification now enabled. The qmod ships Mozilla's CA bundle (`cacert.pem`, copied into the mod's ModData folder and used via libcurl `CURLOPT_CAINFO`) — newer Horizon OS builds keep system CAs in the Conscrypt APEX where native code can't read them. Android's system CA path remains as a fallback if the copy is missing.
- **Async lifetime safety**: callbacks that cross from a worker thread capture no il2cpp/Unity pointers at all, only plain data — only callbacks that are guaranteed to run and die on the main thread capture Unity objects, via `SafePtrUnity`, to prevent use-after-free.
- **Atomic map installs**: extraction now happens to a temp directory and renames into place, ensuring failed installs leave no corrupt level folders.
- **Truthful error messages**: feed empty vs. network error now clearly distinguished; cookie reuse and fallback paths clearly documented in code.
- **Bounded image caches**: both Quest and PC limit cached sprites/textures to prevent session-long memory growth.
- **No PC freeze on install**: all file I/O now runs off the main thread; map installs no longer cause VR frame drops.

**The rest of this README covers the Quest version.**

- Target: **Beat Saber 1.40.8 (build 7379), Quest standalone (aarch64), Scotland2**
- Dependencies (auto-installed from `mod.json`): beatsaber-hook, custom-types, paper2, BSML, SongCore
- Reuses the BeatLeader mod's login read-only when present (never modified or stored by SnipeFeed); otherwise only public API endpoints

## Where to find it

The tab appears in the gameplay setup panel (left screen) in **every** mode — Solo/Party song selection, online multiplayer, campaign, and modded flows like **Multiplayer+** (QBeatSaberPlus) lobbies. Open the **Mods** tab on that panel and pick **Snipe Feed**.

## What the Play button does (per mode)

Tap any score row to open its details. The button at the bottom adapts to where you are:

| Where you are | Button | What happens |
|---|---|---|
| Solo song selection | **Play** / **Download & Play** | Downloads the map if needed, then the menu briefly hops out and back in with the sniped song selected, ready to play. |
| Party song selection, Multiplayer song-select screen | **Play** / **Download & Play** | Downloads the map if needed, then selects the song directly in the open picker — no hop (the re-enter hop is Solo-only). |
| A lobby (vanilla multiplayer or Multiplayer+) | **Download** / **In Custom Levels** | Downloads and installs the map only — the mod never yanks you out of a lobby. Then pick the song through the lobby's own song picker so the whole room gets it. |
| Score on an official OST/DLC song | *(disabled)* | Official songs have no downloadable map. |

In Party mode the song is selected in place in the open picker on both platforms (the re-enter hop is Solo-only).

## Setup (one time)

1. Install `SnipeFeed.qmod` (see Install below).
2. Enter **Solo**, then on the left panel open the **Mods** tab and select **Snipe Feed**.
3. If you are logged in inside the official BeatLeader mod, the feed loads automatically — nothing to enter. Then press **Refresh**.

No BeatLeader mod login? Put your BeatLeader player ID into the mod's config file (`ModData/.../Configs/snipefeed.json`, `PlayerId`) and the feed uses the public API instead.

## Install

**ModsBeforeFriday (recommended):** open [mbf.bsquest.xyz](https://mbf.bsquest.xyz) in Chrome/Edge with the headset connected, and drag `SnipeFeed.qmod` into the "upload mods" area.

**QuestPatcher:** drag `SnipeFeed.qmod` into the mods list.

**ADB (dev loop):** with the game closed, push the qmod via MBF, or copy just the rebuilt `build/libsnipefeed.so` to the Scotland2 late mods folder and restart the game.

## Build from source

Requires: qpm CLI, CMake, Ninja, PowerShell 7. Android NDK is managed by qpm.

```
qpm restore
qpm ndk resolve -d
qpm s build      # compiles build/libsnipefeed.so
qpm qmod manifest # regenerates mod.json
qpm s qmod       # packages SnipeFeed.qmod
```

## How it works

1. If the official BeatLeader mod's login cookie exists on the headset (`ModData/.../Mods/bl/cookies/cookies.txt`), one call to `GET api.beatleader.com/user/friendScores?sortBy=date&order=desc` returns the same feed the BeatLeader website home page shows. The cookie is read-only reused, never modified or logged.
2.  Entries merged and sorted newest-first: player, accuracy, PP, FC flag, song, difficulty, stars, modifiers, time ago.

All requests run on a background thread with 15s timeouts; UI updates go through BSML's main-thread scheduler. If the network is down or the ID is wrong, the view shows an error message and the game is unaffected.

## v2.0.0 features

- **Lives in the gameplay setup panel, everywhere**: the feed is a **Snipe Feed** tab in the left panel's **Mods** section — in Solo/Party, online multiplayer, campaign, and Multiplayer+ lobbies (`MenuType::All`). No more main-menu button.
- **Full-height list**: the list is sized from the tab's actual measured height (60 units on 1.40.8) instead of a hardcoded guess — roughly 4½ compact rows, with working page arrows (they were being swallowed by the rows' touch surface).
- **Mode-aware Play button**: in-place selection where a song picker is open, the quick re-enter hop in Solo, and download-only in lobbies — the mod never dismisses an active lobby flow (doing so corrupts the menu state; learned the hard way).
- **Modal fixes**: the detail popup closes instantly when launching and can never linger across menu transitions.

## v1.0.0 features

- **Song-first rows**: the title line moved to the top of each row and grew — "Song Name - Artist [mapper]" with difficulty and stars — while the player identity (avatar + name) moved underneath at a smaller size, next to the score stats and time-ago. The mapper name now comes straight from BeatLeader's song metadata and also appears in the detail modal's byline.

## v0.5.0 features

- **Row-based feed layout**: every score is one clean horizontal row — rank, uniform cover art, avatar + prominent player name with the time-ago right beside it, and a single info line (song name · difficulty · stars · accuracy · FC) with clear spacing between stats. A right-edge chevron marks each row as selectable. Fixes the v0.4.0 bug where the player name never rendered (the name line was vertically ellipsized away by its own row height).
- **Consistent stat colors**: difficulty keeps BeatLeader's per-difficulty colors (spelled out as "Expert+"), stars yellow, accuracy orange, FC green, secondary text muted gray.
- **Adjustable feed size**: a "Scores" stepper (10–100, default 50) controls how many scores a refresh pulls on both the friends-feed and public-API paths (on the public-API path the per-player pull scales up to meet the stepper, capped at 20 per player).
- **Simplified header**: the ID search bar is gone — the feed uses the BeatLeader mod login on the headset (`PlayerId` in the config file remains as a fallback for the public API). One control row holds the player filter dropdown, the Scores stepper, and Refresh; the redundant heading label is gone too.
- **Wider, denser list**: the list and header rows share one 105-unit content width, filling the previously empty horizontal space; row backgrounds are darker so entries separate visually; status line is small muted secondary text; scroll arrows centered over the rows.

## v0.4.0 features

- **BeatLeader-style score cards**: each row displays song cover art (left), player avatar with name and time-ago (center), song title with colored difficulty and star rating, and color-graded accuracy / purple PP / FC badge / modifiers.
- **Detail modal redesigned**: large cover art at top, avatar + player row, colored stats display. **Play / Download & Play** button: checks installed custom levels via SongCore by hash; if missing, downloads the map zip from BeatSaver, installs it into the custom levels folder, refreshes SongCore, then jumps straight to the song.
- **Parallel score loading**: followed players' scores now load in parallel (up to 4 at once) when using the public-API path — much faster refresh on feeds with 20+ followed players.
- **2-minute feed cache**: the feed is kept in memory between menu visits — reopening is instant. Press **Refresh** to force a reload from the API.
- **Image caching**: song covers and player avatars are cached per session and downloaded once per URL.

## Known limitations

- Official OST/DLC map scores have no custom-song hash — their Play button is disabled ("Not a custom song").
- Follows capped (default 20 players × 3 scores) in the public-API fallback path; the friends-feed path (BeatLeader login cookie) gets everything in one request.
