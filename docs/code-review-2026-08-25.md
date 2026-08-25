# SnipeFeed Reliability Review — 2026-08-25

Full-codebase review (both platforms) focused on reliability, crash-safety, and works-as-documented correctness. Vendored `include/zip` (miniz) excluded except for how first-party code calls it.

Verdicts: **Quest — Needs fixes** (architecture solid, one crash-class bug on every async path). **PCVR — Needs fixes** (defensively written, no outright crash path, but one guaranteed hang path and a cluster of misleading error paths).

Checkboxes track the fix phase.

---

## Quest (C++ / Scotland2)

### Critical

- [ ] **Q-C1 — `UnityW` liveness guards are not GC-safe → use-after-free on every async completion path.**
  `state.activeView` (src/FeedView.cpp:63, written :599, read :376-377, :390, :418); `UnityW<FeedView> weakSelf` in the download/install callbacks incl. the 5-second `ScheduleAfterTime` (src/FeedView.cpp:303-344); modal sprite callbacks (src/FeedView.cpp:198-214); deferred-DidActivate retry (src/FeedView.cpp:588-592); `UnityW<FeedCell> self` in every cell's sprite callbacks (src/FeedCell.cpp:88-102).
  `UnityW` is a raw pointer whose "alive" check dereferences that pointer; stored inside native `std::function` queues it neither roots the object nor nulls on death. Routine sequence: request in flight → user leaves menu → tab destroyed → GC collects view → callback fires on freed memory. The likely source of rare menu-transition crashes.
  **Fix:** capture `SafePtrUnity<FeedView>` / `SafePtrUnity<FeedCell>` in every callback that can outlive a frame; make `state.activeView` a `SafePtrUnity<FeedView>` and clear it in an `OnDestroy` override.

### Important

- [ ] **Q-I1 — TLS peer verification disabled while sending the BeatLeader login cookie.**
  src/Web.cpp:43 (`CURLOPT_SSL_VERIFYPEER, 0L`); cookie attached at src/Feed.cpp:126-128. Session cookie stealable by anyone on the network; also makes the BeatSaver zip download MITM-able (feeds Q-I2).
  **Fix:** ship a CA bundle (`cacert.pem`) and set `CURLOPT_CAINFO`; verify at minimum on the cookie-bearing request.

- [ ] **Q-I2 — Unvalidated `hash` builds the download URL and extraction dir; vendored zip honors symlink entries.**
  src/SongInstaller.cpp:46 (URL), :90 (`targetFolder`); include/zip/src/zip.c:346-366 creates symlinks with unrestricted targets → hostile archive can write outside the target dir.
  **Fix:** validate `hash` matches `^[0-9a-fA-F]{40}$` before use; refuse archives containing symlink entries.

- [ ] **Q-I3 — Failed/partial extraction leaves a corrupt level folder that SongCore then scans.**
  src/SongInstaller.cpp:92-98.
  **Fix:** extract into a temp dir and rename into place on success; `remove_all` on failure (error_code overloads).

- [ ] **Q-I4 — "0 follows", "no scores", "expired cookie", and "network down" all collapse into a misleading login error.**
  src/Feed.cpp:145 (empty 200 treated as failure), :281 (fallback message).
  **Fix:** three-state outcome in `TryFetchFriendScores` (entries / success-but-empty / failure); surface "You don't follow anyone yet" / "Network error" directly.

- [ ] **Q-I5 — No `curl_global_init`, no `CURLOPT_NOSIGNAL`.**
  src/Web.cpp:21. Lazy global init isn't thread-safe; works today by luck (first call single-threaded).
  **Fix:** `curl_global_init(CURL_GLOBAL_ALL)` once in `late_load`; set `CURLOPT_NOSIGNAL, 1L` in `Get`.

- [ ] **Q-I6 — Sprite cache unbounded; `ClearCache()` is dead code.**
  src/SpriteCache.cpp:19. Rooted textures accumulate all session (~256 KB+ each) on a memory-pressure-prone device.
  **Fix:** LRU cap (~150 entries) or threshold clear at refresh.

- [ ] **Q-I7 — Refresh completion destroys the filter dropdown even while it's open → possible stuck input blocker.**
  src/FeedView.cpp:103-125 (`RebuildFilter` unconditionally `Destroy`s).
  **Fix:** dismiss the dropdown's modal first, skip rebuild while open, or update values in place.

### Minor

