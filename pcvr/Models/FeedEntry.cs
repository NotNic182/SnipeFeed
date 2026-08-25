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
}
