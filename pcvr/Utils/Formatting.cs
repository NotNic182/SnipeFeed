using SnipeFeed.PC.Models;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SnipeFeed.PC.Utils
{
    // Text formatting shared by the feed rows and the detail modal,
    // mirroring the Quest mod's Format.hpp. Difficulty colors match the
    // official BeatLeader mod; stat colors are fixed per stat (stars
    // yellow, accuracy orange, FC green) so rows scan consistently.
    internal static class Formatting
    {
        public static string TimeAgo(long timepost)
        {
            if (timepost <= 0) return "";
            var diff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timepost;
            if (diff < 0) diff = 0;
            if (diff < 60) return "just now";
            if (diff < 3600) return (diff / 60) + "m ago";
            if (diff < 86400) return (diff / 3600) + "h ago";
            return (diff / 86400) + "d ago";
        }

        // Top three ranks get medal-ish colors, the rest stay muted.
        public static string RankText(int rank)
        {
            string color;
            switch (rank)
            {
                case 1: color = "#FFB921"; break;
                case 2: color = "#C4CEDC"; break;
                case 3: color = "#D98E4A"; break;
                default: color = "#5A6B7A"; break;
            }
            return rank <= 3
                ? "<b><color=" + color + ">" + rank + "</color></b>"
                : "<color=" + color + ">" + rank + "</color>";
        }

        // The feed row's top line: "Song Name - Artist [mapper]" with small
        // colored difficulty and stars at the end.
        public static string TitleLine(FeedEntry e)
        {
            var line = EscapeForTmp(e.SongName);
            if (!string.IsNullOrEmpty(e.SongAuthor)) line += " <color=#BBCCDD>- " + EscapeForTmp(e.SongAuthor) + "</color>";
            if (!string.IsNullOrEmpty(e.Mapper)) line += " <size=80%><color=#8899AA>[" + EscapeForTmp(e.Mapper) + "]</color></size>";
            line += DiffAndStars(e, "70%");
            return line;
        }

        public static string PlayerLine(FeedEntry e) => "<b>" + EscapeForTmp(e.PlayerName) + "</b>";

        public static string StatsLine(FeedEntry e)
        {
            var line = FormatAcc(e.Accuracy);
            if (e.Pp > 0) line += "  <color=#B856FF>" + e.Pp.ToString("0", CultureInfo.InvariantCulture) + "<size=70%>pp</size></color>";
            if (e.FullCombo) line += "  <color=#57FF8A>FC</color>";
            if (!string.IsNullOrEmpty(e.Modifiers)) line += "  <color=#999999>+" + EscapeForTmp(e.Modifiers) + "</color>";
            return line;
        }

        // The detail modal's text block: big song title, muted byline,
        // difficulty/stars, stats, then how long ago the score was set.
        public static string DetailText(FeedEntry e)
        {
            var info = "<size=140%><b>" + EscapeForTmp(e.SongName) + "</b></size>";

            if (!string.IsNullOrEmpty(e.SongAuthor) || !string.IsNullOrEmpty(e.Mapper))
            {
                var byline = EscapeForTmp(e.SongAuthor);
                if (!string.IsNullOrEmpty(e.Mapper))
                    byline += (string.IsNullOrEmpty(byline) ? "[" : " [") + EscapeForTmp(e.Mapper) + "]";
                info += "\n<color=#888888>" + byline + "</color>";
            }

            var diffLine = DiffAndStars(e, "75%");
            if (!string.IsNullOrEmpty(diffLine))
                info += "\n<size=85%>" + diffLine.TrimStart() + "</size>";

            info += "\n" + StatsLine(e);

            var extra = ExtraStatsLine(e);
            if (!string.IsNullOrEmpty(extra))
                info += "\n" + extra;

            var ago = TimeAgo(e.Timepost);
            if (!string.IsNullOrEmpty(ago))
                info += "\n<size=75%><color=#777777>" + ago + "</color></size>";
            return info;
        }

        // What a profile row is sorted by, when the row doesn't already show
        // it (date, pp, acc and stars are always on the row). Empty if the
        // score didn't come with that stat.
        public static string SortValue(FeedEntry e, string sortBy)
        {
            string text = null;
            switch (sortBy)
            {
                case "accPP": if (e.AccPp >= 0f) text = Whole(e.AccPp) + "pp acc"; break;
                case "passPP": if (e.PassPp >= 0f) text = Whole(e.PassPp) + "pp pass"; break;
                case "techPP": if (e.TechPp >= 0f) text = Whole(e.TechPp) + "pp tech"; break;
                case "scoreValue": if (e.Score >= 0) text = Thousands(e.Score); break;
                case "rank": if (e.Placement > 0) text = "#" + Thousands(e.Placement); break;
                case "playCount": if (e.PlayCount >= 0) text = Count(e.PlayCount, "play", "plays"); break;
                case "pauses": if (e.Pauses >= 0) text = Count(e.Pauses, "pause", "pauses"); break;
                case "maxStreak": if (e.MaxStreak >= 0) text = "streak " + Thousands(e.MaxStreak); break;
                case "sotwNominations": if (e.Nominations >= 0) text = Count(e.Nominations, "nomination", "nominations"); break;
                case "replaysWatched": if (e.ReplaysWatched >= 0) text = Thousands(e.ReplaysWatched) + " watched"; break;
                case "mistakes": if (e.Mistakes >= 0) text = Count(e.Mistakes, "mistake", "mistakes"); break;
                // The title shows the map's stars; BeatLeader sorts by the
                // modifier-adjusted value, so show that when modifiers moved it.
                case "stars":
                    if (!string.IsNullOrEmpty(e.StarModifiers))
                        text = e.EffectiveStars.ToString("0.00", CultureInfo.InvariantCulture) + "★ with " + EscapeForTmp(e.StarModifiers);
                    break;
            }
            return string.IsNullOrEmpty(text) ? "" : "<color=#FFD27A>" + text + "</color>";
        }

        // One short line of extra score stats for the detail modal. Zero
        // pauses, plays, views and nominations are left out to keep it short.
        public static string ExtraStatsLine(FeedEntry e)
        {
            var parts = new List<string>();
            if (e.Placement > 0) parts.Add("#" + Thousands(e.Placement) + " on map");
            if (e.Mistakes >= 0) parts.Add(Count(e.Mistakes, "mistake", "mistakes"));
            if (e.Pauses > 0) parts.Add(Count(e.Pauses, "pause", "pauses"));
            if (e.MaxStreak > 0) parts.Add("streak " + Thousands(e.MaxStreak));
            if (e.PlayCount > 0) parts.Add(Count(e.PlayCount, "play", "plays"));
            if (e.ReplaysWatched > 0) parts.Add(Thousands(e.ReplaysWatched) + " watched");
            if (e.Nominations > 0) parts.Add(Count(e.Nominations, "nomination", "nominations"));
            if (parts.Count == 0) return "";
            return "<size=70%><color=#AABBCC>" + string.Join(" • ", parts) + "</color></size>";
        }

        public static string Thousands(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

        private static string Count(long n, string singular, string plural) => Thousands(n) + " " + (n == 1 ? singular : plural);

        private static string Whole(float value) => value.ToString("0", CultureInfo.InvariantCulture);

        private static string DiffAndStars(FeedEntry e, string size)
        {
            var line = "";
            if (!string.IsNullOrEmpty(e.Difficulty))
                line += "  <size=" + size + "><color=" + DiffColor(e.Difficulty) + ">" + DiffLabel(e.Difficulty) + "</color></size>";
            if (e.Stars > 0)
                line += "  <size=" + size + "><color=#FFB921>" + e.Stars.ToString("0.0", CultureInfo.InvariantCulture) + "★</color></size>";
            return line;
        }

        private static string FormatAcc(float acc) => "<color=#FF8C29>" + (acc * 100f).ToString("0.00", CultureInfo.InvariantCulture) + "%</color>";

        private static string DiffLabel(string diff) => diff == "ExpertPlus" ? "Expert+" : diff;

        private static string DiffColor(string diff)
        {
            switch (diff)
            {
                case "Easy": return "#3cb371";
                case "Normal": return "#59b0f4";
                case "Hard": return "#ff6347";
                case "Expert": return "#bf2a42";
                case "ExpertPlus": return "#8f48db";
                default: return "#bbbbbb";
            }
        }

        // TMP does not decode HTML entities — "&amp;" renders literally — so
        // entity escaping is wrong here. Instead neutralize markup: a
        // zero-width space directly after every '<' keeps the character
        // visible while making it impossible to open a tag.
        public static string EscapeForTmp(string text) => (text ?? "").Replace("<", "<​");
    }
}
