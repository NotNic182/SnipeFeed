using SnipeFeed.PC.Models;
using System;
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

            var ago = TimeAgo(e.Timepost);
            if (!string.IsNullOrEmpty(ago))
                info += "\n<size=75%><color=#777777>" + ago + "</color></size>";
            return info;
        }

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
