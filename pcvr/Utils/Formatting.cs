using SnipeFeed.PC.Models;
using System;

namespace SnipeFeed.PC.Utils
{
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

        public static string TitleLine(FeedEntry e)
        {
            var line = e.SongName ?? "";
            if (!string.IsNullOrEmpty(e.SongAuthor)) line += " <color=#BBCCDD>- " + Escape(e.SongAuthor) + "</color>";
            if (!string.IsNullOrEmpty(e.Mapper)) line += " <size=80%><color=#8899AA>[" + Escape(e.Mapper) + "]</color></size>";
            if (!string.IsNullOrEmpty(e.Difficulty)) line += "  <size=70%><color=" + DiffColor(e.Difficulty) + ">" + DiffLabel(e.Difficulty) + "</color></size>";
            if (e.Stars > 0) line += "  <size=70%><color=#FFB921>" + e.Stars.ToString("0.0") + "★</color></size>";
            return line;
        }

        public static string SubLine(FeedEntry e)
        {
            var stats = "<b>" + Escape(e.PlayerName) + "</b>   " + FormatAcc(e.Accuracy);
            if (e.Pp > 0) stats += "  <color=#B856FF>" + e.Pp.ToString("0") + "<size=70%>pp</size></color>";
            if (e.FullCombo) stats += "  <color=#57FF8A>FC</color>";
            if (!string.IsNullOrEmpty(e.Modifiers)) stats += "  <color=#999999>+" + Escape(e.Modifiers) + "</color>";
            var ago = TimeAgo(e.Timepost);
            if (!string.IsNullOrEmpty(ago)) stats += "  <color=#8899AA>" + ago + "</color>";
            return stats;
        }

        public static string Detail(FeedEntry e)
        {
            var byline = e.SongAuthor ?? "";
            if (!string.IsNullOrEmpty(e.Mapper)) byline += (string.IsNullOrEmpty(byline) ? "[" : " [") + e.Mapper + "]";
            var detail = "<size=130%><b>" + Escape(e.SongName) + "</b></size>";
            if (!string.IsNullOrEmpty(byline)) detail += "\n<color=#8899AA>" + Escape(byline) + "</color>";
            detail += "\n" + SubLine(e);
            return detail;
        }

        private static string FormatAcc(float acc) => "<color=#FF8C29>" + (acc * 100f).ToString("0.00") + "%</color>";
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

        private static string Escape(string text) => (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    }
}
