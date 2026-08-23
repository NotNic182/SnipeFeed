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
using UnityEngine.Networking;

namespace SnipeFeed.PC.Services
{
    internal sealed class BeatLeaderService
    {
        private const string ApiBase = "https://api.beatleader.com";
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
                var friendEntries = await TryFetchAuthenticatedFriendScores(feedCount, progress);
                if (friendEntries != null && friendEntries.Count > 0)
                {
                    result.Entries = friendEntries.OrderByDescending(x => x.Timepost).Take(feedCount).ToList();
                    result.Success = true;
                    return result;
                }

                var input = SanitizeInput(playerInput);
                if (string.IsNullOrEmpty(input))
                {
                    result.Error = "Couldn't reuse a BeatLeader PC login.\nInstall the BeatLeader mod and let it sign in, then press Refresh.\n(Or set PlayerId in UserData/SnipeFeedPC.json to use the public API.)";
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

        // How long to wait for the BeatLeader mod's automatic sign-in.
        // It starts when the menu scene loads (OnMenuInstaller ->
        // Authentication.Login), so the first Snipe Feed refresh can easily
        // run before the login cookie exists.
        private const int LoginWaitMilliseconds = 8000;

        // The BeatLeader PC mod signs into whichever server is selected in
        // its own settings; the login cookie only works against that host.
        private static readonly string[] KnownApiBases =
        {
            "https://api.beatleader.com",
            "https://api.beatleader.net",
        };

        private sealed class BeatLeaderSession
        {
            public string ApiBase;
            public CookieCollection Cookies;
        }

        private static async Task<List<FeedEntry>> TryFetchAuthenticatedFriendScores(int count, Action<string> progress)
        {
            var beatLeader = FindBeatLeaderAssembly();
            if (beatLeader == null) return null;

            if (!IsBeatLeaderSignedIn(beatLeader) && TryGetBeatLeaderSession(beatLeader) == null)
            {
                progress?.Invoke("Waiting for the BeatLeader mod to sign in...");
                await WaitForBeatLeaderLogin(beatLeader);
            }

            // Newer BeatLeader versions keep the login in a managed cookie
            // container we can copy read-only into our own client.
            var session = TryGetBeatLeaderSession(beatLeader);
            if (session != null)
            {
                var viaCookies = await FetchFriendScoresWithCookies(session, count);
                if (viaCookies != null) return viaCookies;
            }

            // Older versions (0.9.x) sign in through UnityWebRequest, whose
            // engine-level cookie cache is shared process-wide — a
            // UnityWebRequest from us to the same server carries the login
            // automatically. Without a login the request just returns 401.
            foreach (var apiBase in CandidateApiBases(beatLeader))
            {
                var viaUnity = await FetchFriendScoresViaUnity(apiBase, count);
                if (viaUnity != null) return viaUnity;
            }

            return null;
        }

        private static async Task<List<FeedEntry>> FetchFriendScoresWithCookies(BeatLeaderSession session, int count)
        {
            // Copy the cookies read-only so we never mutate the BeatLeader
            // mod's own session state.
            var apiUri = new Uri(session.ApiBase);
            var target = new CookieContainer();
            foreach (Cookie cookie in session.Cookies)
            {
                var domain = string.IsNullOrEmpty(cookie.Domain) ? apiUri.Host : cookie.Domain;
                target.Add(new Cookie(cookie.Name, cookie.Value, string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path, domain));
            }

            using (var handler = new HttpClientHandler { CookieContainer = target, UseCookies = true })
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd("SnipeFeed-PC/2.0.0");

                var response = await client.GetAsync(FriendScoresUrl(session.ApiBase, count));
                if (!response.IsSuccessStatusCode)
                {
                    Plugin.Log?.Info("friendScores via the BeatLeader session cookies returned HTTP " + (int)response.StatusCode + ".");
                    return null;
                }

                return ParseFriendScores(await response.Content.ReadAsStringAsync());
            }
        }

