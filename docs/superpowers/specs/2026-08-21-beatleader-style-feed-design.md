# SnipeFeed v0.4.0 — BeatLeader-Style Feed Redesign

**Date:** 2026-08-21
**Status:** Approved
**Target:** Beat Saber 1.40.8 (build 7379), Quest standalone, Scotland2, BSML ^0.4.x

## Goal

Make the Snipe Feed view look and feel like BeatLeader's own UI (score cards
with player avatars, song cover art, BeatLeader's colors and typography) while
fitting Beat Saber's native menu style, and apply a small set of performance
and code-quality improvements. No behavior changes to fetching logic,
song install, or launch flow beyond what is listed here.

## Reference material

Patterns are taken from the reference sources on disk (not vendored, not
copied verbatim except where noted):

- `references/beatleader-qmod` — card BSML layout (`FeaturedPreviewPanel`,
  `TextNewsPostHeaderPanel`), cached sprite loader (`Assets/Sprites.cpp`),
  format helpers (`Utils/FormatUtils.hpp`), styling constants.
- `references/BetterSongSearchQuest` — custom `HMUI::TableCell` with
  dequeue/reuse (`SongListCellTableData.hpp`), `IDataSource` wiring,
  selection/highlight states, per-cell cover loading with stale-guard.

## Architecture

Five source files today; two new pairs are added. Responsibilities stay
sharply separated:

| Unit | Role | Depends on |
|---|---|---|
| `Feed.{hpp,cpp}` | Fetch + parse the feed (worker threads) | Web |
| `Web.{hpp,cpp}` | HTTP GET via curl | — |
| `SpriteCache.{hpp,cpp}` **(new)** | URL → `UnityEngine::Sprite*` cache | Web, BSML |
| `FeedCell.{hpp,cpp}` **(new)** | One feed card as a reusable `HMUI::TableCell` | SpriteCache |
| `FeedViewController.{hpp,cpp}` | View: header, table (`IDataSource`), modal | Feed, FeedCell, SongInstaller |
| `SongInstaller.{hpp,cpp}` | Unchanged | Web, SongCore |
| `main.cpp` | Unchanged | — |

## 1. Data layer (`Feed.hpp` / `Feed.cpp`)

- `FeedEntry` gains:
  - `std::string avatarUrl;` — from the score's `player.avatar`, else the
    followed player's `avatar` from the followers list (fallback path).
  - `std::string coverUrl;` — from `leaderboard.song.coverImage`.
- `FollowedPlayer` gains `avatar`, parsed from the followers response.
- **Parallel fallback fetch:** replace the sequential per-player score loop
  with ~4 worker threads pulling indices from a shared `std::atomic<size_t>`.
  Each worker appends into its own local vector; results are merged under a
  mutex (or after `join`), then sorted by `timepost` descending exactly as
  today. Progress callback reports a running count ("Loading scores… 12/20")
  driven by an atomic counter.
- All other logic (cookie path, alias resolution, error messages, clamps)
  unchanged.

## 2. Sprite cache (`SpriteCache.hpp` / `SpriteCache.cpp`, new)

Modeled on beatleader-qmod `Sprites::get_Icon`, with two extra guards:

```cpp
namespace SnipeFeed::SpriteCache {
    // Main-thread only. Callback fires on the main thread, possibly
    // synchronously on a cache hit. Never fires with nullptr; on failure
    // the callback simply never fires (caller keeps its placeholder).
    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite);
    void ClearCache(); // optional hygiene hook
}
```

- Cache: `std::unordered_map<std::string, SafePtrUnity<UnityEngine::Sprite>>`.
- On miss: download bytes with `Web::Get` on a detached thread → marshal via
  `BSML::MainThreadScheduler::Schedule` → `BSML::Lite::ArrayToSprite` →
  store → invoke callbacks.
- **Coalescing:** a pending-request map (`url → vector<callback>`) so N cells
  requesting the same avatar cause one download.
- **Failure memo:** URLs that failed (non-200 / decode failure) are recorded
  for the session and re-requests are ignored — no retry storm.
- Expected working set is small (≤ 60 covers + ≤ 20 avatars per refresh);
  no eviction policy needed beyond `ClearCache()`.

## 3. Feed card cell (`FeedCell.hpp` / `FeedCell.cpp`, new)

Custom-types class `SnipeFeed::FeedCell : HMUI::TableCell`, built with
`BSML::parse_and_construct` from an inline BSML string, using the
BetterSongSearch dequeue pattern:

- `static FeedCell* GetCell(HMUI::TableView*)` — `DequeueReusableCellForIdentifier`,
  else construct: new GameObject + component + parse BSML + add `HMUI::Touchable`.
- `void SetData(FeedEntry const&)` — binds all texts and kicks off both image
  loads through SpriteCache.
