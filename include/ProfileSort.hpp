#pragma once

#include <algorithm>
#include <iterator>
#include <string>

// My Profile sort options, in dropdown order. sortBy values are BeatLeader's
// ScoresSortBy enum for GET /player/{id}/scores. No game types in here.
namespace SnipeFeed::ProfileSort {

    struct Option {
        char const* label;
        char const* sortBy;
        // pp and stars only exist on ranked maps; without type=ranked an
        // ascending sort is just a wall of unranked 0pp / no-star scores.
        bool rankedOnly;
        bool ascendingByDefault;
        char const* descLabel;
        char const* ascLabel;
    };

    inline constexpr Option OPTIONS[] = {
        {"Date", "date", false, false, "Newest first", "Oldest first"},
        {"PP", "pp", true, false, "Highest first", "Lowest first"},
        {"Acc PP", "accPP", true, false, "Highest first", "Lowest first"},
        {"Pass PP", "passPP", true, false, "Highest first", "Lowest first"},
        {"Tech PP", "techPP", true, false, "Highest first", "Lowest first"},
        {"Acc", "acc", false, false, "Highest first", "Lowest first"},
        {"Score", "scoreValue", false, false, "Highest first", "Lowest first"},
        {"Rank", "rank", false, true, "Worst first", "Best first"},
        {"Stars", "stars", true, false, "Highest first", "Lowest first"},
        {"Plays", "playCount", false, false, "Most first", "Fewest first"},
        {"Pauses", "pauses", false, false, "Most first", "Fewest first"},
        {"Streak", "maxStreak", false, false, "Longest first", "Shortest first"},
        {"Nominations", "sotwNominations", false, false, "Most first", "Fewest first"},
        {"Watched", "replaysWatched", false, false, "Most first", "Fewest first"},
        {"Mistakes", "mistakes", false, false, "Most first", "Fewest first"},
    };

    inline constexpr int COUNT = static_cast<int>(std::size(OPTIONS));
    inline constexpr int DEFAULT = 0;

    inline Option const& Get(int index) {
        return OPTIONS[std::clamp(index, 0, COUNT - 1)];
    }

    inline char const* OrderLabel(int index, bool ascending) {
        auto const& option = Get(index);
        return ascending ? option.ascLabel : option.descLabel;
    }

    // Query for /player/{id}/scores, minus page/count.
    inline std::string Query(int index, bool ascending) {
        auto const& option = Get(index);
        std::string query = std::string("sortBy=") + option.sortBy + "&order=" + (ascending ? "asc" : "desc");
        if (option.rankedOnly) query += "&type=ranked";
        return query;
    }
}
