#pragma once

#include "Feed.hpp"

#include <algorithm>
#include <cmath>
#include <ctime>
#include <format>
#include <string>

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
