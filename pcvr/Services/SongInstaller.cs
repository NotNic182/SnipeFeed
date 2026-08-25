using HMUI;
using Newtonsoft.Json;
using SongCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SnipeFeed.PC.Services
{
    internal sealed class SongInstallResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        public BeatmapLevel Level { get; set; }
    }

    internal static class SongInstaller
    {
        private const long MaxExtractedBytes = 512L * 1024 * 1024;
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        static SongInstaller()
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipeFeed-PC/2.0.0");
        }

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

        // Declared sizes in the central directory can be forged; the only
        // trustworthy count is bytes actually produced by decompression.
        private static void CopyBounded(Stream input, Stream output, ref long totalWritten)
        {
            var buffer = new byte[81920];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalWritten += read;
                if (totalWritten > MaxExtractedBytes)
                    throw new InvalidDataException("Map archive is unreasonably large.");
                output.Write(buffer, 0, read);
            }
        }

        private static void ExtractZipSafely(byte[] bytes, string target)
        {
            var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var memory = new MemoryStream(bytes, false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            {
                // A hostile or corrupt archive must not be able to fill the
                // disk; real beatmaps are tens of MB at most.
                long declaredTotal = 0;
                foreach (var entry in archive.Entries)
                {
                    declaredTotal += entry.Length;
                    if (declaredTotal > MaxExtractedBytes)
                        throw new InvalidDataException("Map archive is unreasonably large.");
                }

                long actualTotal = 0;
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    var destination = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
                    if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Map archive attempted to write outside its install folder.");

                    if (entry.FullName.EndsWith("/", StringComparison.Ordinal) || entry.FullName.EndsWith("\\", StringComparison.Ordinal))
                    {
                        Directory.CreateDirectory(destination);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(destination);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    using (var input = entry.Open())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                        CopyBounded(input, output, ref actualTotal);
                }
            }
        }

        private static async Task RefreshSongCore()
        {
            if (Loader.Instance == null) return;

            var completion = new TaskCompletionSource<bool>();
            Action<Loader, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel>> handler = null;
            handler = (loader, levels) =>
            {
                Loader.SongsLoadedEvent -= handler;
                completion.TrySetResult(true);
            };

            Loader.SongsLoadedEvent += handler;
            Loader.Instance.RefreshSongs(false);

            var finished = await Task.WhenAny(completion.Task, Task.Delay(15000));
            Loader.SongsLoadedEvent -= handler;
            if (finished != completion.Task)
                Plugin.Log?.Warn("Timed out waiting for SongCore refresh; the map may appear a little later.");
        }

        // Mirrors the Quest mod's Installer (itself modeled on BeatLeader's
        // MapDownloadDialog.OpenMap), split in two: priming stores the
        // level selection on the solo flow coordinator (applied on its next
        // activation), pressing the Solo button only works once the main
        // menu is visible again.
        public static bool PrimeSoloFlow(BeatmapLevel level)
        {
            if (level == null) return false;

            var customLevelsPack = Loader.CustomLevelsPack;
            if (customLevelsPack == null || customLevelsPack._beatmapLevels == null || customLevelsPack._beatmapLevels.Length == 0)
                return false;

            var soloFlowCoordinator = Resources.FindObjectsOfTypeAll<SoloFreePlayFlowCoordinator>().FirstOrDefault();
            if (soloFlowCoordinator == null)
            {
                Plugin.Log?.Error("SoloFreePlayFlowCoordinator not found");
                return false;
            }

            var state = new LevelSelectionFlowCoordinator.State(
                SelectLevelCategoryViewController.LevelCategory.All, customLevelsPack, default, level);
            soloFlowCoordinator.Setup(state);
            return true;
        }

        public static bool PressSoloButton()
        {
            var songSelectButton = GameObject.Find("SoloButton")
                ?? GameObject.Find("Wrapper/BeatmapWithModifiers/BeatmapSelection/EditButton");
            if (songSelectButton == null)
            {
                Plugin.Log?.Error("Could not find the solo menu button to press");
                return false;
            }

            var button = songSelectButton.GetComponent<NoTransitionsButton>()
                ?? songSelectButton.GetComponentInChildren<NoTransitionsButton>();
            if (button == null)
            {
                Plugin.Log?.Error("Solo menu object found but has no NoTransitionsButton");
                return false;
            }

            button.onClick.Invoke();
            return true;
        }

        private static SongInstallResult Fail(string error) => new SongInstallResult { Success = false, Error = error };

        private sealed class BeatSaverMap { public List<BeatSaverVersion> versions { get; set; } }
        private sealed class BeatSaverVersion { public string hash { get; set; } public string downloadURL { get; set; } }
    }
}
