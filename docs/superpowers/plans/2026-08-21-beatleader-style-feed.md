# BeatLeader-Style Feed Redesign Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild SnipeFeed's feed view as BeatLeader-style score cards (avatars + cover art in reusable table cells) and apply the approved performance/cleanup improvements, shipping as v0.4.0.

**Architecture:** The feed list moves from BSML's stock text list to a custom `HMUI::TableCell` ("FeedCell") rendered by the view controller acting as the table's `IDataSource` — the BetterSongSearch pattern, styled with BeatLeader's colors/layout. A new `SpriteCache` unit downloads and caches avatar/cover sprites. The data layer gains image URLs and a parallel fetch.

**Tech Stack:** C++20, qpm-rust, CMake+Ninja, Android NDK (managed by qpm), beatsaber-hook, custom-types, BSML ^0.4.55, SongCore, libcurl, rapidjson. Target: Beat Saber 1.40.8 (build 7379), Quest aarch64, Scotland2.

**Spec:** `docs/superpowers/specs/2026-08-21-beatleader-style-feed-design.md`

## Global Constraints

- Target game version: **Beat Saber 1.40.8 (7379)**, package version `1.40.8_7379`; do not change any dependency version in `qpm.json`.
- No new qpm dependencies. Everything needed (BSML, custom-types, songcore, libcurl) is already declared.
- All Unity/IL2CPP object access on the **main thread only** (`BSML::MainThreadScheduler::Schedule`); worker threads touch plain C++ data only.
- `Web.cpp`, `SongInstaller.{hpp,cpp}`, `ModConfig.hpp`, `main.cpp` are **out of scope** — do not modify.
- **Test cycle:** this project has no host-side unit test harness. Every task's gate is a clean compile: `qpm s build` must exit 0 with no warnings introduced by the change. Behavioral verification is the on-headset checklist in Task 8. Do not claim a task complete without a passing build.
- **Toolchain (this machine):** qpm is NOT on PATH. Every shell session that builds must first run:
  ```powershell
  $env:PATH += ";C:\Users\notni\Desktop\BeatSaberQuestMod\tools\qpm"
  ```
  Build from the repo root: `qpm s build` (wraps `pwsh ./scripts/build.ps1`).
- **Reference sources** (read-only, on disk): `C:\Users\notni\Desktop\BeatSaberQuestMod\references\beatleader-qmod` and `...\BetterSongSearchQuest`. If a BSML/HMUI API in this plan doesn't compile, check the restored headers in `extern/includes/` and the matching reference usage before improvising.
- Commit after every task; commit messages end with:
  `Co-Authored-By: Claude Fable 5 <noreply@anthropic.com>`

---

### Task 0: Toolchain restore, .gitignore, baseline build

**Files:**
- Create: `.gitignore`
- No source changes.

**Interfaces:**
- Consumes: nothing.
- Produces: a machine that compiles v0.3.0 cleanly; a repo that won't commit build artifacts.

- [ ] **Step 1: Create `.gitignore`** (the repo has none; `qpm restore` creates large untracked trees)

```gitignore
build/
extern/
includes/
.cache/
*.qmod
ndkpath.txt
```

