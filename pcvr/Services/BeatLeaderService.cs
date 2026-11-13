using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
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
        private static readonly PlayerDto UnknownPlayer = new PlayerDto { name = "?" };

        private static HttpClient CreatePublicClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(Plugin.UserAgent);
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
                List<FeedEntry> friendEntries = null;
                var login = await GetWithLogin(
                    "/user/friendScores?sortBy=date&order=desc&page=1&count=" + feedCount,
                    body => (friendEntries = ParseScorePage(body, UnknownPlayer)) != null,
                    progress);
                if (login.Route != null)
                {
                    result.Entries = friendEntries.OrderByDescending(x => x.Timepost).Take(feedCount).ToList();
                    result.Success = true;
                    if (result.Entries.Count == 0)
                        result.Info = "No recent scores from the players you follow yet.\nFollow players on beatleader.com, then press Refresh.";
                    return result;
                }

                if (login.RateLimited)
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
                // Qualified: a bare "Formatting" is ambiguous here against
                // Newtonsoft.Json.Formatting, which this file also uses.
                result.Error = "Couldn't load the feed: " + Utils.Formatting.EscapeForTmp(ex.Message);
                return result;
            }
        }

        // Loads your profile (BeatLeader mod login first, configured PlayerId
        // as the fallback) and one page of scores. scoresQuery comes from
        // ProfileSort.Query; BeatLeader does the ordering server-side. When
        // the login found the profile, the scores request sends it too.
        public async Task<ProfileResult> FetchProfileAsync(string playerInput, string scoresQuery, int count, Action<string> progress = null)
        {
            var result = new ProfileResult();
            count = Math.Max(10, Math.Min(100, count));

            try
            {
                progress?.Invoke("Loading your BeatLeader profile...");
                ProfileSummary profile = null;
                var login = await GetWithLogin("/user/modinterface", body => (profile = ParseProfile(body)) != null, progress);
                var route = login.Route;

                if (route != null)
                {
                    // The login endpoint leaves out scoreStats (ranked avg acc
                    // showed 0.00%), so fill them in from the full profile;
                    // keep the login's answer if that fails.
                    if (profile.AverageRankedAccuracy <= 0f)
                    {
                        var (statsStatus, statsBody) = await SendWithLogin(route, "/player/" + Uri.EscapeDataString(profile.Id) + "?stats=true");
                        var full = statsStatus == 200 ? ParseProfile(statsBody) : null;
                        if (full != null && full.Id == profile.Id)
                            profile = full;
                    }
                }
                else
                {
                    var input = SanitizeInput(playerInput);
                    if (string.IsNullOrEmpty(input))
                    {
                        result.Error = "Sign into the BeatLeader mod, or set PlayerId in UserData/SnipeFeedPC.json.";
                        return result;
                    }

                    var (status, body) = await SendPublic(ApiBase + "/player/" + Uri.EscapeDataString(input) + "?stats=true");
                    if (status <= 0)
                    {
                        result.Error = "Network error. Check your connection.";
                        return result;
                    }
                    if (status == 404)
                    {
                        result.Error = "No BeatLeader player found for '" + input + "'.";
                        return result;
                    }
                    if (status == 429)
                    {
                        result.Error = "BeatLeader is rate-limiting requests. Wait a moment, then press Refresh.";
                        return result;
                    }
                    if (status != 200)
                    {
                        result.Error = "BeatLeader profile request returned HTTP " + status + ".";
                        return result;
                    }
                    profile = ParseProfile(body);
                    if (profile == null)
                    {
                        result.Error = "BeatLeader returned a profile without a player ID.";
                        return result;
                    }
                }
                result.Profile = profile;

                progress?.Invoke("Loading your scores...");
                var scoresPath = "/player/" + Uri.EscapeDataString(profile.Id) + "/scores?" + scoresQuery + "&page=1&count=" + count;
                var (scoresStatus, scoresBody) = route != null
                    ? await SendWithLogin(route, scoresPath)
                    : await SendPublic(ApiBase + scoresPath);
                if (scoresStatus <= 0)
                {
                    result.Error = "Profile loaded, but the scores request failed. Check your connection.";
                    return result;
                }
                if (scoresStatus == 429)
                {
                    result.Error = "BeatLeader is rate-limiting requests. Wait a moment, then press Refresh.";
                    return result;
                }
                if (scoresStatus != 200)
                {
                    result.Error = "BeatLeader scores request returned HTTP " + scoresStatus + ".";
                    return result;
                }

                ScorePageDto page;
                try
                {
                    page = JsonConvert.DeserializeObject<ScorePageDto>(scoresBody);
                }
                catch (Exception ex)
                {
                    Plugin.Log?.Warn("Couldn't parse the profile scores response: " + ex.Message);
                    page = null;
                }
                if (page == null)
                {
                    result.Error = "Unexpected BeatLeader scores response.";
                    return result;
                }
                if (page.data == null)
                {
                    result.Error = "BeatLeader scores response did not include a score list.";
                    return result;
                }

                var self = new PlayerDto { id = profile.Id, name = profile.Name, avatar = profile.AvatarUrl };
                result.Entries = page.data.Where(x => x != null).Select(x => ParseScore(x, self)).ToList();
                result.TotalScores = page.metadata?.total ?? 0;
                if (result.TotalScores <= 0)
                    result.TotalScores = result.Entries.Count;

                result.Success = true;
                if (result.Entries.Count == 0)
                    result.Error = "No scores found for this profile.";
                return result;
            }
            catch (TaskCanceledException)
            {
                result.Error = "BeatLeader request timed out. Check your connection and try again.";
                return result;
            }
            catch (Exception ex)
            {
                Plugin.Log?.Error("Profile refresh failed: " + ex);
                result.Error = "Couldn't load your profile: " + Utils.Formatting.EscapeForTmp(ex.Message);
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

        // How a request carries the BeatLeader mod's login: its session
        // cookies copied into our own client, or (Cookies null) Unity's
        // shared cookie cache.
        private sealed class LoginRoute
        {
            public string ApiBase;
            public CookieCollection Cookies;

            public override string ToString() => (Cookies != null ? "session cookies, " : "Unity cookie cache, ") + ApiBase;
        }

        // GETs pathAndQuery with the BeatLeader mod's login and returns the
        // route whose answer accept() took, so follow-up requests can go
        // out the same way. Route is null when no login worked.
        private static async Task<(LoginRoute Route, bool RateLimited)> GetWithLogin(string pathAndQuery, Func<string, bool> accept, Action<string> progress)
        {
            var beatLeader = FindBeatLeaderAssembly();
            if (beatLeader == null)
            {
                Plugin.Log?.Info("BeatLeader mod not found; using the public API fallback.");
                return (null, false);
            }

            if (!IsBeatLeaderSignedIn(beatLeader) && TryGetSessionRoute(beatLeader) == null)
            {
                if (_loginWaitExhausted)
                {
                    Plugin.Log?.Debug("Skipping the BeatLeader login wait (it already timed out this session).");
                }
                else
                {
                    progress?.Invoke("Waiting for the BeatLeader mod to sign in...");
                    await WaitForBeatLeaderLogin(beatLeader);
                    if (!IsBeatLeaderSignedIn(beatLeader) && TryGetSessionRoute(beatLeader) == null)
                        _loginWaitExhausted = true;
                }
            }

            var routes = new List<LoginRoute>();
            // Newer BeatLeader versions keep the login in a managed cookie
            // container we can copy read-only into our own client.
            var session = TryGetSessionRoute(beatLeader);
            if (session != null) routes.Add(session);

            // Older versions (0.9.x) sign in through UnityWebRequest, whose
            // engine-level cookie cache is shared process-wide — a
            // UnityWebRequest from us to the same server carries the login
            // automatically. Without a login the request just returns 401.
            // Only the server the mod is signed into is worth a request;
            // the mirrors are tried solely when the config can't be read.
            var configured = TryGetConfiguredApiBase(beatLeader);
            foreach (var apiBase in configured != null ? new[] { configured } : KnownApiBases)
                routes.Add(new LoginRoute { ApiBase = apiBase });

            var path = pathAndQuery.Split('?')[0];
            var rateLimited = false;
            foreach (var route in routes)
            {
                var (status, body) = await SendWithLogin(route, pathAndQuery);
                if (status == 200 && body != null && accept(body))
                {
                    Plugin.Log?.Info("Loaded " + path + " with the BeatLeader login (" + route + ").");
                    return (route, false);
                }
                if (status != 200)
                    Plugin.Log?.Info(path + " with the BeatLeader login (" + route + ") returned HTTP " + status + ".");
                rateLimited |= status == 429;
            }

            Plugin.Log?.Info(rateLimited
                ? "BeatLeader is rate-limiting requests; try again shortly."
                : "BeatLeader is installed but no reusable login was found; using the public API fallback.");
            return (null, rateLimited);
        }

        // Status 0 means the request never got an HTTP answer.
        private static Task<(long Status, string Body)> SendWithLogin(LoginRoute route, string pathAndQuery)
        {
            var url = route.ApiBase + pathAndQuery;
            return route.Cookies != null ? GetWithCookies(url, route.Cookies) : GetViaUnity(url);
        }

        private static async Task<(long Status, string Body)> GetWithCookies(string url, CookieCollection cookies)
        {
            try
            {
                // Copy the cookies read-only so we never mutate the BeatLeader
                // mod's own session state.
                var uri = new Uri(url);
                var target = new CookieContainer();
                foreach (Cookie cookie in cookies)
                {
                    var domain = string.IsNullOrEmpty(cookie.Domain) ? uri.Host : cookie.Domain;
                    target.Add(new Cookie(cookie.Name, cookie.Value, string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path, domain));
                }

                using (var handler = new HttpClientHandler { CookieContainer = target, UseCookies = true })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) })
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(Plugin.UserAgent);
                    using (var response = await client.GetAsync(uri))
                        return ((long)response.StatusCode, await response.Content.ReadAsStringAsync());
                }
            }
            catch (Exception ex)
            {
                // new Uri / Cookie.Add / GetAsync can all throw; a failure
                // here must fall through to the next route and the public
                // fallback, not abort the whole fetch.
                Plugin.Log?.Info("Request with the BeatLeader session cookies failed: " + ex.Message);
                return (0, null);
            }
        }

        private static async Task<(long Status, string Body)> GetViaUnity(string url)
        {
            try
            {
                using (var request = UnityWebRequest.Get(url))
                {
                    request.timeout = 15;
                    request.SetRequestHeader("User-Agent", Plugin.UserAgent);
                    var operation = request.SendWebRequest();
                    while (!operation.isDone) await Task.Yield();
                    return (request.responseCode, request.downloadHandler?.text);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.Info("Unity web request failed: " + ex.Message);
                return (0, null);
            }
        }

        private static async Task<(long Status, string Body)> SendPublic(string url)
        {
            try
            {
                using (var response = await PublicClient.GetAsync(url))
                    return ((long)response.StatusCode, await response.Content.ReadAsStringAsync());
            }
            catch (HttpRequestException ex)
            {
                Plugin.Log?.Info("BeatLeader request failed: " + ex.Message);
                return (0, null);
            }
        }

        private static List<FeedEntry> ParseScorePage(string body, PlayerDto fallback)
        {
            try
            {
                var page = JsonConvert.DeserializeObject<ScorePageDto>(body);
                if (page?.data == null) return null;

                // An empty list is a real, successful answer (the user follows
                // nobody, or nobody scored recently) — never fold it into null,
                // which callers read as "no usable login".
                return page.data.Where(x => x != null).Select(x => ParseScore(x, fallback)).ToList();
            }
            catch (Exception ex)
            {
                // A parse failure after a successful request must be loud —
                // it means the feed WAS fetched and then thrown away.
                Plugin.Log?.Warn("Couldn't parse the friends feed response: " + ex.Message);
                return null;
            }
        }

        // Null when the body has no player id.
        private static ProfileSummary ParseProfile(string body)
        {
            try
            {
                var json = JObject.Parse(body);
                // Tolerate a { player: {...} } wrapper as well as a bare player.
                var player = json["id"] != null ? json : json["player"] as JObject;
                var id = player?["id"]?.Type == JTokenType.String ? (string)player["id"] : null;
                if (string.IsNullOrEmpty(id)) return null;

                var stats = player["scoreStats"] as JObject;
                return new ProfileSummary
                {
                    Id = id,
                    Name = JsonString(player, "name"),
                    AvatarUrl = JsonString(player, "avatar"),
                    Country = JsonString(player, "country"),
                    Pp = (float)(JsonNumber(player, "pp") ?? 0),
                    Rank = (int)(JsonNumber(player, "rank") ?? 0),
                    CountryRank = (int)(JsonNumber(player, "countryRank") ?? 0),
                    AverageRankedAccuracy = stats != null ? (float)(JsonNumber(stats, "averageRankedAccuracy") ?? 0) : 0f,
                };
            }
            catch (Exception ex)
            {
                Plugin.Log?.Debug("Couldn't parse a BeatLeader profile: " + ex.Message);
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
                if (IsBeatLeaderSignedIn(beatLeader) || TryGetSessionRoute(beatLeader) != null) return;
                await Task.Delay(500);
            }
        }

        private static LoginRoute TryGetSessionRoute(Assembly beatLeader)
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
                        return new LoginRoute { ApiBase = apiBase, Cookies = cookies };
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

            var entry = new FeedEntry
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
                Timepost = timestamp,
                AccPp = OptionalFloat(score.accPP),
                PassPp = OptionalFloat(score.passPP),
                TechPp = OptionalFloat(score.techPP),
                Score = OptionalCount(score.modifiedScore),
                Placement = OptionalInt(score.rank),
                PlayCount = OptionalInt(score.playCount),
                Pauses = OptionalInt(score.pauses),
                MaxStreak = OptionalInt(score.maxStreak),
                ReplaysWatched = OptionalInt(score.replaysWatched),
                Nominations = OptionalInt(score.sotwNominations),
            };

            // Same definition BeatLeader sorts "mistakes" by.
            long mistakes = -1;
            foreach (var part in new[] { score.missedNotes, score.badCuts, score.bombCuts, score.wallsHit })
            {
                var count = OptionalCount(part);
                if (count >= 0) mistakes = Math.Max(mistakes, 0) + count;
            }
            entry.Mistakes = (int)Math.Min(mistakes, int.MaxValue);

            ApplyEffectiveStars(difficulty, entry);

            var passRating = OptionalFloat(difficulty.passRating);
            var accRating = OptionalFloat(difficulty.accRating);
            var techRating = OptionalFloat(difficulty.techRating);
            entry.HasRatings = passRating >= 0f && accRating >= 0f && techRating >= 0f
                && Math.Max(passRating, Math.Max(accRating, techRating)) > 0f;
            entry.PassRating = Math.Max(0f, passRating);
            entry.AccRating = Math.Max(0f, accRating);
            entry.TechRating = Math.Max(0f, techRating);
            return entry;
        }

        // Matches BeatLeader's Stars sort: the SF/FS/SS stars from
        // modifiersRating, times 1 + the other modifiers' modifierValues.
        private static void ApplyEffectiveStars(DifficultyDto difficulty, FeedEntry entry)
        {
            var stars = entry.Stars;
            var changed = new List<string>();
            var modifiers = entry.Modifiers.Split(',');

            string speedModifier = null;
            if (difficulty.modifiersRating != null)
            {
                foreach (var (modifier, key) in new[] { ("SF", "sfStars"), ("FS", "fsStars"), ("SS", "ssStars") })
                {
                    if (!modifiers.Contains(modifier)) continue;
                    var speedStars = OptionalFloat(JsonNumber(difficulty.modifiersRating, key));
                    if (speedStars > 0f)
                    {
                        stars = speedStars;
                        speedModifier = modifier;
                        changed.Add(modifier);
                    }
                    break;
                }
            }

            var factor = 1f;
            foreach (var modifier in modifiers)
            {
                if (modifier.Length == 0 || modifier == speedModifier) continue;
                double value = 0;
                if (difficulty.modifierValues != null)
                    value = JsonNumber(difficulty.modifierValues, modifier.ToLowerInvariant()) ?? 0;
                else if (modifier == "NF")
                    value = -1;
                if (value != 0 && !double.IsNaN(value) && !double.IsInfinity(value))
                {
                    factor += (float)value;
                    changed.Add(modifier);
                }
            }
            entry.EffectiveStars = Math.Max(0f, stars * factor);
            entry.StarModifiers = string.Join("+", changed);
        }

        // -1 when missing, negative or not a finite number.
        private static float OptionalFloat(double? value) =>
            value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0 ? (float)value.Value : -1f;

        private static long OptionalCount(double? value) =>
            value.HasValue && !double.IsNaN(value.Value) && !double.IsInfinity(value.Value) && value.Value >= 0 ? (long)value.Value : -1;

        private static int OptionalInt(double? value) => (int)Math.Min(OptionalCount(value), int.MaxValue);

        private static double? JsonNumber(JObject obj, string key)
        {
            var token = obj[key];
            return token != null && (token.Type == JTokenType.Float || token.Type == JTokenType.Integer) ? token.Value<double>() : (double?)null;
        }

        private static string JsonString(JObject obj, string key) =>
            obj[key]?.Type == JTokenType.String ? (string)obj[key] : "";

        private static string SanitizeInput(string input)
        {
            input = (input ?? "").Trim().TrimEnd('/');

            // A pasted profile URL can carry ?tab=... or #fragment — never
            // part of the id/alias.
            var cut = input.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) input = input.Substring(0, cut);

            var slash = input.LastIndexOf('/');
            if (slash >= 0) input = input.Substring(slash + 1);
            return input.Trim();
        }

        // Every value-typed field is nullable: BeatLeader sends null for
        // several of them (stars on unranked maps, pp, timepost, ...), and a
        // single null into a non-nullable field makes Newtonsoft throw away
        // the ENTIRE response.
        private sealed class ScorePageDto { public List<ScoreDto> data { get; set; } public MetadataDto metadata { get; set; } }
        private sealed class MetadataDto { public int? total { get; set; } }
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
            public double? accPP { get; set; }
            public double? passPP { get; set; }
            public double? techPP { get; set; }
            public double? modifiedScore { get; set; }
            public double? rank { get; set; }
            public double? playCount { get; set; }
            public double? pauses { get; set; }
            public double? maxStreak { get; set; }
            public double? replaysWatched { get; set; }
            public double? sotwNominations { get; set; }
            public double? missedNotes { get; set; }
            public double? badCuts { get; set; }
            public double? bombCuts { get; set; }
            public double? wallsHit { get; set; }
            public LeaderboardDto leaderboard { get; set; }
        }
        private sealed class LeaderboardDto { public SongDto song { get; set; } public DifficultyDto difficulty { get; set; } }
        private sealed class SongDto { public string name { get; set; } public string author { get; set; } public string mapper { get; set; } public string hash { get; set; } public string coverImage { get; set; } }
        private sealed class DifficultyDto
        {
            public string difficultyName { get; set; }
            public double? stars { get; set; }
            public double? passRating { get; set; }
            public double? accRating { get; set; }
            public double? techRating { get; set; }
            public JObject modifiersRating { get; set; }
            public JObject modifierValues { get; set; }
        }
    }
}
