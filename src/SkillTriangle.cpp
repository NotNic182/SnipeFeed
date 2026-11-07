#include "SkillTriangle.hpp"

#include <zlib.h>

#include <algorithm>
#include <cmath>

namespace SnipeFeed::SkillTriangle {

    namespace {
        struct Vec { float x, y; };
        struct Color { float r, g, b; };

        // Same colors as the Pass/Acc/Tech text everywhere else.
        constexpr Color TECH_COLOR{1.0f, 0.42f, 0.42f};
        constexpr Color ACC_COLOR{0.35f, 0.66f, 1.0f};
        constexpr Color PASS_COLOR{0.34f, 0.84f, 0.55f};
        constexpr Color WHITE{1.0f, 1.0f, 1.0f};

        constexpr int SUPERSAMPLE = 4;

        Vec Lerp(Vec a, Vec b, float t) { return {a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t}; }

        // Barycentric weights of p in (a, b, c); false if outside or degenerate.
        bool Barycentric(Vec p, Vec a, Vec b, Vec c, float& wa, float& wb, float& wc) {
            float det = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (std::abs(det) < 1e-6f) return false;
            wa = ((b.y - c.y) * (p.x - c.x) + (c.x - b.x) * (p.y - c.y)) / det;
            wb = ((c.y - a.y) * (p.x - c.x) + (a.x - c.x) * (p.y - c.y)) / det;
            wc = 1.0f - wa - wb;
            return wa >= 0.0f && wb >= 0.0f && wc >= 0.0f;
        }

        float SegmentDistance(Vec p, Vec a, Vec b) {
            float dx = b.x - a.x, dy = b.y - a.y;
            float len2 = dx * dx + dy * dy;
            float t = len2 > 0.0f ? std::clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / len2, 0.0f, 1.0f) : 0.0f;
            float ex = p.x - (a.x + dx * t), ey = p.y - (a.y + dy * t);
            return std::sqrt(ex * ex + ey * ey);
        }

        float EdgeDistance(Vec p, Vec a, Vec b, Vec c) {
            return std::min({SegmentDistance(p, a, b), SegmentDistance(p, b, c), SegmentDistance(p, c, a)});
        }

        // Premultiplied "over".
        struct Pixel {
            float r = 0, g = 0, b = 0, a = 0;
            void Over(Color c, float alpha) {
                r = c.r * alpha + r * (1.0f - alpha);
                g = c.g * alpha + g * (1.0f - alpha);
                b = c.b * alpha + b * (1.0f - alpha);
                a = alpha + a * (1.0f - alpha);
            }
        };

        void PutU32(std::string& out, uint32_t v) {
            out += static_cast<char>((v >> 24) & 0xFF);
            out += static_cast<char>((v >> 16) & 0xFF);
            out += static_cast<char>((v >> 8) & 0xFF);
            out += static_cast<char>(v & 0xFF);
        }

