using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SnipeFeed.PC.Utils
{
    // BeatLeader's skill triangle, drawn on the CPU straight into a
    // Texture2D. Same layout as BeatLeader's mod: point down, Tech top-left,
    // Acc top-right, Pass bottom, each corner pulled in by rating / 15.
    // Main thread only.
    internal static class SkillTriangle
    {
        public const float MaxRating = 15f;
        private const int Supersample = 4;
        private const int MaxCached = 256;

        // Same colors as the Pass/Acc/Tech text on Quest.
        private static readonly Rgb TechColor = new Rgb(1f, 0.42f, 0.42f);
        private static readonly Rgb AccColor = new Rgb(0.35f, 0.66f, 1f);
        private static readonly Rgb PassColor = new Rgb(0.34f, 0.84f, 0.55f);
        private static readonly Rgb White = new Rgb(1f, 1f, 1f);

        private static readonly Dictionary<string, Sprite> Cache = new Dictionary<string, Sprite>();
        private static readonly List<string> LoadOrder = new List<string>();

        public static Sprite GetSprite(float passRating, float accRating, float techRating, int size)
        {
            size = Math.Max(8, Math.Min(512, size));
            var key = string.Format(CultureInfo.InvariantCulture, "{0}:{1:F2}:{2:F2}:{3:F2}", size, passRating, accRating, techRating);
            if (Cache.TryGetValue(key, out var hit) && hit != null)
            {
                LoadOrder.Remove(key);
                LoadOrder.Add(key);
                return hit;
            }

            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            texture.SetPixels32(Rasterize(passRating, accRating, techRating, size));
            texture.Apply(false, true);
            var sprite = Sprite.Create(texture, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect);

            Cache[key] = sprite;
            LoadOrder.Add(key);
            while (LoadOrder.Count > MaxCached)
            {
                var oldest = LoadOrder[0];
                LoadOrder.RemoveAt(0);
                if (!Cache.TryGetValue(oldest, out var evicted)) continue;
                Cache.Remove(oldest);
                if (evicted == null) continue;
                if (evicted.texture != null) UnityEngine.Object.Destroy(evicted.texture);
                UnityEngine.Object.Destroy(evicted);
            }
            return sprite;
        }

        // size x size pixels in Unity's order (bottom row first), straight alpha.
        public static Color32[] Rasterize(float passRating, float accRating, float techRating, int size)
        {
            float s = size;

            // Equilateral, point down, centered.
            var margin = s * 0.07f;
            var width = s - 2f * margin;
            var height = width * (float)Math.Sqrt(3.0) / 2f;
            var top = (s - height) / 2f;
            var tech = new Vec(margin, top);
            var acc = new Vec(s - margin, top);
            var pass = new Vec(s / 2f, top + height);
            var center = new Vec((tech.X + acc.X + pass.X) / 3f, (tech.Y + acc.Y + pass.Y) / 3f);

            var innerTech = Lerp(center, tech, Normalize(techRating));
            var innerAcc = Lerp(center, acc, Normalize(accRating));
            var innerPass = Lerp(center, pass, Normalize(passRating));
            var halfTech = Lerp(center, tech, 0.5f);
            var halfAcc = Lerp(center, acc, 0.5f);
            var halfPass = Lerp(center, pass, 0.5f);

            var line = Math.Max(1f, s * 0.018f);
            const float samples = Supersample * Supersample;

            var pixels = new Color32[size * size];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    float r = 0f, g = 0f, b = 0f, a = 0f;
                    for (var sy = 0; sy < Supersample; sy++)
                    {
                        for (var sx = 0; sx < Supersample; sx++)
                        {
                            var p = new Vec(x + (sx + 0.5f) / Supersample, y + (sy + 0.5f) / Supersample);
                            var px = new Pixel();
                            if (Barycentric(p, tech, acc, pass, out _, out _, out _))
                                px.Over(White, 0.08f);
                            // Guides: center to each corner, and the halfway (7.5) level.
                            if (SegmentDistance(p, center, tech) < line * 0.5f
                                || SegmentDistance(p, center, acc) < line * 0.5f
                                || SegmentDistance(p, center, pass) < line * 0.5f)
                                px.Over(White, 0.16f);
                            if (EdgeDistance(p, halfTech, halfAcc, halfPass) < line * 0.4f)
                                px.Over(White, 0.14f);
                            if (Barycentric(p, innerTech, innerAcc, innerPass, out var w0, out var w1, out var w2))
                            {
                                // Sharpened weights keep the corner colors vivid
                                // instead of washing out to grey in the middle.
                                w0 = (float)Math.Pow(w0, 1.6);
                                w1 = (float)Math.Pow(w1, 1.6);
                                w2 = (float)Math.Pow(w2, 1.6);
                                var sum = Math.Max(w0 + w1 + w2, 1e-6f);
                                w0 /= sum;
                                w1 /= sum;
                                w2 /= sum;
                                px.Over(new Rgb(
                                    TechColor.R * w0 + AccColor.R * w1 + PassColor.R * w2,
                                    TechColor.G * w0 + AccColor.G * w1 + PassColor.G * w2,
                                    TechColor.B * w0 + AccColor.B * w1 + PassColor.B * w2), 0.92f);
                            }
                            if (EdgeDistance(p, innerTech, innerAcc, innerPass) < line * 0.5f)
                                px.Over(White, 0.45f);
                            if (EdgeDistance(p, tech, acc, pass) < line * 0.6f)
                                px.Over(White, 0.5f);
                            r += px.R;
                            g += px.G;
                            b += px.B;
                            a += px.A;
                        }
                    }
                    r /= samples;
                    g /= samples;
                    b /= samples;
                    a /= samples;
                    // y runs top-down here; Unity textures start at the bottom.
                    pixels[(size - 1 - y) * size + x] = new Color32(
                        ToByte(a > 0f ? r / a : 0f),
                        ToByte(a > 0f ? g / a : 0f),
                        ToByte(a > 0f ? b / a : 0f),
                        ToByte(a));
                }
            }
            return pixels;
        }

        private static float Normalize(float rating) =>
            float.IsNaN(rating) || float.IsInfinity(rating) ? 0f : Math.Max(0f, Math.Min(1f, rating / MaxRating));

        private static byte ToByte(float v) =>
            (byte)Math.Round(Math.Max(0f, Math.Min(1f, v)) * 255f, MidpointRounding.AwayFromZero);

        private static Vec Lerp(Vec a, Vec b, float t) => new Vec(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

        // Barycentric weights of p in (a, b, c); false if outside or degenerate.
        private static bool Barycentric(Vec p, Vec a, Vec b, Vec c, out float wa, out float wb, out float wc)
        {
            wa = wb = wc = 0f;
            var det = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(det) < 1e-6f) return false;
            wa = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / det;
            wb = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / det;
            wc = 1f - wa - wb;
            return wa >= 0f && wb >= 0f && wc >= 0f;
        }

        private static float SegmentDistance(Vec p, Vec a, Vec b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            var len2 = dx * dx + dy * dy;
            var t = len2 > 0f ? Math.Max(0f, Math.Min(1f, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2)) : 0f;
            float ex = p.X - (a.X + dx * t), ey = p.Y - (a.Y + dy * t);
            return (float)Math.Sqrt(ex * ex + ey * ey);
        }

        private static float EdgeDistance(Vec p, Vec a, Vec b, Vec c) =>
            Math.Min(SegmentDistance(p, a, b), Math.Min(SegmentDistance(p, b, c), SegmentDistance(p, c, a)));

        private readonly struct Vec
        {
            public readonly float X;
            public readonly float Y;
            public Vec(float x, float y) { X = x; Y = y; }
        }

        private readonly struct Rgb
        {
            public readonly float R;
            public readonly float G;
            public readonly float B;
            public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
        }

        // Premultiplied "over".
        private struct Pixel
        {
            public float R, G, B, A;

            public void Over(Rgb c, float alpha)
            {
                R = c.R * alpha + R * (1f - alpha);
                G = c.G * alpha + G * (1f - alpha);
                B = c.B * alpha + B * (1f - alpha);
                A = alpha + A * (1f - alpha);
            }
        }
    }
}
