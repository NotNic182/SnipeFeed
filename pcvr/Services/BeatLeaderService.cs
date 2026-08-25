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
                var (friendEntries, rateLimited) = await TryFetchAuthenticatedFriendScores(feedCount, progress);
                if (friendEntries != null)
                {
                    result.Entries = friendEntries.OrderByDescending(x => x.Timepost).Take(feedCount).ToList();
                    result.Success = true;
                    if (result.Entries.Count == 0)
                        result.Info = "No recent scores from the players you follow yet.\nFollow players on beatleader.com, then press Refresh.";
                    return result;
                }

                if (rateLimited)
                {
                    // The public fallback would hit the same rate limit;
                    // don't turn one clear condition into a confusing error.
                    result.Error = "BeatLeader is rate-limiting requests right now.\nWait a minute, then press Refresh.";
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

                // The Scores stepper promises up to feedCount results; with a
                // short following list the configured per-player count could
                // never reach it. Pull enough per player, within API limits.
                var neededPerPlayer = (int)Math.Ceiling((double)feedCount / following.Count);
                scoresPerPlayer = Math.Max(scoresPerPlayer, Math.Min(neededPerPlayer, 20));

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

        // Remembers that a full login wait already timed out once this
        // session, so a BeatLeader install with no signed-in account doesn't
        // stall every refresh 8 seconds for the rest of the session.
        private static bool _loginWaitExhausted;

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

        private static async Task<(List<FeedEntry> Entries, bool RateLimited)> TryFetchAuthenticatedFriendScores(int count, Action<string> progress)
        {
            var beatLeader = FindBeatLeaderAssembly();
            if (beatLeader == null)
            {
                Plugin.Log?.Info("BeatLeader mod not found; using the public API fallback.");
                return (null, false);
            }

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

            var rateLimited = false;

            // Newer BeatLeader versions keep the login in a managed cookie
            // container we can copy read-only into our own client.
            var session = TryGetBeatLeaderSession(beatLeader);
            if (session != null)
            {
                var (viaCookies, cookieStatus) = await FetchFriendScoresWithCookies(session, count);
                if (viaCookies != null)
                {
                    Plugin.Log?.Info("Loaded the friends feed with the BeatLeader session cookies (" + session.ApiBase + ").");
                    return (viaCookies, false);
                }
                rateLimited |= cookieStatus == 429;
            }

            // Older versions (0.9.x) sign in through UnityWebRequest, whose
            // engine-level cookie cache is shared process-wide — a
            // UnityWebRequest from us to the same server carries the login
            // automatically. Without a login the request just returns 401.
            // Only the server the mod is signed into is worth a request;
            // the mirrors are tried solely when the config can't be read.
            var configured = TryGetConfiguredApiBase(beatLeader);
            var unityBases = configured != null ? new[] { configured } : KnownApiBases;
            foreach (var apiBase in unityBases)
            {
                var (viaUnity, unityStatus) = await FetchFriendScoresViaUnity(apiBase, count);
                if (viaUnity != null)
                {
                    Plugin.Log?.Info("Loaded the friends feed through the Unity cookie cache (" + apiBase + ").");
                    return (viaUnity, false);
                }
                rateLimited |= unityStatus == 429;
            }

            Plugin.Log?.Info(rateLimited
                ? "BeatLeader is rate-limiting requests; try again shortly."
                : "BeatLeader is installed but no reusable login was found; using the public API fallback.");
            return (null, rateLimited);
        }

        private static async Task<(List<FeedEntry> Entries, long Status)> FetchFriendScoresWithCookies(BeatLeaderSession session, int count)
        {
            try
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
                        return (null, (long)response.StatusCode);
                    }

                    return (ParseFriendScores(await response.Content.ReadAsStringAsync()), 200);
                }
            }
            catch (Exception ex)
            {
                // new Uri / Cookie.Add / GetAsync can all throw; a failure
                // here must fall through to the Unity path and the public
                // fallback, not abort the whole fetch.
                Plugin.Log?.Info("friendScores via the BeatLeader session cookies failed: " + ex.Message);
                return (null, 0);
            }
        }

        private static async Task<(List<FeedEntry> Entries, long Status)> FetchFriendScoresViaUnity(string apiBase, int count)
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
                        return (null, request.responseCode);
                    }

                    return (ParseFriendScores(request.downloadHandler.text), 200);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Info("Unity web request for friendScores failed: " + ex.Message);
                return (null, 0);
            }
        }

        private static string FriendScoresUrl(string apiBase, int count) =>
            apiBase + "/user/friendScores?sortBy=date&order=desc&page=1&count=" + count;

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
                    // Observe the login task's eventual fault so a failed
                    // login can't surface later as UnobservedTaskException.
                    _ = loginTask.ContinueWith(t => _ = t.Exception,
                        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
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

        // The server BeatLeader is actually signed into, from its own
        // BLConstants (a property in current versions, a const field in old
        // ones). Null when the reflection doesn't line up.
        private static string TryGetConfiguredApiBase(Assembly beatLeader)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            try
            {
                var constants = beatLeader.GetType("BeatLeader.Utils.BLConstants", false);
                var configured = (constants?.GetProperty("BEATLEADER_API_URL", flags)?.GetValue(null)
                    ?? constants?.GetField("BEATLEADER_API_URL", flags)?.GetValue(null)) as string;
                configured = configured?.TrimEnd('/');
                return string.IsNullOrEmpty(configured) ? null : configured;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> CandidateApiBases(Assembly beatLeader)
        {
            var configured = TryGetConfiguredApiBase(beatLeader);
            if (configured != null) yield return configured;
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
            if ((score.timepost ?? 0) > 0)
                timestamp = (long)score.timepost.Value;
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
                Stars = (float)(difficulty.stars ?? 0),
                Accuracy = (float)(score.accuracy ?? 0),
                Pp = (float)(score.pp ?? 0),
                Modifiers = score.modifiers ?? "",
                FullCombo = score.fullCombo ?? false,
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

        // Every value-typed field is nullable: BeatLeader sends null for
        // several of them (stars on unranked maps, pp, timepost, ...), and a
        // single null into a non-nullable field makes Newtonsoft throw away
        // the ENTIRE response.
        private sealed class ScorePageDto { public List<ScoreDto> data { get; set; } }
        private sealed class PlayerDto { public string id { get; set; } public string name { get; set; } public string avatar { get; set; } }
        private sealed class ScoreDto
        {
            public PlayerDto player { get; set; }
            public double? accuracy { get; set; }
            public double? pp { get; set; }
            public string modifiers { get; set; }
            public double? timepost { get; set; }
            public string timeset { get; set; }
            public bool? fullCombo { get; set; }
            public LeaderboardDto leaderboard { get; set; }
        }
        private sealed class LeaderboardDto { public SongDto song { get; set; } public DifficultyDto difficulty { get; set; } }
        private sealed class SongDto { public string name { get; set; } public string author { get; set; } public string mapper { get; set; } public string hash { get; set; } public string coverImage { get; set; } }
        private sealed class DifficultyDto { public string difficultyName { get; set; } public double? stars { get; set; } }
    }
}
