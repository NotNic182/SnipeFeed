using BeatSaberMarkupLanguage;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

namespace SnipeFeed.PC.Services
{
    // URL -> Sprite cache for cover art and avatars, mirroring the Quest
    // mod's SpriteCache: in-flight downloads are coalesced, failed URLs are
    // remembered so scrolling doesn't hammer dead links, and a successful
    // feed refresh clears the failure memo so images can retry.
    //
    // Every member must be used from the Unity main thread only; the async
    // continuations resume on Unity's synchronization context.
    internal static class SpriteCache
    {
        private const int MaxCachedSprites = 256;
        // Oldest-first access order; a hit moves the URL to the back. Sized
        // above one full 100-score feed's covers+avatars so eviction only
        // bites across MANY refreshes, never inside the current view.
        private static readonly List<string> LoadOrder = new List<string>();
        private static readonly Dictionary<string, Sprite> Loaded = new Dictionary<string, Sprite>();
        private static readonly HashSet<string> Failed = new HashSet<string>();
        private static readonly Dictionary<string, List<Action<Sprite>>> Pending = new Dictionary<string, List<Action<Sprite>>>();
        private static readonly HttpClient Client = CreateClient();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipeFeed-PC/2.0.1");
            return client;
        }

        public static void GetSprite(string url, Action<Sprite> onSprite)
        {
            if (string.IsNullOrEmpty(url) || Failed.Contains(url)) return;

            if (Loaded.TryGetValue(url, out var sprite) && sprite != null)
            {
                LoadOrder.Remove(url);
                LoadOrder.Add(url);
                onSprite(sprite);
                return;
            }

            // Coalesce: if a download for this URL is in flight, just queue up.
            if (Pending.TryGetValue(url, out var inFlight))
            {
                inFlight.Add(onSprite);
                return;
            }

            Pending[url] = new List<Action<Sprite>> { onSprite };
            _ = DownloadAsync(url);
        }

        public static void ClearFailures()
        {
            Failed.Clear();
        }

        private static async Task DownloadAsync(string url)
        {
            Sprite sprite = null;
            try
            {
                var bytes = await Client.GetByteArrayAsync(url);
                if (bytes != null && bytes.Length > 0)
                    sprite = await Utilities.LoadSpriteAsync(bytes);
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug("Image download failed for " + url + ": " + ex.Message);
            }

            Pending.TryGetValue(url, out var callbacks);
            Pending.Remove(url);

            if (sprite == null)
            {
                Failed.Add(url);
                return;
            }

            Loaded[url] = sprite;
            LoadOrder.Add(url);
            EvictIfNeeded();
            if (callbacks == null) return;
            foreach (var callback in callbacks)
            {
                try
                {
                    callback(sprite);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.Warn("Sprite callback failed: " + ex.Message);
                }
            }
        }

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
    }
}
