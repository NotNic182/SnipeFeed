#include "Feed.hpp"
#include "Web.hpp"
#include "main.hpp"

#include "beatsaber-hook/shared/config/rapidjson-utils.hpp"
#include "beatsaber-hook/shared/config/config-utils.hpp"

#include <algorithm>
#include <atomic>
#include <filesystem>
#include <mutex>
#include <thread>

namespace SnipeFeed {

    static constexpr auto API_URL = "https://api.beatleader.com";
    static constexpr long TIMEOUT_SECONDS = 15;

    struct FollowedPlayer {
        std::string id;
        std::string name;
        std::string avatar;
    };

    static std::string GetString(rapidjson::Value const& obj, char const* key) {
        auto it = obj.FindMember(key);
        if (it != obj.MemberEnd() && it->value.IsString())
            return it->value.GetString();
        return "";
    }

    static double GetNumber(rapidjson::Value const& obj, char const* key) {
        auto it = obj.FindMember(key);
        if (it != obj.MemberEnd() && it->value.IsNumber())
            return it->value.GetDouble();
        return 0.0;
    }

    // BeatLeader reports score time as "timepost" (number) with a legacy
    // "timeset" (string of a unix timestamp) fallback.
    static long long GetScoreTime(rapidjson::Value const& score) {
        auto post = score.FindMember("timepost");
        if (post != score.MemberEnd() && post->value.IsNumber())
            return static_cast<long long>(post->value.GetDouble());
        auto set = score.FindMember("timeset");
        if (set != score.MemberEnd() && set->value.IsString()) {
            try {
                return std::stoll(set->value.GetString());
            } catch (...) {}
        }
        return 0;
    }

    // Parses one score object (shape shared by /player/{id}/scores and
    // /user/friendScores). Player identity comes from the score's own
    // "player" object when present, otherwise from the fallback.
    static void ParseScore(rapidjson::Value const& score, FollowedPlayer const& fallbackPlayer, std::vector<FeedEntry>& outEntries) {
        FeedEntry entry;
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

        entry.accuracy = static_cast<float>(GetNumber(score, "accuracy"));
        entry.pp = static_cast<float>(GetNumber(score, "pp"));
        entry.modifiers = GetString(score, "modifiers");
        entry.timepost = GetScoreTime(score);
        entry.fullCombo = score.HasMember("fullCombo") && score["fullCombo"].IsBool() && score["fullCombo"].GetBool();

        auto lb = score.FindMember("leaderboard");
        if (lb != score.MemberEnd() && lb->value.IsObject()) {
            auto song = lb->value.FindMember("song");
            if (song != lb->value.MemberEnd() && song->value.IsObject()) {
                entry.songName = GetString(song->value, "name");
                entry.songAuthor = GetString(song->value, "author");
                entry.mapper = GetString(song->value, "mapper");
                entry.songHash = GetString(song->value, "hash");
                entry.coverUrl = GetString(song->value, "coverImage");
            }
            auto diff = lb->value.FindMember("difficulty");
            if (diff != lb->value.MemberEnd() && diff->value.IsObject()) {
                entry.difficulty = GetString(diff->value, "difficultyName");
                entry.stars = static_cast<float>(GetNumber(diff->value, "stars"));
            }
        }
        outEntries.push_back(std::move(entry));
    }

    // Cleans user input: trims whitespace, and if a profile URL was pasted,
    // keeps only the last path segment (the id or alias).
    static std::string SanitizeInput(std::string input) {
        auto notSpace = [](unsigned char c) { return !std::isspace(c); };
        input.erase(input.begin(), std::find_if(input.begin(), input.end(), notSpace));
        input.erase(std::find_if(input.rbegin(), input.rend(), notSpace).base(), input.end());
        // A pasted profile URL can carry ?tab=... or #fragment — those are
        // never part of the id/alias.
        auto cut = input.find_first_of("?#");
        if (cut != std::string::npos)
            input = input.substr(0, cut);
        while (!input.empty() && input.back() == '/')
            input.pop_back();
        auto slash = input.find_last_of('/');
        if (slash != std::string::npos)
            input = input.substr(slash + 1);
        return input;
    }

