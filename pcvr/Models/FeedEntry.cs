using System.Collections.Generic;

namespace SnipeFeed.PC.Models
{
    public sealed class FeedEntry
    {
        public string PlayerName { get; set; } = "";
        public string PlayerId { get; set; } = "";
        public string SongName { get; set; } = "";
        public string SongAuthor { get; set; } = "";
        public string Mapper { get; set; } = "";
        public string SongHash { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string CoverUrl { get; set; } = "";
        public string Difficulty { get; set; } = "";
        public string Modifiers { get; set; } = "";
        public float Accuracy { get; set; }
        public float Pp { get; set; }
        public float Stars { get; set; }
        public long Timepost { get; set; }
        public bool FullCombo { get; set; }

        // What BeatLeader's Stars sort uses: speed-modifier stars times
        // 1 + the other modifiers' values (No Fail makes it 0).
        // StarModifiers lists the ones that changed it ("FS+SC").
        public float EffectiveStars { get; set; }
        public string StarModifiers { get; set; } = "";

        // HasRatings is separate because unrated maps send null, and 0s
        // would draw a made-up triangle.
        public float PassRating { get; set; }
        public float AccRating { get; set; }
        public float TechRating { get; set; }
        public bool HasRatings { get; set; }

        // -1 = not in the payload, so nothing shows a made-up 0.
        public float AccPp { get; set; } = -1f;
        public float PassPp { get; set; } = -1f;
        public float TechPp { get; set; } = -1f;
        public long Score { get; set; } = -1;
        public int Placement { get; set; } = -1;
        public int PlayCount { get; set; } = -1;
        public int Pauses { get; set; } = -1;
        public int MaxStreak { get; set; } = -1;
        public int Mistakes { get; set; } = -1;
        public int ReplaysWatched { get; set; } = -1;
        public int Nominations { get; set; } = -1;
    }

    public sealed class FeedResult
    {
        public bool Success { get; set; }
        public string Error { get; set; } = "";
        // Set only on Success with zero entries: a user-facing message such
        // as "no recent scores from the players you follow" that is NOT an
        // error and must not trigger fallback paths.
        public string Info { get; set; } = "";
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
    }

    public sealed class ProfileSummary
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string AvatarUrl { get; set; } = "";
        public string Country { get; set; } = "";
        public float Pp { get; set; }
        public float AverageRankedAccuracy { get; set; }
        public int Rank { get; set; }
        public int CountryRank { get; set; }
    }

    public sealed class ProfileResult
    {
        public bool Success { get; set; }
        // On Success with no entries this is an info message, not a failure.
        public string Error { get; set; } = "";
        public ProfileSummary Profile { get; set; } = new ProfileSummary();
        public List<FeedEntry> Entries { get; set; } = new List<FeedEntry>();
        public int TotalScores { get; set; }
    }
}
