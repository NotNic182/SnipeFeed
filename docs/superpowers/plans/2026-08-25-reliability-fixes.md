# SnipeFeed Reliability Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix every finding in the 2026-08-25 reliability review — both Criticals, all 15 Importants, and the Minor batch — across the Quest C++ and PCVR C# halves of SnipeFeed, without changing how the mod looks.

**Architecture:** Two independent halves share one repo: Quest (C++/Scotland2, `src/` + `include/`) and PCVR (C#/BSIPA, `pcvr/`). Fixes are grouped by file so tasks touch disjoint code. The one structural change on Quest is a new lifetime idiom: GC-rooted `SafePtrUnity` replaces every `UnityW` liveness guard, and callbacks that cross non-attached worker threads carry **no** il2cpp pointers at all — they resolve the live view through a rooted global on the main thread.

**Tech Stack:** Quest: C++20, beatsaber-hook (SafePtrUnity), custom-types, BSML (MainThreadScheduler), SongCore, libcurl 8.5.0 (qpm), vendored kuba--/zip (miniz). PC: C# **LangVersion 8**, net48, BSIPA, BSML, SongCore, Newtonsoft.Json.

**Spec:** [docs/code-review-2026-08-25.md](../../code-review-2026-08-25.md) — issue IDs (Q-C1, P-I4, …) below refer to that document. Check its checkboxes as issues land.

## Global Constraints

- **C# is LangVersion 8 / net48** (pcvr/SnipeFeed.PC.csproj:5): no target-typed `new()`, no `is not`, no records, no file-scoped namespaces.
- **C++ is C++20 for Android aarch64** (CMakeLists.txt:13); exceptions and RTTI are on.
- **Quest thread rules:** Unity/il2cpp calls on the main thread only. `std::thread` workers are NOT attached to il2cpp — a `SafePtr`/`SafePtrUnity` must never be constructed, copied, or destroyed on them (GC-handle ops off an attached thread are illegal). Callbacks handed to `FetchFeedAsync`/`DownloadAndInstallAsync` cross those workers: they may capture only plain C++ data.
- **PC thread rules:** every async chain starts on the Unity main thread and no `ConfigureAwait(false)` is ever added — continuations resume on the main context. CPU/disk work goes off-main explicitly via `Task.Run`.
- **Match existing style:** this codebase writes rationale comments ("why", constraints) — keep that density. No "what the next line does" comments.
- **Do not change visuals/layout.** The user is happy with how it looks.
- **Behavior contract:** the README's mode-aware Play table and "modal closes instantly, never lingers" rules must hold after every change.
- **Version floors:** Beat Saber 1.40.8; BSIPA ^4.3.5; BSML ^1.12.3 (PC) / ^0.4.55 (Quest); SongCore ^3.0.0 (PC) / ^1.1.24 (Quest).

## Verification Reality (read first)

There is no test infrastructure in this repo, and the code is game-API-bound; classic TDD applies only where logic is pure. This plan deviates from strict TDD as follows, explicitly:

- **PC:** if `BeatSaberDir` is available (Task 1 records it in `docs/superpowers/plans/build-env.md`), every PC task ends with `dotnet build pcvr/SnipeFeed.PC.csproj -c Release -p:BeatSaberDir="<path>"` and must compile clean. Task 5 additionally creates a zero-dependency console check-runner for the pure formatting logic (real red→green cycle).
- **Quest:** if the qpm toolchain was set up in Task 1, every Quest task ends with `qpm s build` and must compile clean. If not, Quest tasks end with a careful self-review step instead, and the plan's final task records that Quest is **unverified by compilation** — never claim otherwise.
- The TLS change (Task 6) additionally needs an on-device smoke test (feed loads over verified TLS); record it as pending in the final task if no device is attached.

---

### Task 1: Baseline under version control (+ optional build environments)

**Files:**
- Create: `.gitignore`
- Create: `docs/superpowers/plans/build-env.md`

**Interfaces:**
- Produces: a git repo with a `baseline` commit all later tasks diff against; `build-env.md` records `QuestBuild: yes|no` and `BeatSaberDir: <path>|none` for later tasks' verify steps.

- [ ] **Step 1: Init repo and baseline commit**

```bash
cd "C:\Users\notni\Music\SnipeFeed-main\SnipeFeed-main"
git init -b main
```

Write `.gitignore`:

```gitignore
# Quest build outputs / restored deps
build/
extern/
ndkpath.txt
*.qmod

# PC build outputs
pcvr/bin/
pcvr/obj/
pcvr/Tests/bin/
pcvr/Tests/obj/
```

```bash
git add -A
git commit -m "chore: baseline import of SnipeFeed v2.0.0 (Quest + PCVR) with reliability review docs"
```

- [ ] **Step 2: Record build environments**

Write `docs/superpowers/plans/build-env.md` with the actual answers (from the user / environment):

```markdown
# Build environment for this plan's verify steps
QuestBuild: no        # yes only if qpm + `qpm restore` + `qpm ndk resolve -d` succeeded
BeatSaberDir: none    # absolute path to a Beat Saber PC install / Refs folder, or "none"
```

If the user approved Quest toolchain setup: install qpm (download `qpm-x64-pc-windows-msvc.zip` from the QuestPackageManager/QPM.CLI GitHub releases, extract `qpm.exe` to a PATH directory), then:

```bash
qpm restore
qpm ndk resolve -d
qpm s build
```

Only set `QuestBuild: yes` after that `qpm s build` compiles the UNMODIFIED baseline (proves the toolchain, isolates later failures to our edits).

- [ ] **Step 3: Commit**

```bash
git add docs/superpowers/plans/build-env.md .gitignore
git commit -m "chore: record build environment availability for verification"
```

---

### Task 2: PC — SongInstaller off-main-thread, atomic, validated (P-C1, P-I5, P-M3, P-M4, P-M8-zipcap)

**Files:**
- Modify: `pcvr/Services/SongInstaller.cs`

**Interfaces:**
- Consumes: nothing new.
- Produces: `SongInstaller.DownloadAndInstallAsync(string hash) : Task<SongInstallResult>` (signature unchanged); `SongInstaller.GetInstalledLevel(string hash) : BeatmapLevel` (signature unchanged, lookup order now upper-then-raw). Task 4 relies on both signatures being unchanged.

- [ ] **Step 1: Add `using System.Threading;`** to the using block (needed for `Thread.Sleep` in the move-retry).

- [ ] **Step 2: Replace `GetInstalledLevel` (lines 31-35) and add `IsValidHash`**

```csharp
        public static BeatmapLevel GetInstalledLevel(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return null;
            // Custom level IDs embed the hash uppercase; SongCore versions
            // differ on normalizing their argument, so try the uppercase
            // spelling first and the caller's spelling second.
            return Loader.GetLevelByHash(hash.ToUpperInvariant()) ?? Loader.GetLevelByHash(hash);
        }

        // The hash arrives from feed JSON; it becomes a URL segment and an
        // install folder name, so anything but exactly 40 hex chars is refused.
        private static bool IsValidHash(string hash)
        {
            if (string.IsNullOrEmpty(hash) || hash.Length != 40) return false;
            foreach (var c in hash)
            {
                var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!hex) return false;
            }
            return true;
        }
```

- [ ] **Step 3: Replace `DownloadAndInstallAsync` (lines 37-86)**

```csharp
        public static async Task<SongInstallResult> DownloadAndInstallAsync(string hash)
        {
            hash = (hash ?? "").Trim().ToLowerInvariant();
            if (hash.Length == 0) return Fail("This score is not a downloadable custom song.");
            if (!IsValidHash(hash)) return Fail("This score has an invalid map hash.");

            var existing = GetInstalledLevel(hash);
            if (existing != null) return new SongInstallResult { Success = true, Level = existing };

            string temp = null;
            try
            {
                var metadataResponse = await Client.GetAsync("https://api.beatsaver.com/maps/hash/" + Uri.EscapeDataString(hash));
                if ((int)metadataResponse.StatusCode == 404) return Fail("Map not found on BeatSaver.");
                if (!metadataResponse.IsSuccessStatusCode) return Fail("BeatSaver lookup failed (HTTP " + (int)metadataResponse.StatusCode + ").");

                var map = JsonConvert.DeserializeObject<BeatSaverMap>(await metadataResponse.Content.ReadAsStringAsync());
                // Only the version matching the score's hash: installing a
                // different version would never match the post-install lookup
                // and silently changes the map under the score.
                var version = map?.versions?.FirstOrDefault(v => string.Equals(v.hash, hash, StringComparison.OrdinalIgnoreCase));
                if (version == null || string.IsNullOrWhiteSpace(version.downloadURL))
                    return Fail("This score's map version is no longer available on BeatSaver.");

                var zipBytes = await Client.GetByteArrayAsync(version.downloadURL);
                if (zipBytes == null || zipBytes.Length == 0) return Fail("The map download was empty.");

                // Unity APIs (Application.dataPath) before leaving the main
                // thread; the disk work below runs on the pool.
                var customLevels = Path.GetFullPath(Path.Combine(Application.dataPath, "CustomLevels"));
                var shortHash = hash.Substring(0, 10);
                var target = Path.Combine(customLevels, "SnipeFeed_" + shortHash);
                temp = Path.Combine(customLevels, ".snipefeed_tmp_" + shortHash);
                var tempForWork = temp;

                // Deleting, decompressing and writing a multi-MB map is
                // seconds of blocking IO — off the render thread, or every
                // install freezes VR. Extract to a temp sibling and move into
                // place so a failure never leaves a half-written level folder.
                await Task.Run(() =>
                {
                    Directory.CreateDirectory(customLevels);
                    if (Directory.Exists(tempForWork)) Directory.Delete(tempForWork, true);
                    Directory.CreateDirectory(tempForWork);
                    ExtractZipSafely(zipBytes, tempForWork);
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    // Windows can hold the deleted dir in a pending state
                    // briefly; a short retry absorbs that race.
                    for (var attempt = 0; ; attempt++)
                    {
                        try { Directory.Move(tempForWork, target); break; }
                        catch (IOException) when (attempt < 3) { Thread.Sleep(50); }
                    }
                });
                temp = null; // moved into place; nothing to clean up

                Plugin.Log?.Info("Installed map " + hash + " to " + target);
                await RefreshSongCore();

                return new SongInstallResult
                {
                    Success = true,
                    Level = GetInstalledLevel(hash)
                };
            }
            catch (TaskCanceledException)
            {
                CleanupPartialInstall(temp);
                return Fail("Map download timed out.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Map install failed: " + ex);
                CleanupPartialInstall(temp);
                return Fail("Map install failed: " + ex.Message);
            }
        }

        private static void CleanupPartialInstall(string temp)
        {
            if (temp == null) return;
            try
            {
                if (Directory.Exists(temp)) Directory.Delete(temp, true);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn("Couldn't remove a partial install folder: " + ex.Message);
            }
        }
```

- [ ] **Step 4: Add a decompressed-size ceiling to `ExtractZipSafely`** — insert at the top of the class `private const long MaxExtractedBytes = 512L * 1024 * 1024;` and, inside `ExtractZipSafely` right after `using (var archive = ...)` opens, before the existing foreach:

```csharp
                // A hostile or corrupt archive must not be able to fill the
                // disk; real beatmaps are tens of MB at most.
                long declaredTotal = 0;
                foreach (var entry in archive.Entries)
                {
                    declaredTotal += entry.Length;
                    if (declaredTotal > MaxExtractedBytes)
                        throw new InvalidDataException("Map archive is unreasonably large.");
                }
```

(The existing extraction foreach stays exactly as it is — its traversal defense is correct.)

- [ ] **Step 5: Verify**

If `BeatSaberDir` in build-env.md is a path: run `dotnet build pcvr/SnipeFeed.PC.csproj -c Release -p:BeatSaberDir="<path>"`. Expected: Build succeeded, 0 errors. Otherwise: re-read the final file top to bottom checking every `await` still starts main-thread-side, `temp` is null exactly when nothing needs cleanup, and no C#9+ syntax crept in.

- [ ] **Step 6: Commit**

```bash
git add pcvr/Services/SongInstaller.cs
git commit -m "fix(pc): install maps off the main thread, atomically, with hash validation (P-C1, P-I5, P-M3, P-M4)"
```

---

### Task 3: PC — BeatLeaderService three-state feed, login memo, contained cookie path (P-I1, P-I2, P-I3, P-M7, P-M8-task)

**Files:**
- Modify: `pcvr/Services/BeatLeaderService.cs`
- Modify: `pcvr/Models/FeedEntry.cs`

**Interfaces:**
- Produces: `FeedResult.Info : string` — non-empty only on `Success == true` with zero entries; carries the user-facing "empty feed" message. Task 4 reads it.
- Produces: `ParseFriendScores` now returns an **empty list** for a successful-but-empty response and `null` only for a parse failure; `TryFetchAuthenticatedFriendScores` returns a non-null (possibly empty) list whenever an authenticated request succeeded.

- [ ] **Step 1: Add `Info` to `FeedResult`** in `pcvr/Models/FeedEntry.cs` (after the `Error` property):

```csharp
        // Set only on Success with zero entries: a user-facing message such
        // as "no recent scores from the players you follow" that is NOT an
        // error and must not trigger fallback paths.
        public string Info { get; set; } = "";
```

- [ ] **Step 2: Make empty-success distinct in `ParseFriendScores`** (lines 256-274). Replace the method:

```csharp
        private static List<FeedEntry> ParseFriendScores(string body)
        {
            try
            {
                var page = JsonConvert.DeserializeObject<ScorePageDto>(body);
                if (page?.data == null) return null;

                var unknown = new PlayerDto { name = "?" };
                // An empty list is a real, successful answer (the user follows
                // nobody, or nobody scored recently) — never fold it into null,
                // which callers read as "no usable login".
                return page.data.Where(x => x != null).Select(x => ParseScore(x, unknown)).ToList();
            }
            catch (Exception ex)
            {
                // A parse failure after a successful request must be loud —
                // it means the feed WAS fetched and then thrown away.
                Plugin.Log?.Warn("Couldn't parse the friends feed response: " + ex.Message);
                return null;
            }
        }
```

- [ ] **Step 3: Accept the empty list in `FetchFeedAsync`** (lines 37-43). Replace:

```csharp
                progress?.Invoke("Loading your BeatLeader friends feed...");
                var (friendEntries, rateLimited) = await TryFetchAuthenticatedFriendScores(feedCount, progress);
                if (friendEntries != null)
                {
                    result.Entries = friendEntries.OrderByDescending(x => x.Timepost).Take(feedCount).ToList();
                    result.Success = true;
                    if (result.Entries.Count == 0)
                        result.Info = "No recent scores from the players you follow yet.\nFollow players on beatleader.com, then press Refresh.";
                    return result;
                }
```

- [ ] **Step 4: Session-scoped login-wait memo** (P-I2). Add a static field next to `LoginWaitMilliseconds` (line 126):

```csharp
        // Remembers that a full login wait already timed out once this
        // session, so a BeatLeader install with no signed-in account doesn't
        // stall every refresh 8 seconds for the rest of the session.
        private static bool _loginWaitExhausted;
```

Replace the wait block in `TryFetchAuthenticatedFriendScores` (lines 151-155):

```csharp
            if (!IsBeatLeaderSignedIn(beatLeader) && TryGetBeatLeaderSession(beatLeader) == null)
            {
                if (_loginWaitExhausted)
                {
                    Plugin.Log?.Debug("Skipping the BeatLeader login wait (it already timed out this session).");
                }
                else
                {
                    progress?.Invoke("Waiting for the BeatLeader mod to sign in...");
                    await WaitForBeatLeaderLogin(beatLeader);
                    if (!IsBeatLeaderSignedIn(beatLeader) && TryGetBeatLeaderSession(beatLeader) == null)
                        _loginWaitExhausted = true;
                }
            }
```

- [ ] **Step 5: Contain the cookie path** (P-I3). Wrap the entire body of `FetchFriendScoresWithCookies` (lines 198-224) in try/catch — the method's existing content becomes the `try` block, followed by:

```csharp
            catch (Exception ex)
            {
                // new Uri / Cookie.Add / GetAsync can all throw; a failure
                // here must fall through to the Unity path and the public
                // fallback, not abort the whole fetch.
                Plugin.Log?.Info("friendScores via the BeatLeader session cookies failed: " + ex.Message);
                return (null, 0);
            }
```

- [ ] **Step 6: Observe the losing login task** (P-M8). In `WaitForBeatLeaderLogin`, replace the `WhenAny` block (lines 318-321):

```csharp
                if (waitLogin != null && waitLogin.Invoke(null, null) is Task loginTask)
                {
                    // Observe the login task's eventual fault so a failed
                    // login can't surface later as UnobservedTaskException.
                    _ = loginTask.ContinueWith(t => _ = t.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                    await Task.WhenAny(loginTask, Task.Delay(LoginWaitMilliseconds));
                    return;
                }
```

- [ ] **Step 7: Scale the per-player pull to the stepper** (P-M7). In `FetchFeedAsync`, right after the `following == null || following.Count == 0` check returns (i.e. `following` is known non-empty), before the `SemaphoreSlim` line:

```csharp
                // The Scores stepper promises up to feedCount results; with a
                // short following list the configured per-player count could
                // never reach it. Pull enough per player, within API limits.
                var neededPerPlayer = (int)Math.Ceiling((double)feedCount / following.Count);
                scoresPerPlayer = Math.Max(scoresPerPlayer, Math.Min(neededPerPlayer, 20));
```

- [ ] **Step 8: Verify** — same rule as Task 2 Step 5 (build if refs, else careful re-read: confirm every return path of `TryFetchAuthenticatedFriendScores` still typechecks as `(List<FeedEntry>, bool)` and no path returns a non-null list for an unauthenticated failure).

- [ ] **Step 9: Commit**

```bash
git add pcvr/Services/BeatLeaderService.cs pcvr/Models/FeedEntry.cs
git commit -m "fix(pc): distinguish empty feed from missing login, stop repeated 8s login stalls, contain cookie-path failures (P-I1, P-I2, P-I3)"
```

---

### Task 4: PC — SnipeFeedView: keep stale feed, guard install completions, harden launch (P-I4, P-I7, P-I8, P-M5, P-M6, P-M8-msg)

**Files:**
- Modify: `pcvr/UI/SnipeFeedView.cs`

**Interfaces:**
- Consumes: `FeedResult.Info` (Task 3), unchanged `SongInstaller` signatures (Task 2), `Formatting.EscapeForTmp(string) : string` (Task 5 — see note in Step 3; this task uses raw `ex.Message`/`install.Error` text, Task 5 sweeps escaping over these sites).

- [ ] **Step 1: Failed refresh keeps the good feed** (P-I4). In `Refresh()` (lines 240-261), replace from `_entries.Clear();` through `HideModal(false);`:

```csharp
                if (!result.Success)
                {
                    // Keep the previous feed on a failed refresh — replacing
                    // good rows with an empty list turns a network blip into
                    // a broken-looking tab.
                    if (_entries.Count > 0)
                    {
                        SetStatus(result.Error + "\n(Showing the previous scores.)");
                    }
                    else
                    {
                        UpdateFilters();
                        RebuildList();
                        SetStatus(result.Error);
                    }
                    return;
                }

                _entries.Clear();
                _selected = null;
                _entries.AddRange(result.Entries);
                _lastFetch = DateTimeOffset.UtcNow;

                // A successful refresh means the feed (and its images) are
                // current again — let previously-failed images retry.
                SpriteCache.ClearFailures();

                UpdateFilters();
                RebuildList();
                HideModal(false);
                if (_entries.Count == 0 && !string.IsNullOrEmpty(result.Info))
                    SetStatus(result.Info);
```

- [ ] **Step 2: Success-empty must not re-refresh every tab show.** In `OnTabShown` (line 180), replace the stale check:

```csharp
            var stale = DateTimeOffset.UtcNow - _lastFetch > CacheLifetime;
```

(`_lastFetch` starts at `DateTimeOffset.MinValue`, so a never-fetched session is still stale; an empty-but-successful fetch now sets `_lastFetch` and gets the same 2-minute cache as a full one.)

- [ ] **Step 3: Guard install completions against a changed selection** (P-I7) and remove the outside-try launch (P-I8). Replace `PlaySelected` (lines 390-445) entirely:

```csharp
        [UIAction("play-selected")]
        private async void PlaySelected()
        {
            if (_installing || _selected == null || string.IsNullOrWhiteSpace(_selected.SongHash)) return;
            var entry = _selected;

            try
            {
                var level = SongInstaller.GetInstalledLevel(entry.SongHash);
                if (level != null)
                {
                    LaunchLevel(level);
                    return;
                }

                _installing = true;
                SetPlayButton("Downloading...", false);

                var install = await SongInstaller.DownloadAndInstallAsync(entry.SongHash);
                if (!install.Success)
                {
                    ShowInstallOutcome(entry, "<color=#ff5555>" + install.Error + "</color>");
                    return;
                }

                level = install.Level ?? SongInstaller.GetInstalledLevel(entry.SongHash);
                if (level == null)
                {
                    ShowInstallOutcome(entry, "Downloaded! The song is still loading — it will appear in Custom Levels shortly.");
                    return;
                }

                // Only auto-launch if the user is still on this tab AND this
                // score is still the selected one — launching score A while
                // score B's modal is open (or from another screen) would yank
                // them somewhere they didn't ask to go.
                if (_rootObject != null && _rootObject.activeInHierarchy && ReferenceEquals(_selected, entry))
                    LaunchLevel(level);
                else
                    SetStatus("Downloaded — press Play when you're back.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Download & play failed: " + ex);
                ShowInstallOutcome(entry, "<color=#ff5555>Map install failed: " + ex.Message + "</color>");
            }
            finally
            {
                _installing = false;
                // Recompute the real button state for whatever score is
                // selected NOW instead of hardcoding a label that may belong
                // to a different score.
                if (_selected != null)
                    UpdatePlayButton();
            }
        }

        // Puts an install outcome where the user is actually looking: the
        // modal if this score is still the open one, else the status line.
        private void ShowInstallOutcome(FeedEntry entry, string message)
        {
            if (ReferenceEquals(_selected, entry))
                SetDetail(message);
            else
                SetStatus(message);
        }
```

- [ ] **Step 4: Contain launch-path exceptions** (P-I8). In `LaunchLevel`, wrap the solo branch's work (currently lines 464-481) and the dismiss callback:

```csharp
            if (IsSoloFlowOnTop(out var youngest))
            {
                var parent = youngest._parentFlowCoordinator;
                if (parent != null)
                {
                    try
                    {
                        if (!SongInstaller.PrimeSoloFlow(level))
                        {
                            // The modal is already hidden — leave SOME feedback
                            // instead of silently doing nothing.
                            SetStatus("<color=#ff5555>Couldn't open the song — pick it in Custom Levels.</color>");
                            return;
                        }
                        Plugin.Log?.Info("LaunchLevel: re-entering solo with " + level.songName);
                        parent.DismissFlowCoordinator(
                            youngest,
                            ViewController.AnimationDirection.Horizontal,
                            (Action)(() =>
                            {
                                try { SongInstaller.PressSoloButton(); }
                                catch (Exception ex) { Plugin.Log?.Error("PressSoloButton failed: " + ex); }
                            }),
                            false);
                    }
                    catch (Exception ex)
                    {
                        // An HMUI transition already in progress can throw; a
                        // half-dismissed flow is exactly the menu corruption
                        // this mod promises never to cause.
                        Plugin.Log?.Error("Solo re-entry failed: " + ex);
                        SetStatus("<color=#ff5555>Couldn't open the song — pick it in Custom Levels.</color>");
                    }
                    return;
                }
            }
```

- [ ] **Step 5: Status message on the picker branch** (P-M5). In `LaunchLevel`'s picker branch, after the successful `picker.SelectLevel(level);` add `SetStatus("Selected in the song list.");` and in its catch add `SetStatus("Couldn't select the song here — open it from Custom Levels.");` after the existing `Plugin.Log?.Warn(...)`.

- [ ] **Step 6: Reparent the modal unconditionally** (P-M6). Replace `HideModal` (lines 523-532):

```csharp
        private void HideModal(bool animated)
        {
            if (_detailModal == null) return;
            _detailModal.Hide(animated, null);
            // Reparent back inline: Show(_, moveToCenter: true) moved the
            // modal under the shared center container, and HMUI does not
            // reliably invoke a hide callback for a modal that a click-off
            // already hid — relying on the callback leaks the modal into the
            // shared container across menu rebuilds. Every caller passes
            // animated=false, so an immediate reparent cannot fight an
            // animation.
            if (_modalOriginalParent != null && _detailModal.transform.parent != _modalOriginalParent)
                _detailModal.transform.SetParent(_modalOriginalParent, true);
        }
```

- [ ] **Step 7: Verify** — build if refs available; else re-read `PlaySelected` confirming: `_installing` is true during the await and false before `UpdatePlayButton()` in finally; every completion path goes through `ShowInstallOutcome` or the launch gate; the early already-installed branch is inside the try.

- [ ] **Step 8: Commit**

```bash
git add pcvr/UI/SnipeFeedView.cs
git commit -m "fix(pc): keep stale feed on failed refresh, target install outcomes at the right score, contain launch exceptions (P-I4, P-I7, P-I8)"
```

---

### Task 5: PC — SpriteCache eviction, TMP-correct escaping (+checks), startup logging (P-I6, P-M2, P-M1)

**Files:**
- Modify: `pcvr/Services/SpriteCache.cs`
- Modify: `pcvr/Utils/Formatting.cs`
- Modify: `pcvr/UI/SnipeFeedView.cs` (two escape call sites)
- Modify: `pcvr/Plugin.cs`
- Create: `pcvr/Tests/FormattingChecks/FormattingChecks.csproj`
- Create: `pcvr/Tests/FormattingChecks/Program.cs`

**Interfaces:**
- Produces: `Formatting.EscapeForTmp(string) : string` (public within assembly; replaces private `Escape`). Consumed by `SnipeFeedView` for exception/error text.

- [ ] **Step 1: Write the failing check for TMP escaping.** Create `pcvr/Tests/FormattingChecks/FormattingChecks.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <LangVersion>8</LangVersion>
    <Nullable>disable</Nullable>
    <!-- Compiles the shipped sources directly so internals are same-assembly.
         Zero NuGet dependencies: plain asserts, exit code = pass/fail. -->
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="..\..\Utils\Formatting.cs" Link="Formatting.cs" />
    <Compile Include="..\..\Models\FeedEntry.cs" Link="FeedEntry.cs" />
  </ItemGroup>
</Project>
```

Note: `Formatting.cs` must stay free of Unity/game references for this to compile (it currently is — only `System` + `SnipeFeed.PC.Models`). It references `Plugin.Log` nowhere. Create `pcvr/Tests/FormattingChecks/Program.cs`:

```csharp
using System;
using SnipeFeed.PC.Models;
using SnipeFeed.PC.Utils;

internal static class Program
{
    private static int _failures;

    private static void Check(string name, bool condition)
    {
        Console.WriteLine((condition ? "PASS " : "FAIL ") + name);
        if (!condition) _failures++;
    }

    private static int Main()
    {
        // TMP renders entities literally, so escaping must NOT produce them.
        Check("no &amp; entities", !Formatting.EscapeForTmp("Rock & Roll").Contains("&amp;"));
        Check("ampersand kept verbatim", Formatting.EscapeForTmp("Rock & Roll") == "Rock & Roll");
        // '<' must survive visibly but never open a TMP tag: a zero-width
        // space directly after every '<'.
        Check("tag neutralized", Formatting.EscapeForTmp("<b>bold</b>") == "<\u200Bb>bold<\u200B/b>");
        Check("heart name kept", Formatting.EscapeForTmp("<3 song") == "<\u200B3 song");
        Check("null tolerated", Formatting.EscapeForTmp(null) == "");

        // TitleLine must route its user strings through the escaper.
        var entry = new FeedEntry { SongName = "<3", SongAuthor = "A<B", Mapper = "M" };
        var title = Formatting.TitleLine(entry);
        Check("TitleLine escapes songName", title.StartsWith("<\u200B3"));
        Check("TitleLine escapes author", title.Contains("A<\u200BB"));

        // TimeAgo boundaries.
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Check("just now", Formatting.TimeAgo(now - 30) == "just now");
        Check("minutes", Formatting.TimeAgo(now - 120) == "2m ago");
        Check("future clamps", Formatting.TimeAgo(now + 999) == "just now");
        Check("zero is empty", Formatting.TimeAgo(0) == "");

        Console.WriteLine(_failures == 0 ? "ALL PASS" : _failures + " FAILURES");
        return _failures == 0 ? 0 : 1;
    }
}
```

- [ ] **Step 2: Run it — expect FAIL.** `dotnet run --project pcvr/Tests/FormattingChecks` must fail to compile (no `EscapeForTmp` yet) — that is the red state.

- [ ] **Step 3: Implement the escaping change** in `pcvr/Utils/Formatting.cs`. Replace the private `Escape` method (line 115) with:

```csharp
        // TMP does not decode HTML entities — "&amp;" renders literally — so
        // entity escaping is wrong here. Instead neutralize markup: a
        // zero-width space directly after every '<' keeps the character
        // visible while making it impossible to open a tag.
        public static string EscapeForTmp(string text) => (text ?? "").Replace("<", "<\u200B");
```

Then replace every `Escape(` call inside Formatting.cs with `EscapeForTmp(` (sites: TitleLine ×3, PlayerLine ×1, StatsLine ×1, DetailText ×3).

- [ ] **Step 4: Run the checks — expect ALL PASS**, `dotnet run --project pcvr/Tests/FormattingChecks` exit code 0.

- [ ] **Step 5: Escape error text at the view's two raw-message sites** in `pcvr/UI/SnipeFeedView.cs` (as left by Task 4): in `PlaySelected`'s failure branch use `Formatting.EscapeForTmp(install.Error)`, in its catch use `Formatting.EscapeForTmp(ex.Message)`, and in `Refresh`'s catch use `SetStatus("Couldn't refresh Snipe Feed: " + Formatting.EscapeForTmp(ex.Message));`.

- [ ] **Step 6: Bound the sprite cache** (P-I6). In `pcvr/Services/SpriteCache.cs`: add fields next to `Loaded`:

```csharp
        private const int MaxCachedSprites = 256;
        // Oldest-first access order; a hit moves the URL to the back. Sized
        // above one full 100-score feed's covers+avatars so eviction only
        // bites across MANY refreshes, never inside the current view.
        private static readonly List<string> LoadOrder = new List<string>();
```

In `GetSprite`, replace the cache-hit block:

```csharp
            if (Loaded.TryGetValue(url, out var sprite) && sprite != null)
            {
                LoadOrder.Remove(url);
                LoadOrder.Add(url);
                onSprite(sprite);
                return;
            }
```

In `DownloadAsync`, replace `Loaded[url] = sprite;` with:

```csharp
            Loaded[url] = sprite;
            LoadOrder.Add(url);
            EvictIfNeeded();
```

Add the eviction method:

```csharp
        private static void EvictIfNeeded()
        {
            while (LoadOrder.Count > MaxCachedSprites)
            {
                var oldest = LoadOrder[0];
                LoadOrder.RemoveAt(0);
                if (!Loaded.TryGetValue(oldest, out var sprite)) continue;
                Loaded.Remove(oldest);
                if (sprite != null)
                {
                    // The sprite is a thin wrapper; the texture holds the
                    // memory. Cells re-request evicted URLs on their next
                    // bind, so a stale reference at worst re-downloads.
                    if (sprite.texture != null) UnityEngine.Object.Destroy(sprite.texture);
                    UnityEngine.Object.Destroy(sprite);
                }
            }
        }
```

- [ ] **Step 7: Startup visibility** (P-M1). In `pcvr/Plugin.cs`: add `using System;`, wrap `OnStart`'s body and `RegisterGameplayTab`'s body:

```csharp
        [OnStart]
        public async Task OnStart()
        {
            try
            {
                await MainMenuAwaiter.WaitForMainMenuAsync();
                RegisterGameplayTab();
                MainMenuAwaiter.MainMenuInitializing += RegisterGameplayTab;
            }
            catch (Exception ex)
            {
                // BSIPA does not observe this task; without the catch a
                // startup failure is a silently missing tab with no log line.
                Log?.Error("SnipeFeed failed to start: " + ex);
            }
        }
```

```csharp
        private static void RegisterGameplayTab()
        {
            try
            {
                var setup = GameplaySetup.Instance;
                if (setup == null)
                {
                    Log?.Warn("GameplaySetup.Instance is null; the Snipe Feed tab was not added this menu load.");
                    return;
                }
                if (ReferenceEquals(setup, _registeredGameplaySetup)) return;

                setup.AddTab(
                    "Snipe Feed",
                    "SnipeFeed.PC.UI.SnipeFeedView.bsml",
                    View,
                    MenuType.All);

                _registeredGameplaySetup = setup;
                Log?.Info("Registered Snipe Feed gameplay setup tab.");
            }
            catch (Exception ex)
            {
                // Thrown from BSML's MainMenuInitializing multicast this
                // would abort every later subscriber (other mods included).
                Log?.Error("Registering the Snipe Feed tab failed: " + ex);
            }
        }
```

- [ ] **Step 8: Verify** — `dotnet run --project pcvr/Tests/FormattingChecks` → ALL PASS; plus the plugin build if refs available.

- [ ] **Step 9: Commit**

```bash
git add pcvr/Services/SpriteCache.cs pcvr/Utils/Formatting.cs pcvr/UI/SnipeFeedView.cs pcvr/Plugin.cs pcvr/Tests
git commit -m "fix(pc): bound sprite cache with texture destruction, TMP-correct escaping with checks, loud startup failures (P-I6, P-M2, P-M1)"
```

---

### Task 6: Quest — curl global init, NOSIGNAL, gzip, TLS verification (Q-I1, Q-I5, Q-M8)

**Files:**
- Modify: `src/Web.cpp`
- Modify: `src/main.cpp`

**Interfaces:**
- Produces: `Web::Get` unchanged signature, now TLS-verified. Every later Quest task's network behavior depends on this.

- [ ] **Step 1: Global init in `late_load`** (Q-I5). In `src/main.cpp`, add `#include "libcurl/shared/curl.h"` to the includes, and as the first line of `late_load()`:

```cpp
    // libcurl's lazy global init is not thread-safe, and this mod runs up
    // to 4 feed workers plus image threads concurrently — init exactly once
    // before any of them can race it.
    curl_global_init(CURL_GLOBAL_ALL);
```

- [ ] **Step 2: Harden the easy handle** (Q-I5, Q-I1, Q-M8). In `src/Web.cpp`, replace the option block from `curl_easy_setopt(curl, CURLOPT_USERAGENT, ...)` through the `CURLOPT_SSL_VERIFYPEER, 0L` line (lines 39-43) with:

```cpp
        curl_easy_setopt(curl, CURLOPT_USERAGENT, userAgent.c_str());
        // Threaded use: signal-based timeouts would deliver SIGALRM to a
        // random thread.
        curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
        // Advertise every codec this build can decode; BeatLeader's 100-score
        // JSON pages compress ~10x.
        curl_easy_setopt(curl, CURLOPT_ACCEPT_ENCODING, "");
        // Verify TLS peers against Android's system CA store — a c_rehash
        // style directory of PEM files, which CAPATH understands. This mod
        // sends the BeatLeader login cookie; without verification anyone who
        // can spoof DNS on the local network can read it.
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 1L);
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 2L);
        curl_easy_setopt(curl, CURLOPT_CAPATH, "/system/etc/security/cacerts");
```

- [ ] **Step 3: Diagnosable TLS failures.** In the `res != CURLE_OK` branch of `Web::Get`, after the existing error log, add:

```cpp
            if (res == CURLE_PEER_FAILED_VERIFICATION || res == CURLE_SSL_CACERT_BADFILE)
                SnipeFeedLogger.error("TLS verification failed — the system CA path may be unusable with this libcurl build; see docs/superpowers/plans/2026-08-25-reliability-fixes.md Task 6");

```

- [ ] **Step 4: Verify.** If `QuestBuild: yes`: `qpm s build` → compiles. **Device smoke test required before release** (not just compile): feed must load with verification on. If the qpm libcurl build's TLS backend cannot read CAPATH directories, every request will fail with curl error 60 — the fallback design is then: ship curl's `cacert.pem` in the qmod via `fileCopies` (mod.template.json) to the mod's data dir and point `CURLOPT_CAINFO` at `getDataDir(modInfo) + "cacert.pem"`. Do NOT silently revert to `VERIFYPEER 0`. Record the smoke-test status in the final task.

- [ ] **Step 5: Commit**

```bash
git add src/Web.cpp src/main.cpp
git commit -m "fix(quest): curl global init + NOSIGNAL + gzip, verify TLS against the system CA store (Q-I1, Q-I5, Q-M8)"
```

---

### Task 7: Quest — Feed.cpp: three-state friend feed, crash-proof workers, input/message polish (Q-I4, Q-M3, Q-M4, Q-M5)

**Files:**
- Modify: `src/Feed.cpp`
- Modify: `include/Feed.hpp` (one comment)

**Interfaces:**
- Produces: `FeedResult` unchanged in shape; NEW CONTRACT documented in Feed.hpp: on `success == true` with zero entries, `error` carries a user-facing info message (not an error). Task 10 (FeedView) displays it.

- [ ] **Step 1: Document the contract** in `include/Feed.hpp` — extend the comment above `struct FeedResult`:

```cpp
    // On success with ZERO entries, `error` carries a user-facing info
    // message ("you don't follow anyone yet") — it is not a failure and
    // callers must not treat it as one.
```

- [ ] **Step 2: Three-state friend fetch** (Q-I4). Replace `TryFetchFriendScores` (lines 125-146):

```cpp
    enum class FriendFeedOutcome {
        Loaded,        // entries parsed
        Empty,         // authenticated 200 with no scores — a real answer
        NetworkError,  // transport failed; the public path would fail too
        Unavailable,   // HTTP error / bad body — expired login etc.; fall back
    };

    // Preferred path: the same feed the BeatLeader website home page shows,
    // in a single request. Requires the BeatLeader mod login cookie.
    static FriendFeedOutcome TryFetchFriendScores(std::string const& cookieFile, int count, std::vector<FeedEntry>& outEntries) {
        std::string url = std::string(API_URL) + "/user/friendScores?sortBy=date&order=desc&page=1&count=" + std::to_string(count);
        std::string body;
        long code = Web::Get(url, TIMEOUT_SECONDS, body, cookieFile);
        if (code < 0) {
            SnipeFeedLogger.warn("friendScores transport error ({})", code);
            return FriendFeedOutcome::NetworkError;
        }
        if (code != 200) {
            SnipeFeedLogger.info("friendScores unavailable (HTTP {}), falling back to public API", code);
            return FriendFeedOutcome::Unavailable;
        }

        rapidjson::Document doc;
        doc.Parse(body);
        if (doc.HasParseError() || !doc.IsObject()) return FriendFeedOutcome::Unavailable;
        auto data = doc.FindMember("data");
        if (data == doc.MemberEnd() || !data->value.IsArray()) return FriendFeedOutcome::Unavailable;

        FollowedPlayer unknown{"", "?", ""};
        for (auto const& score : data->value.GetArray()) {
            if (score.IsObject())
                ParseScore(score, unknown, outEntries);
        }
        // An empty page is a real, successful answer (no follows, or no
        // recent scores) — folding it into failure sends a logged-in user
        // down the public path and tells them their login is broken.
        return outEntries.empty() ? FriendFeedOutcome::Empty : FriendFeedOutcome::Loaded;
    }
```

- [ ] **Step 3: Route the outcomes** in `FetchFeedAsync`'s cookie block (lines 263-276). Replace it:

```cpp
            // Path 1: reuse the BeatLeader mod login for the real friends feed.
            std::string cookieFile = BeatLeaderCookieFile();
            std::error_code fsError;
            if (std::filesystem::exists(cookieFile, fsError)) {
                if (onProgress) onProgress("Loading your BeatLeader friends feed...");
                switch (TryFetchFriendScores(cookieFile, feedCount, result.entries)) {
                    case FriendFeedOutcome::Loaded:
                        std::sort(result.entries.begin(), result.entries.end(), [](FeedEntry const& a, FeedEntry const& b) {
                            return a.timepost > b.timepost;
                        });
                        result.success = true;
                        onDone(std::move(result));
                        return;
                    case FriendFeedOutcome::Empty:
                        result.success = true;
                        result.error = "No recent scores from the players you follow yet.\nFollow players on beatleader.com, then press Refresh.";
                        onDone(std::move(result));
                        return;
                    case FriendFeedOutcome::NetworkError:
                        // The public path rides the same network — falling
                        // through would waste 15s and blame the login.
                        result.error = "Network error. Check your connection.";
                        onDone(std::move(result));
                        return;
                    case FriendFeedOutcome::Unavailable:
                        result.entries.clear();
                        break;
                }
            }
```

- [ ] **Step 4: Crash-proof the workers** (Q-M3). Wrap the outer worker's whole body: first line inside the `std::thread worker([...] {` lambda becomes `try {`, and just before the lambda's closing brace add:

```cpp
            } catch (std::exception const& e) {
                // An exception escaping a detached thread is std::terminate —
                // a whole-game crash. Turn it into a feed error instead.
                SnipeFeedLogger.error("Feed fetch crashed: {}", e.what());
                FeedResult crashResult;
                crashResult.error = "Something went wrong loading the feed. Press Refresh to try again.";
                onDone(std::move(crashResult));
            }
```

(Every existing `onDone` call is immediately followed by `return`, so a throw implies `onDone` has not fired — no double-call.) Also wrap the sub-worker's fetch (inside the `workers.emplace_back([&] { ... })` loop):

```cpp
                        std::vector<FeedEntry> local;
                        try {
                            FetchRecentScores(following[i], scoresPerPlayer, local);
                        } catch (std::exception const& e) {
                            SnipeFeedLogger.warn("Score fetch for {} crashed: {}", following[i].id, e.what());
                        }
```

- [ ] **Step 5: SanitizeInput strips queries/fragments** (Q-M4). In `SanitizeInput` (lines 100-110), after the whitespace trim and before the trailing-slash loop:

```cpp
        // A pasted profile URL can carry ?tab=... or #fragment — those are
        // never part of the id/alias.
        auto cut = input.find_first_of("?#");
        if (cut != std::string::npos)
            input = input.substr(0, cut);
```

- [ ] **Step 6: Human-readable transport errors** (Q-M5). In `FetchFollowing` the `code < 0` branch already exists. `FetchRecentScores` only logs — fine. No other Feed.cpp site prints "HTTP <negative>"; the remaining ones are in SongInstaller (Task 8).

- [ ] **Step 7: Verify** — `qpm s build` if available; else re-read: every switch case returns or breaks, `result.entries` cleared before the public path, try/catch braces balance.

- [ ] **Step 8: Commit**

```bash
git add src/Feed.cpp include/Feed.hpp
git commit -m "fix(quest): three-state friends feed, crash-proof worker threads, URL input cleanup (Q-I4, Q-M3, Q-M4)"
```

---

### Task 8: Quest — SongInstaller: validated hash, symlink-free extraction, atomic install (Q-I2, Q-I3, Q-M5, Q-M6)

**Files:**
- Modify: `src/SongInstaller.cpp`

**Interfaces:**
- Consumes: vendored zip API (`zip_stream_open/zip_entries_total/zip_entry_openbyindex/zip_entry_name/zip_entry_isdir/zip_entry_size/zip_entry_fread/zip_entry_close/zip_close` — all in `include/zip/src/zip.h`). `zip_entry_fread` extracts via `mz_zip_reader_extract_to_file` and never creates symlinks (verified: symlink creation exists only in `zip_archive_extract`, zip.c:346-366, which this task stops using).
- Produces: `Installer::DownloadAndInstallAsync` unchanged signature.

- [ ] **Step 1: Add `#include <filesystem>`** to SongInstaller.cpp's includes.

- [ ] **Step 2: Add validation + safe extraction helpers** (above `DownloadAndInstallAsync`):

```cpp
    // The hash arrives from feed JSON; it becomes a URL segment and an
    // install folder name, so anything but exactly 40 hex chars is refused.
    static bool IsValidHash(std::string const& hash) {
        if (hash.size() != 40) return false;
        return std::all_of(hash.begin(), hash.end(), [](unsigned char c) {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        });
    }

    // Per-entry extraction instead of zip_stream_extract: the bulk helper
    // recreates symlink entries with unrestricted targets, letting a hostile
    // archive write through them anywhere on disk. Writing each regular file
    // ourselves (zip_entry_fread) closes that door, and lets us refuse
    // escape-y names and cap the decompressed size.
    static bool ExtractArchiveSafely(std::string const& zipData, std::string const& targetDir, std::string& outError) {
        constexpr unsigned long long MAX_TOTAL_BYTES = 512ull * 1024 * 1024;

        zip_t* archive = zip_stream_open(zipData.data(), zipData.size(), 0, 'r');
        if (!archive) {
            outError = "The map archive could not be read.";
            return false;
        }
        int total = zip_entries_total(archive);
        if (total <= 0) {
            zip_close(archive);
            outError = "The map archive is empty.";
            return false;
        }

        unsigned long long extractedBytes = 0;
        for (int i = 0; i < total; i++) {
            if (zip_entry_openbyindex(archive, i) != 0) {
                zip_close(archive);
                outError = "The map archive could not be read.";
                return false;
            }
            char const* rawName = zip_entry_name(archive);
            std::string name = rawName ? rawName : "";
            bool isDir = zip_entry_isdir(archive) == 1;
            extractedBytes += zip_entry_size(archive);

            // Stricter than strictly necessary ("song..egg" is also refused)
            // — real beatmap zips contain plain relative names only.
            bool badName = name.empty()
                || name.front() == '/'
                || name.find('\\') != std::string::npos
                || name.find("..") != std::string::npos
                || name.find(':') != std::string::npos;
            if (badName || extractedBytes > MAX_TOTAL_BYTES) {
                zip_entry_close(archive);
                zip_close(archive);
                outError = badName ? "The map archive contains an unsafe file name."
                                   : "The map archive is unreasonably large.";
                return false;
            }

            std::string destination = targetDir + "/" + name;
            std::error_code fsError;
            if (isDir) {
                std::filesystem::create_directories(destination, fsError);
            } else {
                auto parent = std::filesystem::path(destination).parent_path();
                if (!parent.empty()) std::filesystem::create_directories(parent, fsError);
                if (zip_entry_fread(archive, destination.c_str()) != 0) {
                    zip_entry_close(archive);
                    zip_close(archive);
                    outError = "Failed to extract the map archive.";
                    return false;
                }
            }
            zip_entry_close(archive);
        }
        zip_close(archive);
        return true;
    }
```

- [ ] **Step 3: Rewrite `DownloadAndInstallAsync`'s worker body** (lines 42-104) — validated hash, network-vs-HTTP messages, temp-dir + rename, cleanup, top-level try/catch:

```cpp
    void DownloadAndInstallAsync(std::string hash, std::function<void(bool, std::string)> onDone) {
        std::thread worker([hash = ToLower(std::move(hash)), onDone = std::move(onDone)] {
            try {
                if (!IsValidHash(hash)) {
                    onDone(false, "This score has an invalid map hash.");
                    return;
                }

                // 1) Look the map up on BeatSaver.
                std::string meta;
                long code = Web::Get("https://api.beatsaver.com/maps/hash/" + hash, META_TIMEOUT, meta);
                if (code < 0) {
                    onDone(false, "Network error. Check your connection.");
                    return;
                }
                if (code == 404) {
                    onDone(false, "Map not found on BeatSaver.");
                    return;
                }
                if (code != 200) {
                    onDone(false, "BeatSaver lookup failed (HTTP " + std::to_string(code) + ").");
                    return;
                }

                rapidjson::Document doc;
                doc.Parse(meta);
                if (doc.HasParseError() || !doc.IsObject() || !doc.HasMember("versions") || !doc["versions"].IsArray() || doc["versions"].GetArray().Empty()) {
                    onDone(false, "Unexpected BeatSaver response.");
                    return;
                }

                // Only the version matching the score's hash: a different
                // version would never match the post-install lookup and
                // silently changes the map under the score.
                std::string downloadUrl;
                for (auto const& version : doc["versions"].GetArray()) {
                    if (!version.IsObject() || !version.HasMember("downloadURL") || !version["downloadURL"].IsString())
                        continue;
                    if (version.HasMember("hash") && version["hash"].IsString() && ToLower(version["hash"].GetString()) == hash) {
                        downloadUrl = version["downloadURL"].GetString();
                        break;
                    }
                }
                if (downloadUrl.empty()) {
                    onDone(false, "This score's map version is no longer available on BeatSaver.");
                    return;
                }

                // 2) Download the zip (binary-safe std::string buffer).
                std::string zipData;
                code = Web::Get(downloadUrl, DOWNLOAD_TIMEOUT, zipData);
                if (code < 0) {
                    onDone(false, "Network error while downloading the map.");
                    return;
                }
                if (code != 200 || zipData.empty()) {
                    onDone(false, "Download failed (HTTP " + std::to_string(code) + ").");
                    return;
                }

                // 3) Extract into a temp sibling and rename into place, so a
                //    failure never leaves a half-written folder for SongCore
                //    to choke on, and a retry never merges with stale files.
                auto levelsRoot = std::string(SongCore::API::Loading::GetPreferredCustomLevelPath());
                auto targetFolder = levelsRoot + "/" + hash;
                auto tempFolder = levelsRoot + "/.snipefeed_tmp_" + hash;

                std::error_code fsError;
                std::filesystem::remove_all(tempFolder, fsError);
                std::filesystem::create_directories(tempFolder, fsError);
                if (fsError) {
                    onDone(false, "Couldn't create the install folder.");
                    return;
                }

                std::string extractError;
                if (!ExtractArchiveSafely(zipData, tempFolder, extractError)) {
                    std::filesystem::remove_all(tempFolder, fsError);
                    onDone(false, extractError);
                    return;
                }

                std::filesystem::remove_all(targetFolder, fsError);
                fsError.clear();
                std::filesystem::rename(tempFolder, targetFolder, fsError);
                if (fsError) {
                    std::filesystem::remove_all(tempFolder, fsError);
                    onDone(false, "Couldn't move the map into Custom Levels.");
                    return;
                }

                SnipeFeedLogger.info("Installed map {} to {}", hash, targetFolder);
                onDone(true, "");
            } catch (std::exception const& e) {
                // An exception escaping a detached thread is std::terminate.
                SnipeFeedLogger.error("Map install crashed: {}", e.what());
                onDone(false, "Something went wrong installing the map.");
            }
        });
        worker.detach();
    }
```

(This also removes the old `int args = 2; ... &args` oddity — Q-M6 — because `zip_stream_extract` is no longer called.)

- [ ] **Step 4: Verify** — `qpm s build` if available. Else re-read: every early return before `onDone(true, ...)` calls `onDone(false, ...)` exactly once; `zip_entry_close` pairs with every successful `zip_entry_openbyindex`; `zip_close` on every path out of `ExtractArchiveSafely`.

- [ ] **Step 5: Commit**

```bash
git add src/SongInstaller.cpp
git commit -m "fix(quest): validate map hash, extract without symlinks, atomic temp-dir install with cleanup (Q-I2, Q-I3, Q-M6)"
```

---

### Task 9: Quest — GC-safe lifetime for every async completion (Q-C1)

**Files:**
- Modify: `include/FeedView.hpp`
- Modify: `src/FeedView.cpp`
- Modify: `src/FeedCell.cpp`

**Interfaces:**
- Produces: file-static `SnipeFeed::FeedView* ActiveViewAlive()` in FeedView.cpp's anonymous namespace (main-thread only); `FeedView::OnDestroy()`. Task 10 modifies the same functions afterward — Task 10's diffs are written against THIS task's output.
- Rule established here, used by all later work: **worker-transiting callbacks capture only plain C++ data**; main→main deferrals may capture `SafePtrUnity`.

- [ ] **Step 1: Declare `OnDestroy`** in `include/FeedView.hpp`, next to `DECLARE_INSTANCE_METHOD(void, Refresh);`:

```cpp
    DECLARE_INSTANCE_METHOD(void, OnDestroy);
```

- [ ] **Step 2: Root the active view.** In `src/FeedView.cpp`'s `FeedState` (line 63), replace the `activeView` member and its comment:

```cpp
        // GC-rooted handle to the most recently activated view. UnityW was
        // wrong here: it is a raw pointer whose alive-check DEREFERENCES that
        // pointer, and stored in native memory it neither keeps the object
        // alive nor nulls when the GC collects it — a use-after-free on
        // every async completion. SafePtrUnity holds a GC handle (memory
        // stays valid) and its bool checks m_CachedPtr on live memory, so it
        // correctly reports Unity destruction. Main thread only.
        SafePtrUnity<SnipeFeed::FeedView> activeView;
```

After the `FeedState state;` line, add:

```cpp
    // The live view to deliver deferred results to, or nullptr. Main thread
    // only — resolving a SafePtr is a GC-handle read.
    SnipeFeed::FeedView* ActiveViewAlive() {
        return state.activeView ? state.activeView.ptr() : nullptr;
    }
```

- [ ] **Step 3: Assign and clear the root.** In `DidActivate` (line 599), replace `state.activeView = UnityW<FeedView>(this);` with:

```cpp
    state.activeView = SafePtrUnity<FeedView>(this);
```

Add the new method (next to `DidActivate`):

```cpp
void FeedView::OnDestroy() {
    // Un-pin the GC root when the pinned view is the one being destroyed;
    // a dead view must not stay rooted until the next activation.
    if (state.activeView.ptr() == this)
        state.activeView = SafePtrUnity<FeedView>();
}
```

- [ ] **Step 4: Refresh callbacks read through the root.** In `Refresh()` (lines 371-425), replace each `auto view = state.activeView;` with `auto* view = ActiveViewAlive();` (three sites: progress callback, failure branch, success branch). The surrounding `if (view)` logic keeps working unchanged.

- [ ] **Step 5: Modal sprite callbacks — main→main SafePtrUnity capture.** In `OnCellClicked` (lines 198-214), replace `auto weakSelf = UnityW<FeedView>(this);` and both callbacks:

```cpp
    // SpriteCache callbacks are created, stored, invoked and destroyed on
    // the main thread only, so a SafePtrUnity capture is legal here and
    // both roots the view and reports its destruction.
    SafePtrUnity<FeedView> safeSelf(this);
    if (!e.coverUrl.empty()) {
        SpriteCache::GetSprite(e.coverUrl, [safeSelf, url = e.coverUrl](UnityEngine::Sprite* sprite) {
            if (!safeSelf || !safeSelf->modalCover) return;
            if (!safeSelf->modalPendingCover || static_cast<std::string>(safeSelf->modalPendingCover) != url) return;
            safeSelf->modalCover->set_sprite(sprite);
            safeSelf->modalCover->set_color(FeedCell::LoadedTint());
        });
    }
    if (!e.avatarUrl.empty()) {
        SpriteCache::GetSprite(e.avatarUrl, [safeSelf, url = e.avatarUrl](UnityEngine::Sprite* sprite) {
            if (!safeSelf || !safeSelf->modalAvatar) return;
            if (!safeSelf->modalPendingAvatar || static_cast<std::string>(safeSelf->modalPendingAvatar) != url) return;
            safeSelf->modalAvatar->set_sprite(sprite);
            safeSelf->modalAvatar->set_color(FeedCell::LoadedTint());
        });
    }
```

- [ ] **Step 6: Download/install completion — NO il2cpp captures** (this callback crosses the non-attached installer thread, where copying/destroying a SafePtr is illegal). Replace the `auto weakSelf = ...; Installer::DownloadAndInstallAsync(...)` block in `PlaySelected` (lines 303-344):

```cpp
    // This callback crosses the installer's non-attached worker thread —
    // constructing/copying/destroying a SafePtr there is not legal (GC
    // handle ops need an attached thread), so it captures only plain data
    // and resolves the live view on the main thread via state.activeView.
    Installer::DownloadAndInstallAsync(entry.songHash, [entry](bool success, std::string error) {
        BSML::MainThreadScheduler::Schedule([entry, success, error = std::move(error)]() mutable {
            if (!success) {
                state.busyPlaying = false;
                if (auto* view = ActiveViewAlive()) {
                    if (view->playButtonText) view->playButtonText->set_text("Download & Play");
                    if (view->playButton) view->playButton->set_interactable(true);
                    if (view->detailText) view->detailText->set_text("<color=#ff5555>" + error + "</color>");
                }
                return;
            }

            if (auto* view = ActiveViewAlive(); view && view->playButtonText)
                view->playButtonText->set_text("Installing...");
            SongCore::API::Loading::RefreshSongs(false);
            SongCore::API::Loading::RefreshLevelPacks();

            // Same grace period the BeatLeader mod uses before opening.
            BSML::MainThreadScheduler::ScheduleAfterTime(5, [entry]() {
                state.busyPlaying = false;
                auto level = Installer::GetInstalledLevel(entry.songHash);
                auto* view = ActiveViewAlive();
                if (!view) return;
                if (view->playButton) view->playButton->set_interactable(true);
                if (level) {
                    if (view->playButtonText) view->playButtonText->set_text("Play");
                    // Only auto-launch if the user is still on this tab —
                    // firing the solo re-entry while they browse another
                    // tab or screen would yank them away without warning.
                    if (view->get_isActiveAndEnabled()) {
                        view->LaunchLevel(level);
                    } else if (view->statusText) {
                        view->statusText->set_text("Downloaded — press Play when you're back.");
                    }
                } else {
                    if (view->playButtonText) view->playButtonText->set_text("Download & Play");
                    if (view->detailText)
                        view->detailText->set_text("Downloaded! The song is still loading — it will appear in Custom Levels shortly.");
                }
            });
        });
    });
```

(Delivering to the CURRENT view instead of the starting view is deliberate: the tab GameObject is recreated across menu visits, and the old code lost button/detail updates onto dead views.)

- [ ] **Step 7: Deferred DidActivate retry — main→main capture.** Replace the `weakSelf` block in `DidActivate` (lines 588-592):

```cpp
            SafePtrUnity<FeedView> safeSelf(this);
            BSML::MainThreadScheduler::Schedule([safeSelf]() mutable {
                if (safeSelf) safeSelf->DidActivate(false);
            });
```

- [ ] **Step 8: FeedCell sprite callbacks.** In `src/FeedCell.cpp` `SetData` (lines 88-102), replace `auto self = UnityW<FeedCell>(this);` with `SafePtrUnity<FeedCell> self(this);` — the two callbacks' bodies keep their exact guards (`if (!self || !self->pendingCoverUrl ...) return;`), which now test a rooted object.

- [ ] **Step 9: Grep gate.** `grep -rn "UnityW" src/ include/` must return **zero** matches in first-party code — every liveness guard is now SafePtrUnity or activeView-based.

- [ ] **Step 10: Verify** — `qpm s build` if available (this task is the one most worth a compile: SafePtrUnity's exact API — `ptr()`, `operator->`, `operator bool`, assignment from `T*` — must line up with the installed beatsaber-hook 6.4.x headers; if `state.activeView = SafePtrUnity<FeedView>(this)` fails to compile, use `state.activeView.emplace(this)` per the header). Else: re-read every capture list in FeedView.cpp/FeedCell.cpp against the thread rule in Global Constraints.

- [ ] **Step 11: Commit**

```bash
git add include/FeedView.hpp src/FeedView.cpp src/FeedCell.cpp
git commit -m "fix(quest): GC-rooted SafePtrUnity liveness guards; no il2cpp captures across worker threads (Q-C1)"
```

---

### Task 10: Quest — dropdown/modal guards, keep stale feed, sprite cache bound, TMP escaping (Q-I6, Q-I7, Q-M1, Q-M2 + parity keep-stale)

**Files:**
- Modify: `src/FeedView.cpp` (as left by Task 9)
- Modify: `src/FeedCell.cpp`
- Modify: `src/SpriteCache.cpp`
- Modify: `include/Format.hpp`

**Interfaces:**
- Consumes: `ActiveViewAlive()` (Task 9); `FeedResult.error`-as-info contract (Task 7).
- Produces: `Format::Escape(std::string const&) : std::string`.

- [ ] **Step 1: `Format::Escape`** in `include/Format.hpp` (top of the namespace):

```cpp
    // TMP parses '<' as markup, and feed strings (song names like "<3",
    // player names) must not be able to open tags. A zero-width space
    // (U+200B) directly after every '<' keeps the character visible while
    // breaking tag parsing. TMP does NOT decode HTML entities, so
    // &lt;-style escaping would render literally.
    inline std::string Escape(std::string const& text) {
        std::string out;
        out.reserve(text.size());
        for (char c : text) {
            out += c;
            if (c == '<') out += "\xE2\x80\x8B";
        }
        return out;
    }
```

Apply it inside Format.hpp: in `SongLine` — `std::string line = Escape(e.songName);`; in `StatsLine` — `Escape(e.modifiers)`; in `TitleLine` — `Escape(e.songName)`, `Escape(e.songAuthor)`, `Escape(e.mapper)`.

- [ ] **Step 2: Escape the remaining user-string sites.** `src/FeedCell.cpp` `SetData`: `playerText->set_text("<b>" + Format::Escape(entry.playerName) + "</b>");` (add `#include "Format.hpp"` — already included). `src/FeedView.cpp` `OnCellClicked`: the detail-info builder — `Escape(e.songName)` in the `<size=140%>` line, `Escape(e.songAuthor)` / `Escape(e.mapper)` in the byline, and `modalPlayerText->set_text(Format::Escape(e.playerName));`. NOTE: the existing `Format::SongLine(e).substr(e.songName.size())` trick would break once SongLine escapes the name (escaped length differs) — replace that line with the equivalent explicit build:

```cpp
        if (!e.difficulty.empty() || e.stars > 0.0f) {
            std::string diffLine;
            if (!e.difficulty.empty())
                diffLine += "  <size=75%><color=" + std::string(Format::DiffColor(e.difficulty)) + ">" + Format::DiffLabel(e.difficulty) + "</color></size>";
            if (e.stars > 0.0f)
                diffLine += std::format("  <size=75%><color=#ffaa22>{:.1f}★</color></size>", e.stars);
            info += "\n<size=85%>" + diffLine + "</size>";
        }
```

Also in Task 9's install-completion failure branch: `view->detailText->set_text("<color=#ff5555>" + Format::Escape(error) + "</color>");` and in `Refresh`'s failure branch, error text passes through as-is (our own wording — no escaping needed; BeatLeader alias errors embed user input: `ResolvePlayer`'s messages include `aliasOrId` — leave, the input is the user's own config).

- [ ] **Step 3: Dismiss an open filter dropdown before rebuilding** (Q-I7). In `RebuildFilter` (line 103), before the destroy loop, add (plus includes `HMUI/SimpleTextDropdown.hpp`, `HMUI/ModalView.hpp` at the top of FeedView.cpp):

```cpp
    // If the dropdown's option list is open, destroying it mid-show orphans
    // its modal blocker and can eat all menu input until a scene change.
    if (auto* dropdown = filterContainer->GetComponentInChildren<HMUI::SimpleTextDropdown*>()) {
        if (auto* modal = dropdown->_modalView.unsafePtr(); modal && modal->_isShown)
            modal->Hide(false, nullptr);
    }
```

(If `_modalView` is declared as a plain pointer rather than `UnityW` in the installed bs-cordl headers, drop the `.unsafePtr()`.)

- [ ] **Step 4: Failed refresh keeps the good feed + hides the modal** (parity with P-I4; Q-M1). In `Refresh()`'s completion (as left by Task 9), replace the `if (!result.success) { ... }` block:

```cpp
                if (!result.success) {
                    auto* view = ActiveViewAlive();
                    // Keep the previous feed on a failed refresh — a network
                    // blip shouldn't blank a perfectly good list.
                    if (!state.entries.empty()) {
                        if (view && view->statusText)
                            view->statusText->set_text(result.error + "\n(Showing the previous scores.)");
                        return;
                    }
                    state.selected = -1;
                    if (view) {
                        view->RebuildFilter();
                        view->RebuildList();
                        if (view->statusText)
                            view->statusText->set_text(result.error);
                        if (view->detailModal) view->detailModal->Hide();
                    }
                    return;
                }
```

And in the success branch, after `view->RebuildList();`, add the empty-success info (Task 7 contract):

```cpp
                    if (state.entries.empty() && view->statusText && !result.error.empty())
                        view->statusText->set_text(result.error);
```

- [ ] **Step 5: Bound the sprite cache** (Q-I6). In `src/SpriteCache.cpp`: add `#include "UnityEngine/Object.hpp"` and `#include <algorithm>`; in the anonymous namespace add:

```cpp
        constexpr size_t MAX_CACHED_SPRITES = 256;
        // Oldest-first access order; a hit moves the URL to the back. Sized
        // above one full 100-score feed's covers+avatars so eviction only
        // bites across many refreshes, never inside the current view.
        std::vector<std::string> loadOrder;

        void EvictIfNeeded() {
            while (loadOrder.size() > MAX_CACHED_SPRITES) {
                auto oldest = loadOrder.front();
                loadOrder.erase(loadOrder.begin());
                auto it = cache.find(oldest);
                if (it == cache.end()) continue;
                if (it->second) {
                    // The texture holds the memory; the sprite is a wrapper.
                    // Cells re-request evicted URLs on their next bind.
                    if (auto* sprite = it->second.ptr()) {
                        if (auto* texture = sprite->get_texture()) UnityEngine::Object::Destroy(texture);
                        UnityEngine::Object::Destroy(sprite);
                    }
                }
                cache.erase(it);
            }
        }
```

In `GetSprite`'s cache-hit branch, before `onSprite(...)`:

```cpp
            auto pos = std::find(loadOrder.begin(), loadOrder.end(), url);
            if (pos != loadOrder.end()) loadOrder.erase(pos);
            loadOrder.push_back(url);
```

In the scheduled store path, after `cache[url] = sprite;`:

```cpp
                loadOrder.push_back(url);
                EvictIfNeeded();
```

And in `ClearCache()`, add `loadOrder.clear();`.

- [ ] **Step 6: Empty-success must not re-refresh every activation.** In `DidActivate` (line 605), replace the stale check with time-only (mirrors PC Task 4 Step 2 — an authenticated empty feed sets `lastFetchTime` and deserves the same 2-minute cache; a never-fetched session still reads stale because `lastFetchTime` is 0):

```cpp
    bool stale = (static_cast<long long>(std::time(nullptr)) - state.lastFetchTime) > REFRESH_MAX_AGE_SECONDS;
```

- [ ] **Step 7: Verify** — `qpm s build` if available; else re-read for: `DiffColor`/`DiffLabel` are namespace-qualified where used from FeedView.cpp; includes added; no site still calls the removed `SongLine(...).substr(...)` pattern.

- [ ] **Step 8: Commit**

```bash
git add src/FeedView.cpp src/FeedCell.cpp src/SpriteCache.cpp include/Format.hpp
git commit -m "fix(quest): guard open dropdown across rebuilds, keep stale feed, bound sprite cache, TMP-safe strings (Q-I6, Q-I7, Q-M1, Q-M2)"
```

---

### Task 11: Docs truth-up, version bump, tracker close-out (Q-M7, P-M5-doc, P-M7-doc)

**Files:**
- Modify: `README.md`
- Modify: `qpm.json`, `mod.json`, `mod.template.json` (version)
- Modify: `pcvr/manifest.json` (version)
- Modify: `docs/code-review-2026-08-25.md` (checkboxes)

**Interfaces:** none.

- [ ] **Step 1: README corrections** (Q-M7):
  - Line 18 `- Uses only public BeatLeader API endpoints — no login, no credentials stored` → `- Reuses the BeatLeader mod's login read-only when present (never modified or stored by SnipeFeed); otherwise only public API endpoints`.
  - Line 17 dependency list: append `, SongCore`.
  - "What the Play button does" table: add a note under it — `In Party mode the picker is selected in place on PC (the panel's picker is already showing); on Quest, Party follows the Solo re-entry path.` Adjust wording to what the code actually does after this plan (PC: picker branch; Quest: `IsSoloFlowOnTop` is false in Party, so picker-or-download).
  - v2.0.x notes: add a `## v2.0.1 fixes` section summarizing: TLS verification on Quest, crash-safe async lifetimes, atomic map installs, truthful empty-feed/network errors, kept feed on failed refresh, bounded image caches, no more freeze during PC map installs.
  - Scores stepper: in the v0.5.0 bullet, append `(on the public-API path the per-player pull scales up to meet the stepper, capped at 20 per player)`.

- [ ] **Step 2: Version bump.** `qpm.json` `"version": "2.0.1"`; `mod.json` + `mod.template.json` `"version": "2.0.1"` (if the Quest toolchain is set up, regenerate with `qpm qmod manifest` instead of hand-editing mod.json); `pcvr/manifest.json` `"version": "2.0.1-pc.1"`. Update the three PC `UserAgent` strings `SnipeFeed-PC/2.0.0` → `SnipeFeed-PC/2.0.1` (BeatLeaderService.cs:23, SpriteCache.cs:27, SongInstaller.cs:28) and Quest's `SnipeFeed/` UA follows `VERSION` automatically.

- [ ] **Step 3: Close out the tracker.** In `docs/code-review-2026-08-25.md`, check every box actually fixed; add one line at the top: `Status 2026-08-25: all items addressed — see docs/superpowers/plans/2026-08-25-reliability-fixes.md. Quest compile verification: <yes/no>; Quest on-device TLS smoke test: <done/pending>; PC compile verification: <yes/no>.` Fill in the truth from build-env.md — never claim a verification that didn't run. P-M8's "time-ago staleness" is closed as no-change-needed: `OnTabShown` rebuilds rows, so staleness is bounded by a continuously-open tab, minutes at most.

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "docs: truth-up README, bump to 2.0.1, close out reliability review tracker"
```

---

## Coverage map (self-review against the spec)

| Spec item | Task | | Spec item | Task |
|---|---|---|---|---|
| Q-C1 | 9 | | P-C1 | 2 |
| Q-I1 | 6 | | P-I1 | 3 |
| Q-I2 | 8 | | P-I2 | 3 |
| Q-I3 | 8 | | P-I3 | 3 |
| Q-I4 | 7 | | P-I4 | 4 |
| Q-I5 | 6 | | P-I5 | 2 |
| Q-I6 | 10 | | P-I6 | 5 |
| Q-I7 | 10 | | P-I7 | 4 |
| Q-M1 | 10 | | P-I8 | 4 |
| Q-M2 | 10 | | P-M1 | 5 |
| Q-M3 | 7 | | P-M2 | 5 |
| Q-M4 | 7 | | P-M3 | 2 |
| Q-M5 | 7+8 | | P-M4 | 2 |
| Q-M6 | 8 | | P-M5 | 4+11 |
| Q-M7 | 11 | | P-M6 | 4 |
| Q-M8 | 6 | | P-M7 | 3+11 |
| Q-M9 | consciously skipped — bounded by visible cells; a queue adds machinery without a failure mode to fix | | P-M8 | 2+3+5 (+time-ago closed as no-change, Task 11) |