        void PutChunk(std::string& out, char const* type, std::string const& data) {
            PutU32(out, static_cast<uint32_t>(data.size()));
            std::string body = std::string(type, 4) + data;
            out += body;
            PutU32(out, static_cast<uint32_t>(crc32(0L, reinterpret_cast<Bytef const*>(body.data()), static_cast<uInt>(body.size()))));
        }
    }

    std::vector<uint8_t> Rasterize(float passRating, float accRating, float techRating, int size) {
        size = std::clamp(size, 8, 512);
        float s = static_cast<float>(size);

        // Equilateral, point down, centered.
        float margin = s * 0.07f;
        float width = s - 2.0f * margin;
        float height = width * std::sqrt(3.0f) / 2.0f;
        float top = (s - height) / 2.0f;
        Vec tech{margin, top};
        Vec acc{s - margin, top};
        Vec pass{s / 2.0f, top + height};
        Vec center{(tech.x + acc.x + pass.x) / 3.0f, (tech.y + acc.y + pass.y) / 3.0f};

        auto norm = [](float rating) { return std::isfinite(rating) ? std::clamp(rating / MAX_RATING, 0.0f, 1.0f) : 0.0f; };
        Vec innerTech = Lerp(center, tech, norm(techRating));
        Vec innerAcc = Lerp(center, acc, norm(accRating));
        Vec innerPass = Lerp(center, pass, norm(passRating));
        Vec halfTech = Lerp(center, tech, 0.5f);
        Vec halfAcc = Lerp(center, acc, 0.5f);
        Vec halfPass = Lerp(center, pass, 0.5f);

        float line = std::max(1.0f, s * 0.018f);

        std::vector<uint8_t> out(static_cast<size_t>(size) * size * 4);
        for (int y = 0; y < size; y++) {
            for (int x = 0; x < size; x++) {
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < SUPERSAMPLE; sy++) {
                    for (int sx = 0; sx < SUPERSAMPLE; sx++) {
                        Vec p{x + (sx + 0.5f) / SUPERSAMPLE, y + (sy + 0.5f) / SUPERSAMPLE};
                        Pixel px;
                        float w0, w1, w2;
                        if (Barycentric(p, tech, acc, pass, w0, w1, w2))
                            px.Over(WHITE, 0.08f);
                        // Guides: center to each corner, and the halfway (7.5) level.
                        if (SegmentDistance(p, center, tech) < line * 0.5f
                            || SegmentDistance(p, center, acc) < line * 0.5f
                            || SegmentDistance(p, center, pass) < line * 0.5f)
                            px.Over(WHITE, 0.16f);
                        if (EdgeDistance(p, halfTech, halfAcc, halfPass) < line * 0.4f)
                            px.Over(WHITE, 0.14f);
                        if (Barycentric(p, innerTech, innerAcc, innerPass, w0, w1, w2)) {
                            // Sharpened weights keep the corner colors vivid
                            // instead of washing out to grey in the middle.
                            w0 = std::pow(w0, 1.6f); w1 = std::pow(w1, 1.6f); w2 = std::pow(w2, 1.6f);
                            float sum = std::max(w0 + w1 + w2, 1e-6f);
                            w0 /= sum; w1 /= sum; w2 /= sum;
                            Color c{TECH_COLOR.r * w0 + ACC_COLOR.r * w1 + PASS_COLOR.r * w2,
                                    TECH_COLOR.g * w0 + ACC_COLOR.g * w1 + PASS_COLOR.g * w2,
                                    TECH_COLOR.b * w0 + ACC_COLOR.b * w1 + PASS_COLOR.b * w2};
                            px.Over(c, 0.92f);
                        }
                        if (EdgeDistance(p, innerTech, innerAcc, innerPass) < line * 0.5f)
                            px.Over(WHITE, 0.45f);
                        if (EdgeDistance(p, tech, acc, pass) < line * 0.6f)
                            px.Over(WHITE, 0.5f);
                        r += px.r; g += px.g; b += px.b; a += px.a;
                    }
                }
                constexpr float samples = SUPERSAMPLE * SUPERSAMPLE;
                r /= samples; g /= samples; b /= samples; a /= samples;
                uint8_t* dst = &out[(static_cast<size_t>(y) * size + x) * 4];
                auto byte = [](float v) { return static_cast<uint8_t>(std::lround(std::clamp(v, 0.0f, 1.0f) * 255.0f)); };
                dst[0] = byte(a > 0.0f ? r / a : 0.0f);
                dst[1] = byte(a > 0.0f ? g / a : 0.0f);
                dst[2] = byte(a > 0.0f ? b / a : 0.0f);
                dst[3] = byte(a);
            }
        }
        return out;
    }

    std::string EncodePng(std::vector<uint8_t> const& rgba, int width, int height) {
        if (width <= 0 || height <= 0 || rgba.size() != static_cast<size_t>(width) * height * 4) return "";

        std::string raw;
        raw.reserve(static_cast<size_t>(height) * (width * 4 + 1));
        for (int y = 0; y < height; y++) {
            raw += '\0';  // filter: none
            raw.append(reinterpret_cast<char const*>(&rgba[static_cast<size_t>(y) * width * 4]), static_cast<size_t>(width) * 4);
        }
        uLongf packedSize = compressBound(static_cast<uLong>(raw.size()));
        std::string packed(packedSize, '\0');
        if (compress2(reinterpret_cast<Bytef*>(packed.data()), &packedSize,
                      reinterpret_cast<Bytef const*>(raw.data()), static_cast<uLong>(raw.size()), 6) != Z_OK)
            return "";
        packed.resize(packedSize);

        std::string header;
        PutU32(header, static_cast<uint32_t>(width));
        PutU32(header, static_cast<uint32_t>(height));
        header += '\x08';  // bit depth
        header += '\x06';  // RGBA
        header += '\0';
        header += '\0';
        header += '\0';

        std::string png("\x89PNG\r\n\x1a\n", 8);
        PutChunk(png, "IHDR", header);
        PutChunk(png, "IDAT", packed);
        PutChunk(png, "IEND", "");
        return png;
    }
}
