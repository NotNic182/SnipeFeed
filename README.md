# SnipeFeed

Your BeatLeader following feed inside Beat Saber on Quest. Adds a **Snipe Feed** button to the main menu (Mods section) that lists the most recent scores of every player you follow on BeatLeader — newest first — so you know exactly which maps to snipe without taking the headset off.

- Target: **Beat Saber 1.40.8 (build 7379), Quest standalone (aarch64), Scotland2**
- Dependencies (auto-installed from `mod.json`): beatsaber-hook, custom-types, paper2, BSML
- Uses only public BeatLeader API endpoints — no login, no credentials stored

## Setup (one time)

1. Install `SnipeFeed.qmod` (see Install below).
2. In the main menu, open **Snipe Feed**.
3. If you are logged in inside the official BeatLeader mod, the feed loads automatically — nothing to enter. Otherwise enter your BeatLeader **ID, alias (e.g. `nic`), or pasted profile URL** and press **Refresh**.

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

1. **Preferred:** if the official BeatLeader mod's login cookie exists on the headset (`ModData/.../Mods/bl/cookies/cookies.txt`), one call to `GET api.beatleader.com/user/friendScores?sortBy=date&order=desc` returns the same feed the BeatLeader website home page shows. The cookie is read-only reused, never modified or logged.
2. **Fallback (public API, no login):**
   - non-numeric input is resolved via `GET /player/{aliasOrId}` (the server resolves aliases itself);
   - `GET /player/{id}/followers?type=following` — note BeatLeader hides this list when the profile has "hide my friends" enabled, in which case the mod explains what to do;
   - `GET /player/{id}/scores?sortBy=date&order=desc` per followed player.
3. Entries merged and sorted newest-first: player, accuracy, PP, FC flag, song, difficulty, stars, modifiers, time ago.

All requests run on a background thread with 15s timeouts; UI updates go through BSML's main-thread scheduler. If the network is down or the ID is wrong, the view shows an error message and the game is unaffected.

## Test plan

1. Boot game → no crash, `SnipeFeed` listed in mod logs (`qpm s log`).
2. Main menu → Mods section shows **Snipe Feed** button.
3. Open while logged into the BeatLeader mod → friends feed loads, no crash.
4. **[changed in v0.5.0]** No login + bogus `PlayerId` in the config file (e.g. `1`) → clean error message.
5. **[changed in v0.5.0]** No login + your real `PlayerId` in the config file → progress text, then feed sorted newest-first.
6. Airplane-mode the headset → Refresh shows network error, gameplay unaffected.
7. Enter/exit a song, reopen feed → still works (no duplicate UI).
8. **[v0.4.0]** Open Snipe Feed → cards render with cover art (left), avatar + player name + time (center), song title + colored difficulty + stars, colored stats.
9. **[v0.4.0]** Covers and avatars appear as rows scroll into view; placeholder tiles shown while images load.
10. **[v0.4.0]** Fast scroll up/down → no cell ever shows another row's image.
11. **[v0.4.0]** Close and reopen the view within 2 minutes → instant load, no "Loading..." message. Press Refresh to force reload.
12. **[v0.4.0]** Refresh with ~20 followed players completes noticeably faster than v0.3.0 (parallel fetch).
13. **[v0.5.0]** Rows read left → right: rank number, cover art, avatar + bold player name (visible!) with time-ago beside it, then one song/stats line (song · difficulty · stars · accuracy · FC), chevron at the right edge.
14. **[v0.5.0]** All cover tiles identical size and vertically aligned; row backgrounds clearly separate entries; ranks 1–3 tinted gold/silver/bronze.
15. **[v0.5.0]** Header is a single row: player filter dropdown left, "Scores" 10–100 stepper and Refresh right; no search bar. Status line renders small and muted; scroll arrows centered over the rows.
16. **[v0.5.0]** Set Scores to 100 → refresh pulls up to 100 scores (status line count matches); set to 10 → 10 scores.
17. **[v0.5.0]** With no BeatLeader mod login and no PlayerId in the config file → clear instruction message, no crash.
18. **[v1.0.0]** Each row leads with a large "Song Name - Artist [mapper]" title (plus difficulty and stars); below it sit the avatar, a smaller player name, the score stats, and the time-ago.

## Rollback

Remove the mod in MBF/QuestPatcher, or delete `SnipeFeed.qmod`'s installed files via the mod manager. The mod writes only its own config file (`.../ModData/.../Configs/snipefeed.json` per config-utils) and never touches PlayerData.dat, AvatarData.dat, or settings.cfg. Previous game state is untouched.

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
