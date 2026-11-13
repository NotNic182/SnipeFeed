using System;

namespace SnipeFeed.PC.Models
{
    // My Profile sort options, in dropdown order. SortBy values are
    // BeatLeader's ScoresSortBy enum for GET /player/{id}/scores.
    internal static class ProfileSort
    {
        internal sealed class Option
        {
            public readonly string Label;
            public readonly string SortBy;
            // pp and stars only exist on ranked maps; without type=ranked an
            // ascending sort is just a wall of unranked 0pp / no-star scores.
            public readonly bool RankedOnly;
            public readonly bool AscendingByDefault;
            public readonly string DescLabel;
            public readonly string AscLabel;

            public Option(string label, string sortBy, bool rankedOnly, bool ascendingByDefault, string descLabel, string ascLabel)
            {
                Label = label;
                SortBy = sortBy;
                RankedOnly = rankedOnly;
                AscendingByDefault = ascendingByDefault;
                DescLabel = descLabel;
                AscLabel = ascLabel;
            }
        }

        public static readonly Option[] Options =
        {
            new Option("Date", "date", false, false, "Newest first", "Oldest first"),
            new Option("PP", "pp", true, false, "Highest first", "Lowest first"),
            new Option("Acc PP", "accPP", true, false, "Highest first", "Lowest first"),
            new Option("Pass PP", "passPP", true, false, "Highest first", "Lowest first"),
            new Option("Tech PP", "techPP", true, false, "Highest first", "Lowest first"),
            new Option("Acc", "acc", false, false, "Highest first", "Lowest first"),
            new Option("Score", "scoreValue", false, false, "Highest first", "Lowest first"),
            new Option("Rank", "rank", false, true, "Worst first", "Best first"),
            new Option("Stars", "stars", true, false, "Highest first", "Lowest first"),
            new Option("Plays", "playCount", false, false, "Most first", "Fewest first"),
            new Option("Pauses", "pauses", false, false, "Most first", "Fewest first"),
            new Option("Streak", "maxStreak", false, false, "Longest first", "Shortest first"),
            new Option("Nominations", "sotwNominations", false, false, "Most first", "Fewest first"),
            new Option("Watched", "replaysWatched", false, false, "Most first", "Fewest first"),
            new Option("Mistakes", "mistakes", false, false, "Most first", "Fewest first"),
        };

        public const int Default = 0;

        public static Option Get(int index) => Options[Math.Max(0, Math.Min(Options.Length - 1, index))];

        public static string OrderLabel(int index, bool ascending)
        {
            var option = Get(index);
            return ascending ? option.AscLabel : option.DescLabel;
        }

        // Query for /player/{id}/scores, minus page/count.
        public static string Query(int index, bool ascending)
        {
            var option = Get(index);
            var query = "sortBy=" + option.SortBy + "&order=" + (ascending ? "asc" : "desc");
            if (option.RankedOnly) query += "&type=ranked";
            return query;
        }
    }
}
