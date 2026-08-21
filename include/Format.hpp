#pragma once

#include "Feed.hpp"

#include <algorithm>
#include <cmath>
#include <ctime>
#include <format>
#include <string>

// Text formatting shared by the feed cells and the detail modal.
// Colors and the accuracy gradient match the official BeatLeader mod
// (references/beatleader-qmod/include/Utils/FormatUtils.hpp).
namespace SnipeFeed::Format {

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
        return diff == "ExpertPlus" ? "Ex+" : diff;
    }

    // BeatLeader's accuracy color: lerp #EEFF9E -> #FF6347 by pow(acc, 14).
    inline std::string AccColorHex(float acc) {
        float t = std::pow(std::clamp(acc, 0.0f, 1.0f), 14.0f);
        auto lerp = [](float a, float b, float t) { return a + (b - a) * t; };
        int r = static_cast<int>(std::lround(lerp(0.93f, 1.00f, t) * 255));
        int g = static_cast<int>(std::lround(lerp(1.00f, 0.39f, t) * 255));
        int b = static_cast<int>(std::lround(lerp(0.62f, 0.28f, t) * 255));
        return std::format("#{:02X}{:02X}{:02X}", r, g, b);
    }

    inline std::string FormatAcc(float acc) {
        return std::format("<color={}>{:.2f}%</color>", AccColorHex(acc), acc * 100.0f);
    }

    inline std::string FormatPP(float pp) {
        if (pp <= 0.0f) return "";
        return std::format("<color=#B856FF>{:.0f}<size=70%>pp</size></color>", pp);
    }

    inline std::string SongLine(FeedEntry const& e) {
        std::string line = e.songName;
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
        if (!e.modifiers.empty()) line += "  <color=#999999>+" + e.modifiers + "</color>";
        return line;
    }
}