    static bool IsNumeric(std::string const& s) {
        return !s.empty() && std::all_of(s.begin(), s.end(), [](unsigned char c) { return c >= '0' && c <= '9'; });
    }

    // The path where the official BeatLeader Quest mod (MOD_ID "bl") keeps its
    // login cookies. Both mods run inside the same game process, so if the
    // player is logged in there we can reuse the session read-only.
    static std::string BeatLeaderCookieFile() {
        return getDataDir("bl") + "cookies/cookies.txt";
    }

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

    // Resolves an alias (e.g. "nic") or pasted URL to a numeric player ID via
    // GET /player/{aliasOrId}, which the server resolves either way.
    static bool ResolvePlayer(std::string const& aliasOrId, FollowedPlayer& outPlayer, std::string& outError) {
        std::string url = std::string(API_URL) + "/player/" + aliasOrId;
        std::string body;
        long code = Web::Get(url, TIMEOUT_SECONDS, body);
        if (code < 0) {
            outError = "Network error. Check your connection.";
            return false;
        }
        if (code == 404) {
            outError = "No BeatLeader player found for '" + aliasOrId + "'.";
            return false;
        }
        if (code != 200) {
            outError = "BeatLeader returned HTTP " + std::to_string(code) + ".";
            return false;
        }
        rapidjson::Document doc;
        doc.Parse(body);
        if (doc.HasParseError() || !doc.IsObject()) {
            outError = "Unexpected response while resolving the player.";
            return false;
        }
        outPlayer.id = GetString(doc, "id");
        outPlayer.name = GetString(doc, "name");
        if (outPlayer.id.empty()) {
            outError = "Could not resolve '" + aliasOrId + "' to a player ID.";
            return false;
        }
        return true;
    }

    static bool FetchFollowing(std::string const& playerId, int maxPlayers, std::vector<FollowedPlayer>& outPlayers, std::string& outError) {
        std::string url = std::string(API_URL) + "/player/" + playerId + "/followers?page=1&count=" + std::to_string(maxPlayers) + "&type=following";
        std::string body;
        long code = Web::Get(url, TIMEOUT_SECONDS, body);
        if (code < 0) {
            outError = "Network error. Check your connection.";
            return false;
        }
        if (code != 200) {
            outError = "BeatLeader returned HTTP " + std::to_string(code) + ".";
            return false;
        }

        rapidjson::Document doc;
        doc.Parse(body);
        if (doc.HasParseError() || !doc.IsArray()) {
            outError = "Unexpected response for the following list.";
            return false;
        }

        for (auto const& p : doc.GetArray()) {
            if (!p.IsObject()) continue;
            FollowedPlayer fp;
            fp.id = GetString(p, "id");
            fp.name = GetString(p, "name");
            fp.avatar = GetString(p, "avatar");
            if (!fp.id.empty())
                outPlayers.push_back(std::move(fp));
        }

        if (outPlayers.empty()) {
            // BeatLeader hides a profile's following list from anonymous
            // callers when "hide friends" is enabled, so this is the most
            // common reason for an empty result.
            outError = "Your following list is empty or hidden.\nEither log in inside the BeatLeader mod on this headset, or disable 'Hide my friends' in your beatleader.com profile settings.";
            return false;
        }
        return true;
    }

    static void FetchRecentScores(FollowedPlayer const& player, int scoresPerPlayer, std::vector<FeedEntry>& outEntries) {
        std::string url = std::string(API_URL) + "/player/" + player.id + "/scores?sortBy=date&order=desc&page=1&count=" + std::to_string(scoresPerPlayer);
        std::string body;
        long code = Web::Get(url, TIMEOUT_SECONDS, body);
        if (code != 200) {
            // One player failing (hidden profile, transient error) should not
            // kill the whole feed.
            SnipeFeedLogger.warn("Scores request for {} returned {}", player.id, code);
            return;
        }

        rapidjson::Document doc;
        doc.Parse(body);
        if (doc.HasParseError() || !doc.IsObject()) {
            SnipeFeedLogger.warn("Bad scores JSON for player {}", player.id);
            return;
        }
        auto data = doc.FindMember("data");
        if (data == doc.MemberEnd() || !data->value.IsArray()) return;

        for (auto const& score : data->value.GetArray()) {
            if (score.IsObject())
                ParseScore(score, player, outEntries);
        }
    }