- **Stale-guard:** the cell stores the URLs it last requested; the sprite
  callback compares before applying, so a reused cell never shows a wrong
  image after fast scrolling.
- Overrides `SelectionDidChange` / `HighlightDidChange` / `WasPreparedForReuse`
  to drive background state and reset images to the placeholder.

Layout (one card, height ~12, width ~92):

```
[cover ~10x10] | [avatar 4x4] PlayerName            2h ago
               | Song Name  Ex+  7.3★
               | 96.42%  312pp  FC  +SF
```

Styling (BeatLeader values):

- Card background: `bg="round-rect-panel"`, black tint — alpha 0.45 idle,
  0.8 selected/highlighted.
- Player name: white, size 3.5. Time-ago: `#888888`, size 2.5, right side.
- Song line: size 3.2, `overflow-mode="Ellipsis"`, `word-wrapping="false"`;
  difficulty keeps the existing per-difficulty colors; stars `#ffaa22`.
- Accuracy: gradient `Lerp(#EEFF9E, #FF6347, pow(acc, 14))` (BeatLeader's
  exact formula). PP: `#B856FF` with `<size=70%>pp</size>` suffix.
  FC: `#57ff8a`. Modifiers: `#999999`.
- Font: BSML default (the game's Teko-Medium SDF) — native look for free.
- Format helpers (`FormatAcc`, `FormatPP`, `TimeAgo`, diff colors) move to a
  small shared header (`Format.hpp`) used by both the cell and the modal.

## 4. FeedViewController rework

- **Table:** replace `CreateScrollableList` with an `HMUI::TableView` whose
  `IDataSource` is the view controller (`CellSize()` ≈ 12, `NumberOfCells()`,
  `CellForIdx()` → `FeedCell::GetCell` + `SetData`). Cell click keeps the
  existing behavior (open detail modal, clear selection).
- **Header:** same controls (ID input, Refresh, player filter dropdown,
  status line) arranged in one tightened row group; no functional change.
- **Refresh policy:** feed data persists across view activations.
  `DidActivate` triggers a fetch only when the feed is empty or the last
  successful fetch is older than 120 seconds. The Refresh button always
  forces a fetch. Track `lastFetchTime` next to the entries.
- **State cleanup:** the file-static globals (`gEntries`, `gVisible`,
  `gPlayers`, `gPlayerFilter`, `gSelected`, `gBusyPlaying`, generation
  counters) consolidate into one file-static `struct FeedState` instance.
  Staying file-static is deliberate — it survives view re-creation, which is
  the current, working behavior. The `static ownedNames` hack in
  `RebuildFilter` is replaced by owned storage inside `FeedState`.

## 5. Detail modal redesign

Same data and buttons, BeatLeader card look, built from inline BSML:

- Cover image ~20×20; song name size 5; author size 3.5 `#888888`.
- Avatar + player name row (avatar 4×4).
- Stats line using the shared format helpers (colored acc/pp/FC/modifiers,
  time-ago).
- Existing Play / Download & Play button and all of `SongInstaller` logic
  untouched, including the download/installing/grace-period state machine.

## 6. Error handling

- Image load failure → cell keeps a neutral placeholder sprite; failed URLs
  are not retried within the session.
- Feed fetch error paths, messages, and the generation guard against stale
  results are unchanged.
- All Unity object access stays on the main thread via
  `BSML::MainThreadScheduler`; worker threads touch only plain data.

## 7. Out of scope

- No new features (no pagination, no notifications, no extra filters).
- No changes to `Web.cpp` (SSL stance stays as documented), `SongInstaller`,
  config schema, or menu registration.
- No custom asset bundle; rounded corners/skew only where achievable with
  stock BSML (`bg="round-rect-panel"`) or cloned game sprites.

## 8. Versioning & docs

- Version bump to **0.4.0** in `qpm.json` (propagates to `mod.json` via
  `qpm qmod manifest`).
- README: update the features section for the new UI, refresh the test plan.

## 9. Test plan (on headset)

Existing checklist from README, plus:

1. Covers and avatars appear as rows scroll into view; placeholder shows
   while loading.
2. Fast scroll up/down — no cell ever shows another row's image (stale-guard).
3. Reopen the view within 2 minutes → instant, no network; after 2 minutes or
   Refresh → refetch.
4. Fallback path with 20 followed players loads noticeably faster than
   v0.3.0 (parallel fetch).
5. Player filter, detail modal, Play / Download & Play all behave as before.
6. Airplane mode: feed error message unchanged; cards without images render
   cleanly.

## 10. Build verification

`qpm restore` → `qpm s build` must compile clean; `qpm s qmod` must produce
`SnipeFeed.qmod`. On-headset checks per the test plan above.
