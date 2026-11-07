#pragma once

#include <cstdint>
#include <string>
#include <vector>

// BeatLeader's skill triangle, drawn on the CPU into a PNG so it goes through
// the same ArrayToSprite path as cover art (no shader, mesh or asset bundle).
// Same layout as BeatLeader's mod: Tech top-left, Acc top-right, Pass bottom,
// each corner pulled in by rating / 15. No game types in here.
namespace SnipeFeed::SkillTriangle {

    inline constexpr float MAX_RATING = 15.0f;

    // size x size RGBA8 pixels, rows top to bottom, straight alpha.
    std::vector<uint8_t> Rasterize(float passRating, float accRating, float techRating, int size);

    std::string EncodePng(std::vector<uint8_t> const& rgba, int width, int height);

    inline std::string RenderPng(float passRating, float accRating, float techRating, int size) {
        return EncodePng(Rasterize(passRating, accRating, techRating, size), size, size);
    }
}
