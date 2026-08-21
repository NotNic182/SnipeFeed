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
        std::string songHash;
        std::string difficulty;
        std::string modifiers;
        float accuracy = 0.0f;
        float pp = 0.0f;
        float stars = 0.0f;
        long long timepost = 0;
        bool fullCombo = false;
    };

    struct FeedResult {
        bool success = false;
        std::string error;
        std::vector<FeedEntry> entries;
    };

    // Fetches the feed on a detached worker thread. Tries the BeatLeader mod
    // login cookie first (real friends feed); falls back to the public API
    // using `playerInput` (numeric ID, alias, or pasted profile URL).
    // Callbacks are invoked FROM THE WORKER THREAD — marshal to the main
    // thread (BSML::MainThreadScheduler) before touching Unity objects.
    void FetchFeedAsync(
        std::string playerInput,
        int maxPlayers,
        int scoresPerPlayer,
        std::function<void(std::string)> onProgress,
        std::function<void(FeedResult)> onDone);
}