Note: `ndkpath.txt` is machine-specific (it currently points into this machine's `AppData`), so it belongs in `.gitignore`. It is already committed; untrack it with `git rm --cached ndkpath.txt` (the file stays on disk).

- [ ] **Step 2: Restore dependencies and resolve NDK**

```powershell
$env:PATH += ";C:\Users\notni\Desktop\BeatSaberQuestMod\tools\qpm"
qpm restore
qpm ndk resolve -d
```

Expected: `extern/includes/` appears, containing `bsml/`, `beatsaber-hook/`, `songcore/`, etc.

- [ ] **Step 3: Baseline build**

Run: `qpm s build`
Expected: exit 0, `build/libsnipefeed.so` produced. If this fails, STOP — fix the environment before any code task.

- [ ] **Step 4: Verify CMake picks up new sources automatically**

Open `CMakeLists.txt` and confirm it globs sources (look for a line like `file(GLOB_RECURSE cpp_files ${SOURCE_DIR}/*.cpp)`). If it lists files explicitly instead, remember: Tasks 4 and 5 must add their new `.cpp` files to that list.

- [ ] **Step 5: Commit**

```bash
git rm --cached ndkpath.txt
git add .gitignore
git commit -m "chore: add .gitignore, untrack machine-specific ndkpath.txt"
```

---

### Task 1: Data layer — avatar and cover URLs

**Files:**
- Modify: `include/Feed.hpp` (FeedEntry struct)
- Modify: `src/Feed.cpp` (FollowedPlayer, ParseScore, FetchFollowing)

**Interfaces:**
- Consumes: existing `FeedEntry`, `ParseScore`, `FetchFollowing`.
- Produces: `FeedEntry::avatarUrl` and `FeedEntry::coverUrl` (`std::string`, may be empty), populated on every feed path. Tasks 5 and 7 read these.

- [ ] **Step 1: Add fields to `FeedEntry`** in `include/Feed.hpp` after `songHash`:

```cpp
        std::string avatarUrl;   // player avatar image URL (may be empty)
        std::string coverUrl;    // song cover art URL (may be empty)
```

- [ ] **Step 2: Extend `FollowedPlayer`** in `src/Feed.cpp`:

```cpp
    struct FollowedPlayer {
        std::string id;
        std::string name;
        std::string avatar;
    };
```

- [ ] **Step 3: Parse the URLs in `ParseScore`.** Inside the existing `player` block, add avatar; seed from fallback first:

```cpp
        entry.playerId = fallbackPlayer.id;
        entry.playerName = fallbackPlayer.name;
        entry.avatarUrl = fallbackPlayer.avatar;

        auto player = score.FindMember("player");
        if (player != score.MemberEnd() && player->value.IsObject()) {
            auto id = GetString(player->value, "id");
            auto name = GetString(player->value, "name");
            auto avatar = GetString(player->value, "avatar");
            if (!id.empty()) entry.playerId = id;
            if (!name.empty()) entry.playerName = name;
            if (!avatar.empty()) entry.avatarUrl = avatar;
        }
```

and inside the existing `song` block:

```cpp
                entry.songName = GetString(song->value, "name");
                entry.songAuthor = GetString(song->value, "author");
                entry.songHash = GetString(song->value, "hash");
                entry.coverUrl = GetString(song->value, "coverImage");
```

- [ ] **Step 4: Parse avatar in `FetchFollowing`:**

```cpp
            FollowedPlayer fp;
            fp.id = GetString(p, "id");
            fp.name = GetString(p, "name");
            fp.avatar = GetString(p, "avatar");
```

Also update the two `FollowedPlayer` brace-initializations elsewhere in the file (`FollowedPlayer unknown{"", "?"};` → `FollowedPlayer unknown{"", "?", ""};` and `FollowedPlayer self{input, input};` → `FollowedPlayer self{input, input, ""};`).

- [ ] **Step 5: Build**

Run: `qpm s build` → exit 0.

- [ ] **Step 6: Commit**

```bash
git add include/Feed.hpp src/Feed.cpp
git commit -m "feat: capture avatar and cover art URLs in feed entries"
```

---

### Task 2: Parallel fallback score fetch

**Files:**
- Modify: `src/Feed.cpp` (the per-player loop in `FetchFeedAsync`, currently `for (size_t i = 0; i < following.size(); i++) { ... }`)

**Interfaces:**
- Consumes: `FetchRecentScores(FollowedPlayer const&, int, std::vector<FeedEntry>&)` (unchanged).
- Produces: same `FeedResult` as before, fetched by up to 4 concurrent workers. No signature changes.

- [ ] **Step 1: Replace the sequential loop** in `FetchFeedAsync` (Path 2, after `FetchFollowing` succeeds) with:

```cpp
            if (onProgress) onProgress("Loading scores... 0/" + std::to_string(following.size()));

            std::atomic<size_t> nextIdx{0};
            std::atomic<size_t> doneCount{0};
            std::mutex mergeMutex;
            size_t workerCount = std::min<size_t>(4, following.size());
            std::vector<std::thread> workers;
            workers.reserve(workerCount);
            for (size_t w = 0; w < workerCount; w++) {
                workers.emplace_back([&] {
                    while (true) {
                        size_t i = nextIdx.fetch_add(1);
                        if (i >= following.size()) break;
                        std::vector<FeedEntry> local;
                        FetchRecentScores(following[i], scoresPerPlayer, local);
                        size_t done = doneCount.fetch_add(1) + 1;
                        if (onProgress)
                            onProgress("Loading scores... " + std::to_string(done) + "/" + std::to_string(following.size()));
                        std::lock_guard<std::mutex> lock(mergeMutex);
                        result.entries.insert(result.entries.end(),
                                              std::make_move_iterator(local.begin()),
                                              std::make_move_iterator(local.end()));
                    }
                });
            }
            for (auto& t : workers) t.join();
```

Add `#include <mutex>` and `#include <atomic>` to the top of `Feed.cpp`. The by-reference captures are safe: `workers` are all `join()`ed inside the same enclosing worker-thread lambda before anything captured goes out of scope. `onProgress` being called from multiple threads is safe — the caller (FeedViewController) already marshals it through `MainThreadScheduler`.

- [ ] **Step 2: Build**

Run: `qpm s build` → exit 0.

- [ ] **Step 3: Commit**

```bash
git add src/Feed.cpp
git commit -m "perf: fetch followed players' scores with 4 parallel workers"
```

---

### Task 3: Shared format helpers (`Format.hpp`)

**Files:**
- Create: `include/Format.hpp`
- Modify: `src/FeedViewController.cpp` (delete the now-duplicated anonymous-namespace helpers `TimeAgo`, `DiffColor`, `AccColor`, `DiffLabel`; include and qualify with `Format::`)

**Interfaces:**
- Consumes: nothing.
- Produces (namespace `SnipeFeed::Format`, all `inline`, used by Tasks 5–7):
  - `std::string TimeAgo(long long timepost)` — "just now" / "5m ago" / "3h ago" / "2d ago"
  - `char const* DiffColor(std::string const& diff)` — hex per difficulty
  - `std::string DiffLabel(std::string const& diff)` — "ExpertPlus" → "Ex+"
  - `std::string AccColorHex(float acc)` — BeatLeader gradient hex
  - `std::string FormatAcc(float acc)` — `<color=...>96.42%</color>`
  - `std::string FormatPP(float pp)` — `<color=#B856FF>312<size=70%>pp</size></color>`, empty when pp <= 0
  - `std::string SongLine(FeedEntry const&)` — song + colored diff + stars (rich text)
  - `std::string StatsLine(FeedEntry const&)` — acc + pp + FC + modifiers (rich text)

- [ ] **Step 1: Create `include/Format.hpp`:**

```cpp
#pragma once

#include "Feed.hpp"

#include <algorithm>
#include <cmath>
#include <ctime>
#include <format>
#include <string>

// Text formatting shared by the feed cells and the detail modal.
// Colors and the accuracy gradient match the official BeatLeader mod
// (references/beatleader-qmod/include/Utils/FormatUtils.hpp).
namespace SnipeFeed::Format {

    inline std::string TimeAgo(long long timepost) {
        if (timepost <= 0) return "";
        long long diff = static_cast<long long>(std::time(nullptr)) - timepost;
        if (diff < 0) diff = 0;
        if (diff < 60) return "just now";
        if (diff < 3600) return std::to_string(diff / 60) + "m ago";
        if (diff < 86400) return std::to_string(diff / 3600) + "h ago";
        return std::to_string(diff / 86400) + "d ago";
    }

    inline char const* DiffColor(std::string const& diff) {
        if (diff == "Easy") return "#3cb371";
        if (diff == "Normal") return "#59b0f4";
        if (diff == "Hard") return "#ff6347";
        if (diff == "Expert") return "#bf2a42";
        if (diff == "ExpertPlus") return "#8f48db";
        return "#bbbbbb";
    }

    inline std::string DiffLabel(std::string const& diff) {
        return diff == "ExpertPlus" ? "Ex+" : diff;
    }

    // BeatLeader's accuracy color: lerp #EEFF9E -> #FF6347 by pow(acc, 14).
    inline std::string AccColorHex(float acc) {
        float t = std::pow(std::clamp(acc, 0.0f, 1.0f), 14.0f);
        auto lerp = [](float a, float b, float t) { return a + (b - a) * t; };
        int r = static_cast<int>(std::lround(lerp(0.93f, 1.00f, t) * 255));
        int g = static_cast<int>(std::lround(lerp(1.00f, 0.39f, t) * 255));
        int b = static_cast<int>(std::lround(lerp(0.62f, 0.28f, t) * 255));
        return std::format("#{:02X}{:02X}{:02X}", r, g, b);
    }

    inline std::string FormatAcc(float acc) {
        return std::format("<color={}>{:.2f}%</color>", AccColorHex(acc), acc * 100.0f);
    }

    inline std::string FormatPP(float pp) {
        if (pp <= 0.0f) return "";
        return std::format("<color=#B856FF>{:.0f}<size=70%>pp</size></color>", pp);
    }

    inline std::string SongLine(FeedEntry const& e) {
        std::string line = e.songName;
        if (!e.difficulty.empty())
            line += "  <size=75%><color=" + std::string(DiffColor(e.difficulty)) + ">" + DiffLabel(e.difficulty) + "</color></size>";
        if (e.stars > 0.0f)
            line += std::format("  <size=75%><color=#ffaa22>{:.1f}\u2605</color></size>", e.stars);
        return line;
    }

    inline std::string StatsLine(FeedEntry const& e) {
        std::string line = FormatAcc(e.accuracy);
        std::string pp = FormatPP(e.pp);
        if (!pp.empty()) line += "  " + pp;
        if (e.fullCombo) line += "  <color=#57FF8A>FC</color>";
        if (!e.modifiers.empty()) line += "  <color=#999999>+" + e.modifiers + "</color>";
        return line;
    }
}
```

- [ ] **Step 2: Update `src/FeedViewController.cpp`.** Add `#include "Format.hpp"`. Delete the anonymous-namespace functions `TimeAgo`, `DiffColor`, `AccColor`, `DiffLabel` and rewrite the three remaining text builders to use the new helpers:

```cpp
    // Line 1: song, colored difficulty, stars.
    std::string CellTitle(FeedEntry const& e) { return Format::SongLine(e); }

    // Line 2, list version: the LevelListTableCell subtitle does NOT parse
    // rich text (tags render literally), so this stays plain.
    std::string CellSubtitle(FeedEntry const& e) {
        std::string line = e.playerName;
        line += std::format("   {:.2f}%", e.accuracy * 100.0f);
        if (e.pp > 0.0f) line += std::format("   {:.0f}pp", e.pp);
        if (e.fullCombo) line += "   FC";
        if (!e.modifiers.empty()) line += "   +" + e.modifiers;
        line += "   " + Format::TimeAgo(e.timepost);
        return line;
    }

    // Modal version: rich text works there.
    std::string RichSubtitle(FeedEntry const& e) {
        std::string line = "<color=#ffffff>" + e.playerName + "</color>";
        line += "   " + Format::StatsLine(e);
        line += "   <color=#777777>" + Format::TimeAgo(e.timepost) + "</color>";
        return line;
    }
```

(`CellTitle`/`CellSubtitle` are deleted entirely in Task 6 when the stock list goes away; keeping them thin here keeps this task compiling standalone.)

- [ ] **Step 3: Build**

Run: `qpm s build` → exit 0.

- [ ] **Step 4: Commit**

```bash
git add include/Format.hpp src/FeedViewController.cpp
git commit -m "refactor: extract BeatLeader-style format helpers into Format.hpp"
```

---

### Task 4: SpriteCache

**Files:**
- Create: `include/SpriteCache.hpp`
- Create: `src/SpriteCache.cpp`
- Modify (only if Task 0 Step 4 found an explicit source list): `CMakeLists.txt`

**Interfaces:**
- Consumes: `Web::Get(url, timeoutSeconds, out)` from `Web.hpp`.
- Produces (namespace `SnipeFeed::SpriteCache`, used by Tasks 5 and 7):
  - `void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite)` — **call from the main thread only**; callback fires on the main thread with a non-null sprite, possibly synchronously on a cache hit; on failure the callback never fires.
  - `void ClearCache()`

- [ ] **Step 1: Create `include/SpriteCache.hpp`:**

```cpp
#pragma once

#include "UnityEngine/Sprite.hpp"

#include <functional>
#include <string>

// URL -> Sprite cache for avatars and cover art, modeled on the official
// BeatLeader mod's Sprites::get_Icon. Main-thread only: callbacks fire on
// the main thread (synchronously on a cache hit) and never with nullptr —
// on failure the callback simply never fires, so callers keep their
// placeholder. Failed URLs are not retried within the session.
namespace SnipeFeed::SpriteCache {
    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite);
    void ClearCache();
}
```

- [ ] **Step 2: Create `src/SpriteCache.cpp`:**

```cpp
#include "SpriteCache.hpp"
#include "Web.hpp"
#include "main.hpp"

#include "bsml/shared/BSML-Lite.hpp"
#include "bsml/shared/BSML/MainThreadScheduler.hpp"

#include <cstring>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace SnipeFeed::SpriteCache {

    namespace {
        // Every container below is read and written on the main thread only;
        // the download thread hands its result back via MainThreadScheduler.
        std::unordered_map<std::string, SafePtrUnity<UnityEngine::Sprite>> cache;
        std::unordered_set<std::string> failed;
        std::unordered_map<std::string, std::vector<std::function<void(UnityEngine::Sprite*)>>> pending;

        constexpr long TIMEOUT_SECONDS = 15;
    }

    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite) {
        if (url.empty() || failed.contains(url)) return;

        auto hit = cache.find(url);
        if (hit != cache.end() && hit->second) {
            onSprite(hit->second.ptr());
            return;
        }

        // Coalesce: if a download for this URL is in flight, just queue up.
        auto inFlight = pending.find(url);
        if (inFlight != pending.end()) {
            inFlight->second.push_back(std::move(onSprite));
            return;
        }
        pending[url].push_back(std::move(onSprite));

        std::thread([url] {
            std::string body;
            long code = Web::Get(url, TIMEOUT_SECONDS, body);
            BSML::MainThreadScheduler::Schedule([url, code, body = std::move(body)] {
                auto callbacks = std::move(pending[url]);
                pending.erase(url);
                if (code != 200 || body.empty()) {
                    failed.insert(url);
                    return;
                }
                ArrayW<uint8_t> bytes(static_cast<il2cpp_array_size_t>(body.size()));
                std::memcpy(bytes.begin(), body.data(), body.size());
                auto sprite = BSML::Lite::ArrayToSprite(bytes);
                if (!sprite) {
                    SnipeFeedLogger.warn("Failed to decode image from {}", url);
                    failed.insert(url);
                    return;
                }
                cache[url] = sprite;
                for (auto& cb : callbacks) cb(sprite);
            });
        }).detach();
    }

    void ClearCache() {
        cache.clear();
        failed.clear();
    }
}
```

- [ ] **Step 3: Build** (new .cpp must be picked up — see Task 0 Step 4)

Run: `qpm s build` → exit 0.

- [ ] **Step 4: Commit**

```bash
git add include/SpriteCache.hpp src/SpriteCache.cpp
git commit -m "feat: add URL->Sprite cache with coalescing and failure memo"
```

---

### Task 5: FeedCell — the BeatLeader-style card cell

**Files:**
- Create: `include/FeedCell.hpp`
- Create: `src/FeedCell.cpp`

**Interfaces:**
- Consumes: `FeedEntry` (Task 1 fields), `SpriteCache::GetSprite` (Task 4), `Format::*` (Task 3).
- Produces (used by Task 6):
  - `static SnipeFeed::FeedCell* FeedCell::GetCell(HMUI::TableView* tableView)` — dequeue-or-create.
  - `void FeedCell::SetData(FeedEntry const& entry)` — binds texts, kicks off image loads.
  - Constant `FeedCell::CELL_HEIGHT = 12.0f` (Task 6's `CellSize()` returns it).

- [ ] **Step 1: Create `include/FeedCell.hpp`:**

```cpp
#pragma once

#include "Feed.hpp"

#include "custom-types/shared/macros.hpp"
#include "HMUI/ImageView.hpp"
#include "HMUI/SelectableCell.hpp"
#include "HMUI/TableCell.hpp"
#include "HMUI/TableView.hpp"
#include "TMPro/TextMeshProUGUI.hpp"

// One feed entry as a BeatLeader-style card: cover art left; avatar +
// player + time-ago, song line, stats line right. Reused by the TableView
// (BetterSongSearch dequeue pattern).
DECLARE_CLASS_CODEGEN(SnipeFeed, FeedCell, HMUI::TableCell) {
    DECLARE_OVERRIDE_METHOD_MATCH(void, SelectionDidChange, &HMUI::SelectableCell::SelectionDidChange, HMUI::SelectableCell::TransitionType transitionType);
    DECLARE_OVERRIDE_METHOD_MATCH(void, HighlightDidChange, &HMUI::SelectableCell::HighlightDidChange, HMUI::SelectableCell::TransitionType transitionType);
    DECLARE_OVERRIDE_METHOD_MATCH(void, WasPreparedForReuse, &HMUI::TableCell::WasPreparedForReuse);

    // Bound by BSML ids in CELL_BSML.
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, bgContainer);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, coverImage);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, avatarImage);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, playerText);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, timeText);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, songText);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, statsText);

    // Stale-guard: URLs this cell is currently waiting for. A reused cell
    // gets new values before the old sprite callback can land.
    DECLARE_INSTANCE_FIELD(StringW, pendingCoverUrl);
    DECLARE_INSTANCE_FIELD(StringW, pendingAvatarUrl);

   public:
    static constexpr float CELL_HEIGHT = 12.0f;

    static FeedCell* GetCell(HMUI::TableView* tableView);
    void SetData(SnipeFeed::FeedEntry const& entry);

   private:
    void RefreshBackground();
    void ResetImages();
};
```

- [ ] **Step 2: Create `src/FeedCell.cpp`:**

```cpp
#include "FeedCell.hpp"
#include "Format.hpp"
#include "SpriteCache.hpp"

#include "bsml/shared/BSML.hpp"
#include "HMUI/Touchable.hpp"
#include "UnityEngine/Color.hpp"
#include "UnityEngine/GameObject.hpp"

DEFINE_TYPE(SnipeFeed, FeedCell);

using namespace SnipeFeed;

namespace {
    constexpr auto REUSE_ID = "SnipeFeedCellReuse";

    // Card layout, BeatLeader style. ids bind to the DECLARE_INSTANCE_FIELDs.
    constexpr auto CELL_BSML = R"(
<horizontal id='bgContainer' bg='round-rect-panel' bg-color='#00000073' pad='1' spacing='2' horizontal-fit='Unconstrained' child-expand-width='false' child-control-width='true' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='https://raw.githubusercontent.com/RedBrumbler/Quest-BSML-Docs/gh-pages/schema.xsd'>
    <image id='coverImage' pref-width='10' pref-height='10' preserve-aspect='true'/>
    <vertical spacing='0' pref-width='78' child-expand-height='false' child-control-height='true'>
        <horizontal spacing='1' pref-height='4' child-expand-width='false' child-control-width='true'>
            <image id='avatarImage' pref-width='4' pref-height='4' preserve-aspect='true'/>
            <text id='playerText' font-size='3.5' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false' flexible-width='1000'/>
            <text id='timeText' font-size='2.5' color='#888888' align='MidlineRight' word-wrapping='false'/>
        </horizontal>
        <text id='songText' font-size='3.2' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
        <text id='statsText' font-size='3' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
    </vertical>
</horizontal>)";

    // While an image is loading (or failed) the ImageView shows as a dim
    // tile instead of a stark white square.
    constexpr UnityEngine::Color PLACEHOLDER_TINT = {1.0f, 1.0f, 1.0f, 0.15f};
    constexpr UnityEngine::Color LOADED_TINT = {1.0f, 1.0f, 1.0f, 1.0f};

    constexpr float BG_ALPHA_IDLE = 0.45f;
    constexpr float BG_ALPHA_ACTIVE = 0.8f;
}

FeedCell* FeedCell::GetCell(HMUI::TableView* tableView) {
    auto tableCell = tableView->DequeueReusableCellForIdentifier(REUSE_ID);
    if (!tableCell) {
        auto go = UnityEngine::GameObject::New_ctor("SnipeFeedCell");
        auto cell = go->AddComponent<FeedCell*>();
        cell->set_interactable(true);
        cell->set_reuseIdentifier(REUSE_ID);
        BSML::parse_and_construct(CELL_BSML, cell->get_transform(), cell);
        go->AddComponent<HMUI::Touchable*>();
        return cell;
    }
    return tableCell.cast<FeedCell>();
}

void FeedCell::SetData(FeedEntry const& entry) {
    playerText->set_text(entry.playerName);
    timeText->set_text(Format::TimeAgo(entry.timepost));
    songText->set_text(Format::SongLine(entry));
    statsText->set_text(Format::StatsLine(entry));

    ResetImages();

    pendingCoverUrl = entry.coverUrl;
    pendingAvatarUrl = entry.avatarUrl;

    auto self = UnityW<FeedCell>(this);
    if (!entry.coverUrl.empty()) {
        SpriteCache::GetSprite(entry.coverUrl, [self, url = entry.coverUrl](UnityEngine::Sprite* sprite) {
            if (!self || !self->pendingCoverUrl || static_cast<std::string>(self->pendingCoverUrl) != url) return;
            self->coverImage->set_sprite(sprite);
            self->coverImage->set_color(LOADED_TINT);
        });
    }
    if (!entry.avatarUrl.empty()) {
        SpriteCache::GetSprite(entry.avatarUrl, [self, url = entry.avatarUrl](UnityEngine::Sprite* sprite) {
            if (!self || !self->pendingAvatarUrl || static_cast<std::string>(self->pendingAvatarUrl) != url) return;
            self->avatarImage->set_sprite(sprite);
            self->avatarImage->set_color(LOADED_TINT);
        });
    }

    RefreshBackground();
}

void FeedCell::ResetImages() {
    pendingCoverUrl = StringW(nullptr);
    pendingAvatarUrl = StringW(nullptr);
    coverImage->set_sprite(nullptr);
    coverImage->set_color(PLACEHOLDER_TINT);
    avatarImage->set_sprite(nullptr);
    avatarImage->set_color(PLACEHOLDER_TINT);
}

void FeedCell::RefreshBackground() {
    if (!bgContainer) return;
    bool active = get_selected() || get_highlighted();
    bgContainer->set_color({0.0f, 0.0f, 0.0f, active ? BG_ALPHA_ACTIVE : BG_ALPHA_IDLE});
}

void FeedCell::SelectionDidChange(HMUI::SelectableCell::TransitionType) { RefreshBackground(); }
void FeedCell::HighlightDidChange(HMUI::SelectableCell::TransitionType) { RefreshBackground(); }
void FeedCell::WasPreparedForReuse() { ResetImages(); }
```

Compile notes for the implementer:
- If `tableCell.cast<FeedCell>()` doesn't compile (return type of `DequeueReusableCellForIdentifier` differs by cordl version), the reference usage is `references/BetterSongSearchQuest/include/UI/ViewControllers/SongListCellTableData.hpp:16-35` — mirror whatever compiles there against this project's `extern/includes`.
- If `bg-color` on the `<horizontal>` doesn't apply, set the color in code right after `parse_and_construct` via `RefreshBackground()` (already called from the first `SetData`).

- [ ] **Step 3: Build**

Run: `qpm s build` → exit 0.

- [ ] **Step 4: Commit**

```bash
git add include/FeedCell.hpp src/FeedCell.cpp
git commit -m "feat: BeatLeader-style feed card cell with avatar and cover art"
```

---

### Task 6: FeedViewController — table swap, state consolidation, refresh policy

**Files:**
- Modify: `include/FeedViewController.hpp` (interface declaration + data source methods)
- Modify: `src/FeedViewController.cpp` (state struct, list wiring, refresh policy)

**Interfaces:**
- Consumes: `FeedCell::GetCell` / `SetData` / `CELL_HEIGHT` (Task 5), `Format::*` (Task 3), `FetchFeedAsync` (unchanged).
- Produces: the view controller now implements `HMUI::TableView::IDataSource` (`CellForIdx`, `CellSize`, `NumberOfCells`). Task 7 reuses `OnCellClicked`'s modal fields.

- [ ] **Step 1: Rewrite the class declaration** in `include/FeedViewController.hpp`:

```cpp
#pragma once

#include "custom-types/shared/macros.hpp"
#include "GlobalNamespace/BeatmapLevel.hpp"
#include "HMUI/TableCell.hpp"
#include "HMUI/TableView.hpp"
#include "HMUI/ViewController.hpp"
#include "TMPro/TextMeshProUGUI.hpp"
#include "UnityEngine/Transform.hpp"
#include "UnityEngine/UI/Button.hpp"
#include "bsml/shared/BSML/Components/CustomListTableData.hpp"
#include "bsml/shared/BSML/Components/ModalView.hpp"

DECLARE_CLASS_CODEGEN_INTERFACES(SnipeFeed, FeedViewController, HMUI::ViewController, HMUI::TableView::IDataSource*)
{
    DECLARE_OVERRIDE_METHOD_MATCH(void, DidActivate, &HMUI::ViewController::DidActivate, bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling);

    DECLARE_OVERRIDE_METHOD_MATCH(HMUI::TableCell*, CellForIdx, &HMUI::TableView::IDataSource::CellForIdx, HMUI::TableView* tableView, int idx);
    DECLARE_OVERRIDE_METHOD_MATCH(float, CellSize, &HMUI::TableView::IDataSource::CellSize);
    DECLARE_OVERRIDE_METHOD_MATCH(int, NumberOfCells, &HMUI::TableView::IDataSource::NumberOfCells);

    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, statusText);
    DECLARE_INSTANCE_FIELD(BSML::CustomListTableData*, listData);
    DECLARE_INSTANCE_FIELD(UnityEngine::Transform*, filterContainer);
    DECLARE_INSTANCE_FIELD(BSML::ModalView*, detailModal);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, detailText);
    DECLARE_INSTANCE_FIELD(UnityEngine::UI::Button*, playButton);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, playButtonText);

    DECLARE_INSTANCE_METHOD(void, Refresh);

   public:
    void RebuildFilter();
    void RebuildList();
    void OnCellClicked(int listIdx);
    void PlaySelected();
    void LaunchLevel(GlobalNamespace::BeatmapLevel* level);
};
```

(If `DECLARE_CLASS_CODEGEN_INTERFACES` is unknown, the working reference is `references/BetterSongSearchQuest/include/UI/ViewControllers/SongList.hpp:55-72` — same custom-types generation.)

- [ ] **Step 2: Consolidate state** in `src/FeedViewController.cpp`. Replace the loose file statics (`refreshGeneration`, `refreshInFlight`, `gEntries`, `gVisible`, `gPlayers`, `gPlayerFilter`, `gSelected`, `gBusyPlaying`, and `RebuildFilter`'s `static ownedNames`) with one deliberate file-static struct (survives view re-creation, same as today):

```cpp
namespace {
    // Plain data, deliberately file-static: it survives BSML re-creating the
    // view, which is what lets the feed persist across menu visits.
    struct FeedState {
        std::atomic<int> refreshGeneration{0};
        std::atomic<bool> refreshInFlight{false};
        std::vector<FeedEntry> entries;
        std::vector<int> visible;           // list row -> entries index
        std::vector<std::string> players;   // unique player names, feed order
        std::vector<std::string> filterNames; // owned strings backing the dropdown
        std::string playerFilter;           // empty = all players
        int selected = -1;
        bool busyPlaying = false;
        long long lastFetchTime = 0;        // unix time of last successful fetch
    };
    FeedState state;

    constexpr auto FILTER_ALL = "All players";
    constexpr long long REFRESH_MAX_AGE_SECONDS = 120;
}
```

Mechanical rename throughout the file: `gEntries`→`state.entries`, `gVisible`→`state.visible`, `gPlayers`→`state.players`, `gPlayerFilter`→`state.playerFilter`, `gSelected`→`state.selected`, `gBusyPlaying`→`state.busyPlaying`, `refreshGeneration`→`state.refreshGeneration`, `refreshInFlight`→`state.refreshInFlight`. In `RebuildFilter`, replace the `static ownedNames` block with `state.filterNames`:

```cpp
    state.filterNames.clear();
    state.filterNames.push_back(FILTER_ALL);
    for (auto const& name : state.players)
        state.filterNames.push_back(name);
    std::vector<std::string_view> views(state.filterNames.begin(), state.filterNames.end());
```

On successful refresh (inside the `result.success` branch of the completion callback), record the time:

```cpp
                state.lastFetchTime = static_cast<long long>(std::time(nullptr));
```

- [ ] **Step 3: Swap the list to the FeedCell data source.** In `DidActivate`'s `firstActivation` block, keep `CreateScrollableList` (it builds the viewport/scrollbar/TableView plumbing and wires the click callback) but replace its data source and drop the old cell text path:

```cpp
        listData = BSML::Lite::CreateScrollableList(parent, {0.0f, 0.0f}, {95.0f, 50.0f}, [self](int idx) {
            self->OnCellClicked(idx);
        });
        listData->tableView->SetDataSource(reinterpret_cast<HMUI::TableView::IDataSource*>(this), false);
```

Add the data source implementations (and `#include "FeedCell.hpp"`):

```cpp
HMUI::TableCell* FeedViewController::CellForIdx(HMUI::TableView* tableView, int idx) {
    auto cell = FeedCell::GetCell(tableView);
    if (idx >= 0 && idx < static_cast<int>(state.visible.size()))
        cell->SetData(state.entries[state.visible[idx]]);
    return cell;
}

float FeedViewController::CellSize() { return FeedCell::CELL_HEIGHT; }

int FeedViewController::NumberOfCells() { return static_cast<int>(state.visible.size()); }
```

Rewrite `RebuildList` — no more `CustomCellInfo`; delete the `CellTitle`/`CellSubtitle` helpers:

```cpp
void FeedViewController::RebuildList() {
    if (!listData || !listData->tableView) return;

    state.visible.clear();
    for (int i = 0; i < static_cast<int>(state.entries.size()); i++) {
        if (!state.playerFilter.empty() && state.entries[i].playerName != state.playerFilter)
            continue;
        state.visible.push_back(i);
    }
    listData->tableView->ReloadData();
    listData->tableView->ClearSelection();

    if (statusText) {
        if (state.entries.empty()) {
            // Keep whatever error/progress message is already showing.
        } else if (state.playerFilter.empty()) {
            statusText->set_text(std::format("{} recent scores. Newest first — go snipe!", state.entries.size()));
        } else {
            statusText->set_text(std::format("{} of {} scores by {}", state.visible.size(), state.entries.size(), state.playerFilter));
        }
    }
}
```

Behavior check for the implementer: after `SetDataSource(..., false)` the click callback registered by `CreateScrollableList` must still fire (it is attached to the TableView's select event, not the data source). If clicking a card does nothing on headset, wire it explicitly instead:

```cpp
        listData->tableView->add_didSelectCellWithIdxEvent(
            custom_types::MakeDelegate<System::Action_2<UnityW<HMUI::TableView>, int>*>(
                std::function<void(UnityW<HMUI::TableView>, int)>(
                    [self](UnityW<HMUI::TableView>, int idx) { self->OnCellClicked(idx); })));
```

- [ ] **Step 4: Refresh policy.** Replace the unconditional `Refresh()` at the end of `DidActivate` with:

```cpp
    bool stale = state.entries.empty()
        || (static_cast<long long>(std::time(nullptr)) - state.lastFetchTime) > REFRESH_MAX_AGE_SECONDS;
    if (stale) {
        Refresh();
    } else {
        RebuildFilter();
        RebuildList();
    }
```

- [ ] **Step 5: Build**

Run: `qpm s build` → exit 0.

- [ ] **Step 6: Commit**

```bash
git add include/FeedViewController.hpp src/FeedViewController.cpp
git commit -m "feat: render feed as reusable card cells; consolidate state; cache-aware refresh"
```

---

### Task 7: Detail modal redesign

**Files:**
- Modify: `include/FeedViewController.hpp` (new modal fields)
- Modify: `src/FeedViewController.cpp` (modal construction in `DidActivate`, population in `OnCellClicked`)

**Interfaces:**
- Consumes: `SpriteCache::GetSprite`, `Format::*`, existing `PlaySelected` flow (untouched).
- Produces: nothing new for later tasks. `detailText`, `playButton`, `playButtonText` keep their exact names — `PlaySelected`'s download state machine writes to them.

- [ ] **Step 1: Add modal fields** to `FeedViewController.hpp` next to the existing modal fields:

```cpp
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, modalCover);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, modalAvatar);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, modalPlayerText);
    DECLARE_INSTANCE_FIELD(StringW, modalPendingCover);
    DECLARE_INSTANCE_FIELD(StringW, modalPendingAvatar);
```

Add `#include "HMUI/ImageView.hpp"` to the header.

- [ ] **Step 2: Rebuild the modal layout** in `DidActivate` (replace the current modal block; include `"SpriteCache.hpp"`):

```cpp
        // Detail modal: cover art + song text on top, avatar + player row,
        // stats, then the play button. Same data as before, card look.
        detailModal = BSML::Lite::CreateModal(get_transform(), {90.0f, 52.0f}, nullptr, true);
        auto modalLayout = BSML::Lite::CreateVerticalLayoutGroup(detailModal->get_transform());
        modalLayout->set_childControlWidth(true);
        modalLayout->set_childControlHeight(true);
        modalLayout->set_childForceExpandWidth(true);
        modalLayout->set_childForceExpandHeight(false);
        modalLayout->set_spacing(1.5f);
        modalLayout->set_padding(UnityEngine::RectOffset::New_ctor(3, 3, 3, 3));

        auto coverRow = BSML::Lite::CreateHorizontalLayoutGroup(modalLayout->get_transform());
        coverRow->set_childControlWidth(true);
        coverRow->set_childControlHeight(true);
        coverRow->set_childForceExpandWidth(false);
        coverRow->set_spacing(3.0f);
        modalCover = BSML::Lite::CreateImage(coverRow->get_transform(), nullptr, {0.0f, 0.0f}, {20.0f, 20.0f});
        auto coverElement = modalCover->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        coverElement->set_preferredWidth(20.0f);
        coverElement->set_preferredHeight(20.0f);
        modalCover->set_preserveAspect(true);
        detailText = BSML::Lite::CreateText(coverRow->get_transform(), "");
        detailText->set_fontSize(3.4f);
        detailText->set_enableWordWrapping(true);

        auto playerRow = BSML::Lite::CreateHorizontalLayoutGroup(modalLayout->get_transform());
        playerRow->set_childControlWidth(true);
        playerRow->set_childControlHeight(true);
        playerRow->set_childForceExpandWidth(false);
        playerRow->set_spacing(1.5f);
        modalAvatar = BSML::Lite::CreateImage(playerRow->get_transform(), nullptr, {0.0f, 0.0f}, {4.0f, 4.0f});
        auto avatarElement = modalAvatar->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        avatarElement->set_preferredWidth(4.0f);
        avatarElement->set_preferredHeight(4.0f);
        modalAvatar->set_preserveAspect(true);
        modalPlayerText = BSML::Lite::CreateText(playerRow->get_transform(), "");
        modalPlayerText->set_fontSize(3.2f);

        playButton = BSML::Lite::CreateUIButton(modalLayout->get_transform(), "Play", [self]() {
            self->PlaySelected();
        });
        playButtonText = playButton->GetComponentInChildren<TMPro::TextMeshProUGUI*>();
```

- [ ] **Step 3: Populate in `OnCellClicked`.** Replace the current `detailText` block with:

```cpp
    if (detailText) {
        std::string info = "<b>" + e.songName + "</b>";
        if (!e.songAuthor.empty())
            info += "\n<size=80%><color=#888888>" + e.songAuthor + "</color></size>";
        if (!e.difficulty.empty() || e.stars > 0.0f)
            info += "\n<size=85%>" + Format::SongLine(e).substr(e.songName.size()) + "</size>";
        info += "\n" + Format::StatsLine(e);
        info += "\n<size=75%><color=#777777>" + Format::TimeAgo(e.timepost) + "</color></size>";
        detailText->set_text(info);
    }
    if (modalPlayerText) modalPlayerText->set_text(e.playerName);

    // Images, with the same stale-guard the cells use.
    modalPendingCover = e.coverUrl;
    modalPendingAvatar = e.avatarUrl;
    if (modalCover) {
        modalCover->set_sprite(nullptr);
        modalCover->set_color({1.0f, 1.0f, 1.0f, 0.15f});
    }
    if (modalAvatar) {
        modalAvatar->set_sprite(nullptr);
        modalAvatar->set_color({1.0f, 1.0f, 1.0f, 0.15f});
    }
    auto weakSelf = UnityW<FeedViewController>(this);
    if (!e.coverUrl.empty()) {
        SpriteCache::GetSprite(e.coverUrl, [weakSelf, url = e.coverUrl](UnityEngine::Sprite* sprite) {
            if (!weakSelf || !weakSelf->modalCover) return;
            if (!weakSelf->modalPendingCover || static_cast<std::string>(weakSelf->modalPendingCover) != url) return;
            weakSelf->modalCover->set_sprite(sprite);
            weakSelf->modalCover->set_color({1.0f, 1.0f, 1.0f, 1.0f});
        });
    }
    if (!e.avatarUrl.empty()) {
        SpriteCache::GetSprite(e.avatarUrl, [weakSelf, url = e.avatarUrl](UnityEngine::Sprite* sprite) {
            if (!weakSelf || !weakSelf->modalAvatar) return;
            if (!weakSelf->modalPendingAvatar || static_cast<std::string>(weakSelf->modalPendingAvatar) != url) return;
            weakSelf->modalAvatar->set_sprite(sprite);
            weakSelf->modalAvatar->set_color({1.0f, 1.0f, 1.0f, 1.0f});
        });
    }
```

Delete the now-unused `RichSubtitle` helper. (`CellTitle`/`CellSubtitle` were already deleted in Task 6.)

- [ ] **Step 4: Build**

Run: `qpm s build` → exit 0.

- [ ] **Step 5: Commit**

```bash
git add include/FeedViewController.hpp src/FeedViewController.cpp
git commit -m "feat: BeatLeader-style detail modal with cover art and avatar"
```

---

### Task 8: Version bump, docs, package, on-headset verification

**Files:**
- Modify: `qpm.json` (version), `mod.json` (regenerated), `README.md`

**Interfaces:**
- Consumes: everything above.
- Produces: `SnipeFeed.qmod` v0.4.0 and updated docs.

- [ ] **Step 1: Bump the version** in `qpm.json`: `"version": "0.4.0"` (in the `info` block). Then regenerate the manifest:

```powershell
qpm qmod manifest
```

Confirm `mod.json` now says `0.4.0` and still targets `packageVersion` `1.40.8_7379`.

- [ ] **Step 2: Update `README.md`:** retitle the "v0.3.0 features" section to "v0.4.0 features" and describe the new UI (BeatLeader-style score cards with player avatars and song cover art, parallel loading, 2-minute feed cache). Update "Known limitations" heading version to v0.4.0. Add to the test plan the four new checks from Step 4 below.

- [ ] **Step 3: Full build + package**

```powershell
qpm s build
qpm s qmod
```

Expected: exit 0, `SnipeFeed.qmod` produced in the repo root.

- [ ] **Step 4: On-headset verification** (requires the Quest; if not connected, mark this step as pending user verification and say so — do NOT claim it passed):

1. Boot game → no crash, `SnipeFeed` in mod logs (`qpm s log`).
2. Open Snipe Feed → cards render: cover left, avatar+player+time, song line, colored stats.
3. Covers/avatars appear as rows scroll into view; placeholder tiles while loading.
4. Fast scroll up/down → no cell ever shows another row's image.
5. Close and reopen the view within 2 minutes → instant, no "Loading...".
6. Press Refresh → refetch happens; with 20 followed players it completes noticeably faster than v0.3.0.
7. Player filter still narrows the list; status line correct.
8. Tap a card → modal shows big cover, avatar, colored stats; Play / Download & Play works.
9. Airplane mode → clean error message, cards without images render fine, game unaffected.

- [ ] **Step 5: Commit and push**

```bash
git add qpm.json mod.json README.md
git commit -m "chore: release v0.4.0 - BeatLeader-style feed UI"
git push origin main
```

---

## Self-Review Notes

- Spec coverage: §1 data layer → Task 1; §1 parallel fetch → Task 2; §2 SpriteCache → Task 4; §3 cell + format helpers → Tasks 3+5; §4 controller/table/refresh/state → Task 6; §5 modal → Task 7; §8 version/docs → Task 8; §10 build gates → every task.
- Names cross-checked: `Format::SongLine/StatsLine/TimeAgo/FormatAcc/FormatPP` (Task 3) match usage in Tasks 5–7; `FeedCell::GetCell/SetData/CELL_HEIGHT` (Task 5) match Task 6; `state.*` renames consistent; `detailText/playButton/playButtonText` names preserved for `PlaySelected`.
- Known risk points are flagged inline with reference-file fallbacks (dequeue cast, `bg-color`, click event after `SetDataSource`).
