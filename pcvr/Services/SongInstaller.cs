using Newtonsoft.Json;
using SongCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
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
        private static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

        static SongInstaller()
        {
            Client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipeFeed-PC/2.0.0");
        }

        public static BeatmapLevel GetInstalledLevel(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return null;
            return Loader.GetLevelByHash(hash) ?? Loader.GetLevelByHash(hash.ToLowerInvariant());
        }

        public static async Task<SongInstallResult> DownloadAndInstallAsync(string hash)
        {
            hash = (hash ?? "").Trim().ToLowerInvariant();
            if (hash.Length == 0) return Fail("This score is not a downloadable custom song.");

            var existing = GetInstalledLevel(hash);
            if (existing != null) return new SongInstallResult { Success = true, Level = existing };

            try
            {
                var metadataResponse = await Client.GetAsync("https://api.beatsaver.com/maps/hash/" + Uri.EscapeDataString(hash));
                if ((int)metadataResponse.StatusCode == 404) return Fail("Map not found on BeatSaver.");
                if (!metadataResponse.IsSuccessStatusCode) return Fail("BeatSaver lookup failed (HTTP " + (int)metadataResponse.StatusCode + ").");

                var map = JsonConvert.DeserializeObject<BeatSaverMap>(await metadataResponse.Content.ReadAsStringAsync());
                var version = map?.versions?.FirstOrDefault(v => string.Equals(v.hash, hash, StringComparison.OrdinalIgnoreCase))
                    ?? map?.versions?.FirstOrDefault();
                if (version == null || string.IsNullOrWhiteSpace(version.downloadURL)) return Fail("BeatSaver did not provide a download for this map.");

                var zipBytes = await Client.GetByteArrayAsync(version.downloadURL);
                if (zipBytes == null || zipBytes.Length == 0) return Fail("The map download was empty.");

                var customLevels = Path.GetFullPath(Path.Combine(Application.dataPath, "CustomLevels"));
                Directory.CreateDirectory(customLevels);
                var shortHash = hash.Length > 10 ? hash.Substring(0, 10) : hash;
                var target = Path.Combine(customLevels, "SnipeFeed_" + shortHash);

                if (Directory.Exists(target)) Directory.Delete(target, true);
                Directory.CreateDirectory(target);
                ExtractZipSafely(zipBytes, target);

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
                return Fail("Map download timed out.");
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Map install failed: " + ex);
                return Fail("Map install failed: " + ex.Message);
            }
        }

        private static void ExtractZipSafely(byte[] bytes, string target)
        {
            var root = Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            using (var memory = new MemoryStream(bytes, false))
            using (var archive = new ZipArchive(memory, ZipArchiveMode.Read))
            {
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
                        input.CopyTo(output);
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

        private static SongInstallResult Fail(string error) => new SongInstallResult { Success = false, Error = error };

        private sealed class BeatSaverMap { public List<BeatSaverVersion> versions { get; set; } }
        private sealed class BeatSaverVersion { public string hash { get; set; } public string downloadURL { get; set; } }
    }
}
