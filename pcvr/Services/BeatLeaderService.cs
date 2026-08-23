using Newtonsoft.Json;
using SnipeFeed.PC.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace SnipeFeed.PC.Services
{
    internal sealed class BeatLeaderService
    {
        private const string ApiBase = "https://api.beatleader.com";
        private static readonly Uri ApiUri = new Uri(ApiBase);
        private static readonly HttpClient PublicClient = CreatePublicClient();

        private static HttpClient CreatePublicClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipeFeed-PC/2.0.0");
            return client;
        }

        public async Task<FeedResult> FetchFeedAsync(string playerInput, int maxPlayers, int scoresPerPlayer, int feedCount, Action<string> progress = null)
        {
            var result = new FeedResult();
            feedCount = Math.Max(10, Math.Min(100, feedCount));
            maxPlayers = Math.Max(1, Math.Min(50, maxPlayers));
            scoresPerPlayer = Math.Max(1, Math.Min(10, scoresPerPlayer));

            try
            {
                progress?.Invoke("Loading your BeatLeader friends feed...");
                var friendEntries = await TryFetchAuthenticatedFriendScores(feedCount);
                if (friendEntries != null && friendEntries.Count > 0)
                {
                    result.Entries = friendEntries.OrderByDescending(x => x.Timepost).Take(feedCount).ToList();
                    result.Success = true;
                    return result;
                }

                var input = SanitizeInput(playerInput);
                if (string.IsNullOrEmpty(input))
                {
                    result.Error = "Couldn't reuse a BeatLeader PC login. Set PlayerId in UserData/SnipeFeedPC.json, then press Refresh.";
                    return result;
                }

                progress?.Invoke("Resolving your BeatLeader profile...");
                var self = await ResolvePlayer(input);
                if (self == null || string.IsNullOrEmpty(self.id))
                {
                    result.Error = "No BeatLeader player found for '" + input + "'.";
                    return result;
                }

                progress?.Invoke("Loading players you follow...");
                var following = await FetchFollowing(self.id, maxPlayers);
                if (following == null || following.Count == 0)
                {
                    result.Error = "Your following list is empty or hidden. Log into the BeatLeader mod, or disable 'Hide my friends' on beatleader.com.";
                    return result;
                }

                var gate = new SemaphoreSlim(4);
                var completed = 0;
                var tasks = following.Select(async player =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var scores = await FetchRecentScores(player, scoresPerPlayer);
                        var done = Interlocked.Increment(ref completed);
                        progress?.Invoke("Loading scores... " + done + "/" + following.Count);
                        return scores;
                    }
                    finally
                    {
                        gate.Release();
                    }
                }).ToArray();

                var chunks = await Task.WhenAll(tasks);
                result.Entries = chunks.SelectMany(x => x)
                    .OrderByDescending(x => x.Timepost)
                    .Take(feedCount)
                    .ToList();

                if (result.Entries.Count == 0)
                {
                    result.Error = "No recent scores found for the players you follow.";
                    return result;
                }

                result.Success = true;
                return result;
            }
            catch (TaskCanceledException)
            {
                result.Error = "BeatLeader request timed out. Check your connection and try again.";
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Feed refresh failed: " + ex);
                result.Error = "Couldn't load the feed: " + ex.Message;
                return result;
            }
        }

        private static async Task<List<FeedEntry>> TryFetchAuthenticatedFriendScores(int count)
        {
            using (var client = CreateBeatLeaderAuthenticatedClient())
            {
                if (client == null) return null;

                var response = await client.GetAsync(ApiBase + "/user/friendScores?sortBy=date&order=desc&page=1&count=" + count);
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadAsStringAsync();
                var page = JsonConvert.DeserializeObject<ScorePageDto>(body);
                if (page?.data == null) return null;

                var unknown = new PlayerDto { name = "?" };
                return page.data.Where(x => x != null).Select(x => ParseScore(x, unknown)).ToList();
            }
        }

        private static HttpClient CreateBeatLeaderAuthenticatedClient()
        {
            try
            {
                var factoryType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType("BeatLeader.WebRequests.WebRequestFactory", false))
                    .FirstOrDefault(t => t != null);
                if (factoryType == null) return null;

                var field = factoryType.GetField("CookieContainer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var source = field?.GetValue(null) as CookieContainer;
                if (source == null) return null;

                var target = new CookieContainer();
                foreach (Cookie cookie in source.GetCookies(ApiUri))
                {
                    var domain = string.IsNullOrEmpty(cookie.Domain) ? ApiUri.Host : cookie.Domain;
                    target.Add(new Cookie(cookie.Name, cookie.Value, string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path, domain));
                }

                if (target.GetCookies(ApiUri).Count == 0) return null;

                var handler = new HttpClientHandler { CookieContainer = target, UseCookies = true };
                var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipeFeed-PC/2.0.0");
                return client;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug("BeatLeader cookie reuse unavailable: " + ex.Message);
                return null;
            }
        }

        private static async Task<PlayerDto> ResolvePlayer(string input)
        {
            var response = await PublicClient.GetAsync(ApiBase + "/player/" + Uri.EscapeDataString(input));
            if (!response.IsSuccessStatusCode) return null;
            return JsonConvert.DeserializeObject<PlayerDto>(await response.Content.ReadAsStringAsync());
        }

        private static async Task<List<PlayerDto>> FetchFollowing(string playerId, int maxPlayers)
        {
            var response = await PublicClient.GetAsync(ApiBase + "/player/" + Uri.EscapeDataString(playerId) + "/followers?page=1&count=" + maxPlayers + "&type=following");
            if (!response.IsSuccessStatusCode) return null;
            return JsonConvert.DeserializeObject<List<PlayerDto>>(await response.Content.ReadAsStringAsync());
        }

        private static async Task<List<FeedEntry>> FetchRecentScores(PlayerDto player, int count)
        {
            try
            {
                var response = await PublicClient.GetAsync(ApiBase + "/player/" + Uri.EscapeDataString(player.id) + "/scores?sortBy=date&order=desc&page=1&count=" + count);
                if (!response.IsSuccessStatusCode) return new List<FeedEntry>();

                var page = JsonConvert.DeserializeObject<ScorePageDto>(await response.Content.ReadAsStringAsync());
                return page?.data?.Where(x => x != null).Select(x => ParseScore(x, player)).ToList() ?? new List<FeedEntry>();
            }
            catch (Exception ex)
            {
                Plugin.Log?.Warn("Score request for " + player.id + " failed: " + ex.Message);
                return new List<FeedEntry>();
            }
        }

        private static FeedEntry ParseScore(ScoreDto score, PlayerDto fallback)
        {
            var player = score.player ?? fallback ?? new PlayerDto();
            var song = score.leaderboard?.song ?? new SongDto();
            var difficulty = score.leaderboard?.difficulty ?? new DifficultyDto();

            long timestamp = 0;
            if (score.timepost > 0)
                timestamp = (long)score.timepost;
            else if (!string.IsNullOrEmpty(score.timeset))
                long.TryParse(score.timeset, out timestamp);

            return new FeedEntry
            {
                PlayerId = player.id ?? "",
                PlayerName = player.name ?? "?",
                AvatarUrl = player.avatar ?? "",
                SongName = song.name ?? "",
                SongAuthor = song.author ?? "",
                Mapper = song.mapper ?? "",
                SongHash = song.hash ?? "",
                CoverUrl = song.coverImage ?? "",
                Difficulty = difficulty.difficultyName ?? "",
                Stars = (float)difficulty.stars,
                Accuracy = (float)score.accuracy,
                Pp = (float)score.pp,
                Modifiers = score.modifiers ?? "",
                FullCombo = score.fullCombo,
                Timepost = timestamp
            };
        }

        private static string SanitizeInput(string input)
        {
            input = (input ?? "").Trim().TrimEnd('/');
            var slash = input.LastIndexOf('/');
            if (slash >= 0) input = input.Substring(slash + 1);
            return input.Trim();
        }

        private sealed class ScorePageDto { public List<ScoreDto> data { get; set; } }
        private sealed class PlayerDto { public string id { get; set; } public string name { get; set; } public string avatar { get; set; } }
        private sealed class ScoreDto
        {
            public PlayerDto player { get; set; }
            public double accuracy { get; set; }
            public double pp { get; set; }
            public string modifiers { get; set; }
            public double timepost { get; set; }
            public string timeset { get; set; }
            public bool fullCombo { get; set; }
            public LeaderboardDto leaderboard { get; set; }
        }
        private sealed class LeaderboardDto { public SongDto song { get; set; } public DifficultyDto difficulty { get; set; } }
        private sealed class SongDto { public string name { get; set; } public string author { get; set; } public string mapper { get; set; } public string hash { get; set; } public string coverImage { get; set; } }
        private sealed class DifficultyDto { public string difficultyName { get; set; } public double stars { get; set; } }
    }
}
