#pragma once

#include "Feed.hpp"

#include <algorithm>
#include <cmath>
#include <ctime>
#include <format>
#include <initializer_list>
#include <string>
#include <utility>

// Text formatting shared by the feed cells and the detail modal.
// Difficulty colors match the official BeatLeader mod; stat colors are
// fixed per stat (stars yellow, accuracy orange, FC green) so rows scan
// consistently.
namespace SnipeFeed::Format {

    // TMP parses '<' as markup, and feed strings (song names like "<3",
    // player names) must not be able to open tags. A zero-width space
    // (U+200B) directly after every '<' keeps the character visible while
    // breaking tag parsing. TMP does NOT decode HTML entities, so
    // &lt;-style escaping would render literally.
    inline std::string Escape(std::string const& text) {
        std::string out;
        out.reserve(text.size());
        for (char c : text) {
            out += c;
            if (c == '<') out += "\xE2\x80\x8B";
        }
        return out;
    }

    inline std::string TimeAgo(long long timepost) {
        if (timepost <= 0) return "";
        long long diff = static_cast<long long>(std::time(nullptr)) - timepost;
        if (diff < 0) diff = 0;
        if (diff < 60) return "just now";
        if (diff < 3600) return std::to_string(diff / 60) + "m ago";
        if (diff < 86400) return std::to_string(diff / 3600) + "h ago";
        return std::to_string(diff / 86400) + "d ago";
    }

    inline char const* DiffColor(std::string const& diff) {
        if (diff == "Easy") return "#3cb371";
        if (diff == "Normal") return "#59b0f4";
        if (diff == "Hard") return "#ff6347";
        if (diff == "Expert") return "#bf2a42";
        if (diff == "ExpertPlus") return "#8f48db";
        return "#bbbbbb";
    }

    inline std::string DiffLabel(std::string const& diff) {
        return diff == "ExpertPlus" ? "Expert+" : diff;
    }

    // BeatLeader publishes these as bitmasks. Keep the labels deterministic
    // and bounded so a multi-tag map cannot consume the score row.
    inline std::string MaskLabel(int mask, std::initializer_list<std::pair<int, char const*>> labels) {
        std::string result;
        int shown = 0;
        int total = 0;
        for (auto const& [bit, label] : labels) {
            if ((mask & bit) == 0) continue;
            total++;
            if (shown >= 2) continue;
            if (!result.empty()) result += " + ";
            result += label;
            shown++;
        }
        if (total > shown) result += " +";
        return result;
    }

    inline std::string MapStyleLabel(FeedEntry const& e) {
        auto type = MaskLabel(e.mapTypeMask, {
            {1, "Acc"}, {2, "Tech"}, {4, "Midspeed"}, {8, "Speed"},
            {16, "Fitbeat"}, {32, "Linear"}, {64, "Bomb Avoid."},
        });
        if (!type.empty()) return type;

        // The detailed tags are server-authored map metadata, not an
        // inference. Prefer style over raw speed when both are present.
        auto style = MaskLabel(e.styleTags, {
            {2, "True Acc"}, {4, "Standard Acc"}, {8, "Tech Acc"},
            {16, "Tech"}, {1, "Linear"}, {32, "Reset"},
            {64, "Bomb Reset"}, {128, "Balanced"}, {256, "Gimmick"},
            {512, "Fitbeat"}, {1024, "Dance"}, {2048, "Challenge"},
            {4096, "Jump"}, {8192, "Stream"}, {16384, "Stamina"},
            {32768, "Paul"}, {65536, "Poodle"},
        });
        if (!style.empty()) return style;

        return MaskLabel(e.speedTags, {
            {1, "Slow"}, {2, "Medium"}, {4, "Fast"},
            {8, "Extreme"}, {16, "Insane"},
        });
    }

    inline char const* MapStatusLabel(int status) {
        switch (status) {
            case 0: return "Unranked";
            case 1: return "Nominated";
            case 2: return "Qualified";
            case 3: return "Ranked";
            case 4: return "Unrankable";
            case 5: return "Outdated";
            case 6: return "In event";
            case 7: return "OST";
            default: return "Status unavailable";
        }
    }

    inline std::string MapStyleLine(FeedEntry const& e) {
        std::string style = MapStyleLabel(e);
        std::string line = "<color=#8899AA>" + std::string(MapStatusLabel(e.mapStatus)) + "</color>";
        if (!style.empty()) line += "  <color=#D7E5F3>" + style + "</color>";
        else line += "  <color=#778899>Map style unavailable</color>";
        if (e.hasRatings) {
            line += std::format(
                "\n<size=80%><color=#57D68D>Pass {:.2f}</color>  <color=#5AA9FF>Acc {:.2f}</color>  <color=#FF6B6B>Tech {:.2f}</color></size>",
                e.passRating, e.accRating, e.techRating);
        } else {
            line += "\n<size=80%><color=#778899>BeatLeader rating graph unavailable</color></size>";
        }
        return line;
    }

    inline std::string FormatAcc(float acc) {
        return std::format("<color=#FF8C29>{:.2f}%</color>", acc * 100.0f);
    }

    inline std::string FormatPP(float pp) {
        if (pp <= 0.0f) return "";
        return std::format("<color=#B856FF>{:.0f}<size=70%>pp</size></color>", pp);
    }

    inline std::string SongLine(FeedEntry const& e) {
        std::string line = Escape(e.songName);
        if (!e.difficulty.empty())
            line += "  <size=75%><color=" + std::string(DiffColor(e.difficulty)) + ">" + DiffLabel(e.difficulty) + "</color></size>";
        if (e.stars > 0.0f)
            line += std::format("  <size=75%><color=#ffaa22>{:.1f}★</color></size>", e.stars);
        return line;
    }

    inline std::string StatsLine(FeedEntry const& e) {
        std::string line = FormatAcc(e.accuracy);
        std::string pp = FormatPP(e.pp);
        if (!pp.empty()) line += "  " + pp;
        if (e.fullCombo) line += "  <color=#57FF8A>FC</color>";
        if (!e.modifiers.empty()) line += "  <color=#999999>+" + Escape(e.modifiers) + "</color>";
        return line;
    }

    // The feed cell's top row: "Song Name - Artist [mapper]" with small
    // colored difficulty and stars at the end.
    inline std::string TitleLine(FeedEntry const& e) {
        std::string line = Escape(e.songName);
        if (!e.songAuthor.empty())
            line += " <color=#BBCCDD>- " + Escape(e.songAuthor) + "</color>";
        if (!e.mapper.empty())
            line += " <size=80%><color=#8899AA>[" + Escape(e.mapper) + "]</color></size>";
        if (!e.difficulty.empty())
            line += "  <size=70%><color=" + std::string(DiffColor(e.difficulty)) + ">" + DiffLabel(e.difficulty) + "</color></size>";
        if (e.stars > 0.0f)
            line += std::format("  <size=70%><color=#FFB921>{:.1f}★</color></size>", e.stars);
        return line;
    }
}