        private static async Task<List<FeedEntry>> FetchFriendScoresViaUnity(string apiBase, int count)
        {
            try
            {
                using (var request = UnityWebRequest.Get(FriendScoresUrl(apiBase, count)))
                {
                    request.timeout = 15;
                    request.SetRequestHeader("User-Agent", "SnipeFeed-PC/2.0.0");
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();

                    if (request.responseCode != 200)
                    {
                        Plugin.Log?.Info("friendScores via the Unity cookie cache returned HTTP " + request.responseCode + " on " + apiBase + ".");
                        return null;
                    }

                    return ParseFriendScores(request.downloadHandler.text);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug("Unity web request for friendScores failed: " + ex.Message);
                return null;
            }
        }

        private static string FriendScoresUrl(string apiBase, int count) =>
            apiBase + "/user/friendScores?sortBy=date&order=desc&page=1&count=" + count;

        private static List<FeedEntry> ParseFriendScores(string body)
        {
            var page = JsonConvert.DeserializeObject<ScorePageDto>(body);
            if (page?.data == null) return null;

            var unknown = new PlayerDto { name = "?" };
            var entries = page.data.Where(x => x != null).Select(x => ParseScore(x, unknown)).ToList();
            return entries.Count > 0 ? entries : null;
        }

        // Old BeatLeader versions have no WaitLogin task, only this flag.
        private static bool IsBeatLeaderSignedIn(Assembly beatLeader)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var authType = beatLeader.GetType("BeatLeader.API.Authentication", false);
                return authType?.GetField("_signedIn", flags)?.GetValue(null) as bool? ?? false;
            }
            catch
            {
                return false;
            }
        }

        private static Assembly FindBeatLeaderAssembly()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetName().Name == "BeatLeader") return assembly;
                }
                catch
                {
                    // A single misbehaving assembly must not abort the scan.
                }
            }
            return null;
        }

        // Awaits BeatLeader's own Authentication.WaitLogin() task when the
        // running version exposes it. That task never completes when the
        // login fails, so it is always raced against a timeout. Falls back
        // to polling for the login cookie on versions without WaitLogin.
        private static async Task WaitForBeatLeaderLogin(Assembly beatLeader)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var authType = beatLeader.GetType("BeatLeader.API.Authentication", false);
                var waitLogin = authType?.GetMethod("WaitLogin", flags);
                if (waitLogin != null && waitLogin.Invoke(null, null) is Task loginTask)
                {
                    await Task.WhenAny(loginTask, Task.Delay(LoginWaitMilliseconds));
                    return;
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug("BeatLeader WaitLogin unavailable: " + ex.Message);
            }

            for (var waited = 0; waited < LoginWaitMilliseconds; waited += 500)
            {
                if (IsBeatLeaderSignedIn(beatLeader) || TryGetBeatLeaderSession(beatLeader) != null) return;
                await Task.Delay(500);
            }
        }

        private static BeatLeaderSession TryGetBeatLeaderSession(Assembly beatLeader)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var factoryType = beatLeader.GetType("BeatLeader.WebRequests.WebRequestFactory", false);
                if (factoryType == null) return null;

                var containerObj = factoryType.GetField("CookieContainer", flags)?.GetValue(null)
                    ?? factoryType.GetProperty("CookieContainer", flags)?.GetValue(null);
                if (!(containerObj is CookieContainer container)) return null;

                foreach (var apiBase in CandidateApiBases(beatLeader))
                {
                    var cookies = container.GetCookies(new Uri(apiBase));
                    if (cookies.Count > 0)
                        return new BeatLeaderSession { ApiBase = apiBase, Cookies = cookies };
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug("BeatLeader cookie reuse unavailable: " + ex.Message);
            }
            return null;
        }

        // The server BeatLeader is actually signed into, first from its own
        // BLConstants (a property in current versions, a const field in old
        // ones), then the known mirrors.
        private static IEnumerable<string> CandidateApiBases(Assembly beatLeader)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            string configured = null;
            try
            {
                var constants = beatLeader.GetType("BeatLeader.Utils.BLConstants", false);
                configured = (constants?.GetProperty("BEATLEADER_API_URL", flags)?.GetValue(null)
                    ?? constants?.GetField("BEATLEADER_API_URL", flags)?.GetValue(null)) as string;
                configured = configured?.TrimEnd('/');
            }
            catch
            {
                // Fall through to the known mirrors.
            }

            if (!string.IsNullOrEmpty(configured)) yield return configured;
            foreach (var known in KnownApiBases)
            {
                if (!string.Equals(known, configured, StringComparison.OrdinalIgnoreCase))
                    yield return known;
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