- [ ] **Q-M1** — Modal not hidden on the refresh-failure path (src/FeedView.cpp:386-397; success path hides at :422).
- [ ] **Q-M2** — TMP rich-text injection: network strings concatenated into markup unescaped (src/FeedCell.cpp:78-79, include/Format.hpp:49-80, src/FeedView.cpp:172-183). Map titles like `<3` break rendering. One escaping helper.
- [ ] **Q-M3** — Detached workers have no top-level try/catch → uncaught exception is `std::terminate` (src/Feed.cpp:258-341, src/SongInstaller.cpp:43-102). Wrap bodies; report failure via `onDone`.
- [ ] **Q-M4** — `SanitizeInput` keeps query strings: pasted profile URL with `?tab=...` fails resolution confusingly (src/Feed.cpp:100-110). Strip at `?`/`#`.
- [ ] **Q-M5** — Negative curl codes shown as "HTTP -6" (src/SongInstaller.cpp:52, :84; src/Feed.cpp:163). Map `code < 0` to "Network error".
- [ ] **Q-M6** — `int args = 2; ... &args` with no-op extract callback (src/SongInstaller.cpp:91-94). Pass `nullptr` or log per-file progress.
- [ ] **Q-M7** — README: "no login, no credentials stored" (README.md:18) contradicts the cookie path (:65); dependency list omits SongCore (mod.json requires it).
- [ ] **Q-M8** — No `CURLOPT_ACCEPT_ENCODING` → uncompressed 100-score JSON. Set to `""` for gzip.
- [ ] **Q-M9** — Thread-per-image download spike (src/SpriteCache.cpp:43); small shared queue would smooth it.

### Reviewer recommendations (Quest)

1. One liveness idiom: a `SafePtrUnity` capture + alive-check helper replacing all five hand-rolled `UnityW` patterns.
2. Atomic installs: temp-dir + rename + 40-hex validation → "either a valid folder appears or nothing changes".
3. Ship a CA bundle; the threat model changed when cookie reuse was added.
4. Replace the fixed 5-second post-install wait (src/FeedView.cpp:321-342) with SongCore's songs-loaded callback.
5. Route feed strings through one escaping helper before TMP markup.

---

## PCVR (C# / BSIPA)

### Critical

- [ ] **P-C1 — Zip extraction, recursive delete, and all file writes run on the Unity main thread → VR freeze on every map install.**
  pcvr/Services/SongInstaller.cs:64-66, 88-113. No `ConfigureAwait(false)` anywhere, so the continuation after the download resumes on the main thread and synchronously deletes/creates/extracts a 10-50 MB map — hundreds of ms to seconds of dropped frames / compositor grey-out.
  **Fix:** wrap the disk work in `Task.Run(...)` (paths are already computed main-thread-side beforehand); everything after correctly resumes on the main context.

### Important

- [ ] **P-I1 — Logged-in user with an empty friends feed sees "Couldn't reuse a BeatLeader PC login".**
  pcvr/Services/BeatLeaderService.cs:256-274 (`entries.Count > 0 ? entries : null`), :37-58. Success-empty is indistinguishable from no-login; also fires the whole fallback chain every refresh for such users. Parse failure and empty feed conflated too (:269-270).
  **Fix:** three-state result (entries / authenticated-but-empty / no-login); surface "No recent scores from the players you follow" as success.

