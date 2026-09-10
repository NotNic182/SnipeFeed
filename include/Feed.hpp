#pragma once

#include <functional>
#include <string>
#include <vector>

namespace SnipeFeed {

    struct FeedEntry {
        std::string playerName;
        std::string playerId;
        std::string songName;
        std::string songAuthor;
        std::string mapper;
        std::string songHash;
        std::string avatarUrl;   // player avatar image URL (may be empty)
        std::string coverUrl;    // song cover art URL (may be empty)
        std::string difficulty;
        std::string modifiers;
        float accuracy = 0.0f;
        float pp = 0.0f;
        float stars = 0.0f;
        // BeatLeader's leaderboard.difficulty ratings. `hasRatings` is
        // separate because the API uses null for maps that have not been
        // graphed/rated; treating null as 0 would invent a triangle.
        float passRating = 0.0f;
        float accRating = 0.0f;
        float techRating = 0.0f;
        bool hasRatings = false;
        // API enum bitmasks (currently type: Acc=1, Tech=2, Midspeed=4,
        // Speed=8, Fitbeat=16, Linear=32, BombReset=64). The tag masks are
        // preserved as authoritative fallbacks when type is absent.
        int mapTypeMask = 0;
        int speedTags = 0;
        int styleTags = 0;
        // DifficultyStatus: 0 unranked, 1 nominated, 2 qualified, 3 ranked,
        // 4 unrankable, 5 outdated, 6 inevent, 7 OST; -1 means absent.
        int mapStatus = -1;
        long long timepost = 0;
        bool fullCombo = false;
    };

    // On success with ZERO entries, `error` carries a user-facing info
    // message ("you don't follow anyone yet") — it is not a failure and
    // callers must not treat it as one.
    struct FeedResult {
        bool success = false;
        std::string error;
        std::vector<FeedEntry> entries;
    };

    struct ProfileSummary {
        std::string id;
        std::string name;
        std::string avatarUrl;
        std::string country;
        float pp = 0.0f;
        float averageRankedAccuracy = 0.0f;
        float topPp = 0.0f;
        int rank = 0;
        int countryRank = 0;
        int totalPlayCount = 0;
        int rankedPlayCount = 0;
    };

    struct ProfileResult {
        bool success = false;
        std::string error;
        ProfileSummary profile;
        std::vector<FeedEntry> entries;
        int totalScores = 0;
    };

    // Fetches the feed on a detached worker thread. Tries the BeatLeader mod
    // login cookie first (real friends feed); falls back to the public API
    // using `playerInput` (numeric ID, alias, or pasted profile URL).
    // `feedCount` caps how many scores the feed holds on either path.
    // Callbacks are invoked FROM THE WORKER THREAD — marshal to the main
    // thread (BSML::MainThreadScheduler) before touching Unity objects.
    void FetchFeedAsync(
        std::string playerInput,
        int maxPlayers,
        int scoresPerPlayer,
        int feedCount,
        std::function<void(std::string)> onProgress,
        std::function<void(FeedResult)> onDone);

    // Loads the configured player's public profile and one bounded page of
    // scores. BeatLeader performs the requested ordering server-side.
    // Callbacks run on the worker thread, just like FetchFeedAsync.
    void FetchProfileAsync(
        std::string playerInput,
        std::string sortBy,
        std::string order,
        int count,
        std::function<void(std::string)> onProgress,
        std::function<void(ProfileResult)> onDone);
}
