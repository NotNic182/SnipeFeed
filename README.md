# SnipeFeed

Your BeatLeader following feed inside Beat Saber on Quest. Adds a **Snipe Feed** tab to the gameplay setup panel's **Mods** section — the left screen of Solo song selection, next to tabs like ReeSabers and Qounters++ — that lists the most recent scores of every player you follow on BeatLeader — newest first — so you know exactly which maps to snipe without taking the headset off.

- Target: **Beat Saber 1.40.8 (build 7379), Quest standalone (aarch64), Scotland2**
- Dependencies (auto-installed from `mod.json`): beatsaber-hook, custom-types, paper2, BSML
- Uses only public BeatLeader API endpoints — no login, no credentials stored

## Setup (one time)

1. Install `SnipeFeed.qmod` (see Install below).
2. Enter **Solo**, then on the left panel open the **Mods** tab and select **Snipe Feed**.
3. If you are logged in inside the official BeatLeader mod, the feed loads automatically — nothing to enter. Then press **Refresh**.

The value is saved to the mod config; after that the feed loads whenever you open the view.

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

## v1.1.0 features

- **Moved into the gameplay setup panel**: the feed no longer lives behind a main-menu Mods button. It is now a **Snipe Feed** tab in the left panel's **Mods** section during Solo song selection, alongside tabs like ReeSabers and Qounters++ — check the feed right where you pick your next song. The layout was compacted to fit the panel (same control row, status line, score rows, and detail modal). **Play / Download & Play** still works from there: the game hops back to the main menu for a moment and re-enters Solo with the chosen song selected.

## v1.0.0 features

- **Song-first rows**: the title line moved to the top of each row and grew — "Song Name - Artist [mapper]" with difficulty and stars — while the player identity (avatar + name) moved underneath at a smaller size, next to the score stats and time-ago. The mapper name now comes straight from BeatLeader's song metadata and also appears in the detail modal's byline.

## v0.5.0 features

- **Row-based feed layout**: every score is one clean horizontal row — rank, uniform cover art, avatar + prominent player name with the time-ago right beside it, and a single info line (song name · difficulty · stars · accuracy · FC) with clear spacing between stats. A right-edge chevron marks each row as selectable. Fixes the v0.4.0 bug where the player name never rendered (the name line was vertically ellipsized away by its own row height).
- **Consistent stat colors**: difficulty keeps BeatLeader's per-difficulty colors (spelled out as "Expert+"), stars yellow, accuracy orange, FC green, secondary text muted gray.
- **Adjustable feed size**: a "Scores" stepper (10–100, default 50) controls how many scores a refresh pulls on both the friends-feed and public-API paths.
- **Simplified header**: the ID search bar is gone — the feed uses the BeatLeader mod login on the headset (`PlayerId` in the config file remains as a fallback for the public API). One control row holds the player filter dropdown, the Scores stepper, and Refresh; the redundant heading label is gone too.
- **Wider, denser list**: the list and header rows share one 105-unit content width, filling the previously empty horizontal space; row backgrounds are darker so entries separate visually; status line is small muted secondary text; scroll arrows centered over the rows.

## v0.4.0 features

- **BeatLeader-style score cards**: each row displays song cover art (left), player avatar with name and time-ago (center), song title with colored difficulty and star rating, and color-graded accuracy / purple PP / FC badge / modifiers.
- **Detail modal redesigned**: large cover art at top, avatar + player row, colored stats display. **Play / Download & Play** button: checks installed custom levels via SongCore by hash; if missing, downloads the map zip from BeatSaver, installs it into the custom levels folder, refreshes SongCore, then jumps straight to the song.
- **Parallel score loading**: followed players' scores now load in parallel (up to 4 at once) when using the public-API path — much faster refresh on feeds with 20+ followed players.
- **2-minute feed cache**: the feed is kept in memory between menu visits — reopening is instant. Press **Refresh** to force a reload from the API.
- **Image caching**: song covers and player avatars are cached per session and downloaded once per URL.

## Known limitations (v0.4.0)

- Official OST/DLC map scores have no custom-song hash — their Play button is disabled ("Not a custom song").
- Follows capped (default 20 players × 3 scores) in the public-API fallback path; the friends-feed path (BeatLeader login cookie) gets everything in one request.