    void FetchFeedAsync(
        std::string playerInput,
        int maxPlayers,
        int scoresPerPlayer,
        int feedCount,
        std::function<void(std::string)> onProgress,
        std::function<void(FeedResult)> onDone) {

        // 100 is the largest page BeatLeader serves per request.
        feedCount = std::clamp(feedCount, 10, 100);

        std::thread worker([playerInput = std::move(playerInput), maxPlayers, scoresPerPlayer, feedCount,
                            onProgress = std::move(onProgress), onDone = std::move(onDone)] {
            // onDone must fire exactly once; if the callback itself
            // throws, the catch below must not fire it again.
            bool doneCalled = false;
            auto finish = [&](FeedResult r) {
                doneCalled = true;
                onDone(std::move(r));
            };

            try {

                FeedResult result;

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
                            finish(std::move(result));
                            return;
                        case FriendFeedOutcome::Empty:
                            result.success = true;
                            result.error = "No recent scores from the players you follow yet.\nFollow players on beatleader.com, then press Refresh.";
                            finish(std::move(result));
                            return;
                        case FriendFeedOutcome::NetworkError:
                            // The public path rides the same network — falling
                            // through would waste 15s and blame the login.
                            result.error = "Network error. Check your connection.";
                            finish(std::move(result));
                            return;
                        case FriendFeedOutcome::Unavailable:
                            result.entries.clear();
                            break;
                    }
                }

                // Path 2: public API using the configured ID or alias.
                std::string input = SanitizeInput(playerInput);
                if (input.empty()) {
                    result.error = "Couldn't use a BeatLeader mod login on this headset.\nLog into the BeatLeader mod, then press Refresh.\n(Or set PlayerId in SnipeFeed's config file to use the public API.)";
                    finish(std::move(result));
                    return;
                }

                FollowedPlayer self{input, input, ""};
                if (!IsNumeric(input)) {
                    if (onProgress) onProgress("Resolving '" + input + "'...");
                    if (!ResolvePlayer(input, self, result.error)) {
                        finish(std::move(result));
                        return;
                    }
                }

                if (onProgress) onProgress("Loading players you follow...");
                std::vector<FollowedPlayer> following;
                if (!FetchFollowing(self.id, maxPlayers, following, result.error)) {
                    finish(std::move(result));
                    return;
                }

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
                            try {
                                FetchRecentScores(following[i], scoresPerPlayer, local);
                            } catch (std::exception const& e) {
                                SnipeFeedLogger.warn("Score fetch for {} crashed: {}", following[i].id, e.what());
                            }
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

                std::sort(result.entries.begin(), result.entries.end(), [](FeedEntry const& a, FeedEntry const& b) {
                    return a.timepost > b.timepost;
                });
                if (result.entries.size() > static_cast<size_t>(feedCount))
                    result.entries.resize(feedCount);

                if (result.entries.empty()) {
                    result.error = "No recent scores found for the players you follow.";
                } else {
                    result.success = true;
                }
                finish(std::move(result));
            } catch (std::exception const& e) {
                // An exception escaping a detached thread is std::terminate —
                // a whole-game crash. Turn it into a feed error instead.
                SnipeFeedLogger.error("Feed fetch crashed: {}", e.what());
                if (!doneCalled) {
                    FeedResult crashResult;
                    crashResult.error = "Something went wrong loading the feed. Press Refresh to try again.";
                    onDone(std::move(crashResult));
                }
            }
        });
        worker.detach();
    }
}