- [ ] **P-I2 — BeatLeader installed but not signed in → 8-second stall on effectively every tab visit.**
  pcvr/Services/BeatLeaderService.cs:126, 151-155, 311-334 (`WaitForBeatLeaderLogin` never remembers the login didn't appear; `WaitLogin` never completes on failure) + auto-refresh in pcvr/UI/SnipeFeedView.cs:180-189.
  **Fix:** session-scoped memo after the first full-timeout wait; skip the wait subsequently.

- [ ] **P-I3 — An exception in the cookie-auth path aborts the entire fetch, skipping the Unity path and the public fallback.**
  pcvr/Services/BeatLeaderService.cs:198-224 (unprotected `new Uri`, `Cookie` add, `GetAsync`; contrast the try/caught Unity variant :227-250).
  **Fix:** try/catch inside the method; return `(null, 0)` so the fallback chain proceeds.

- [ ] **P-I4 — A failed refresh wipes the previously good feed.**
  pcvr/UI/SnipeFeedView.cs:240-249 (`_entries.Clear()` before the `result.Success` check); then `_entries.Count == 0` re-triggers refresh every tab show, compounding P-I2.
  **Fix:** clear/replace only on success; on failure keep stale rows and show the error in the status line.

- [ ] **P-I5 — Failed extraction leaves a partial map folder in CustomLevels.**
  pcvr/Services/SongInstaller.cs:64-66, 77-85. SongCore repeatedly tries to load a broken level. Also a Windows pending-delete race between `Directory.Delete` and immediate `CreateDirectory`.
  **Fix:** best-effort cleanup in the catch; better, extract to a temp sibling and `Directory.Move` into place (atomic installs).

- [ ] **P-I6 — Sprite cache unbounded and never destroys textures.**
  pcvr/Services/SpriteCache.cs:19, :80. Hundreds of `Texture2D`s over a long session, no ceiling.
  **Fix:** LRU cap (~150); `Object.Destroy` texture + sprite on evict; clear on soft restart.

- [ ] **P-I7 — Install completion writes into whichever score's modal is open now, not the one it belongs to.**
  pcvr/UI/SnipeFeedView.cs:404-444 (post-await `SetPlayButton`/`SetDetail` target the shared modal unconditionally). Score B's modal can show score A's error and a wrongly-enabled button.
  **Fix:** guard post-await UI writes with `ReferenceEquals(_selected, entry)`; recompute via `UpdatePlayButton()` instead of hardcoding.

- [ ] **P-I8 — Launch path has no re-entrancy/transition guard and runs outside try/catch.**
  pcvr/UI/SnipeFeedView.cs:396-401 (already-installed branch outside try), :447-506 (`Setup`/`DismissFlowCoordinator` unprotected). HMUI throw during a concurrent transition → half-dismissed flow (the exact menu-state corruption class).
  **Fix:** `_launching` flag checked alongside `_installing`; wrap `LaunchLevel` body in try/catch with a status-line fallback.

### Minor

- [ ] **P-M1** — `[OnStart]` async Task is fire-and-forget with unobserved exceptions (pcvr/Plugin.cs:28-34); silent no-tab failure, and `GameplaySetup.Instance == null` at :45 returns silently. Wrap + log; verify BSIPA accepts Task-returning `[OnStart]`.
- [ ] **P-M2** — `Escape` renders literal `&amp;`/`&lt;` on screen — TMP doesn't decode entities (pcvr/Utils/Formatting.cs:115). Use `<noparse>` or zero-width-space breaking instead.
- [ ] **P-M3** — Hash-case asymmetry in level lookup: tries raw + lowercase, never uppercase; level IDs embed uppercase (pcvr/Services/SongInstaller.cs:31-39). Normalize `ToUpperInvariant()` once.
- [ ] **P-M4** — BeatSaver version fallback (`FirstOrDefault()`) can install the wrong map version, then the hash lookup never matches → perpetual "still loading" (pcvr/Services/SongInstaller.cs:52-53). Fail with "map version no longer on BeatSaver" instead.
- [ ] **P-M5** — Silent no-op when the open picker doesn't contain the level (pcvr/UI/SnipeFeedView.cs:487-500) — add a status message. Party mode takes the picker branch, diverging from the README table (arguably better; note it).
- [ ] **P-M6** — Modal reparent-back relies on HMUI invoking the hide callback for an already-hidden modal (pcvr/UI/SnipeFeedView.cs:523-532, click-off in .bsml:99). If it doesn't, the modal leaks per menu rebuild. Reparent unconditionally in `HideModal`.
- [ ] **P-M7** — Scores stepper on the public path only truncates; pull size is config-only `ScoresPerPlayer` (pcvr/Services/BeatLeaderService.cs:94-98) — stepper 100 can never exceed 60. Scale or document.
- [ ] **P-M8** — Batch: time-ago/rank strings go stale until rebuild (pcvr/UI/SnipeFeedView.cs:594); losing `loginTask` in `Task.WhenAny` left unobserved (BeatLeaderService.cs:320); no decompressed-size ceiling in `ExtractZipSafely` (zip bomb → disk fill) — cap ~2 GB via `entry.Length` sum; raw `ex.Message` interpolated into rich-text fields (SnipeFeedView.cs:265, 412, 439).

### Reviewer recommendations (PCVR)

1. Make the main-thread contract explicit (comment both services; a `RunOnMain` helper) and push CPU/disk work off-main with `Task.Run`.
2. Model the friends-feed outcome as a three-state result — P-I1 and the misleading messages fall out naturally.
3. Centralize "is this completion still relevant?" (`StillCurrent(entry)` helper) for every post-await UI write.
4. Atomic installs: download → extract to temp (off-main) → `Directory.Move` → refresh.
5. Session-level auth memo (login confirmed / absent / last check) to kill the 8s toll and repeated 401 probes.
6. Bound the sprite cache; keep the good coalescing/failure-memo design.
