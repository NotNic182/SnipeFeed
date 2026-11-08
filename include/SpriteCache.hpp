#pragma once

#include "UnityEngine/Sprite.hpp"

#include <functional>
#include <string>

// URL -> sprite cache for covers and avatars. Main thread only. Callbacks
// never get nullptr; on failure they just don't fire.
namespace SnipeFeed::SpriteCache {
    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite);
    // Skill triangle sprite (SkillTriangle.hpp), drawn on first use and kept
    // in the same bounded cache. Main thread only; nullptr if it failed.
    UnityEngine::Sprite* GetTriangleSprite(float passRating, float accRating, float techRating, int size);
    void ClearCache();
    // Clears only the failed-URL memo (not the sprite cache or in-flight
    // downloads), so images that failed during a transient network loss get
    // another chance after a successful feed refresh.
    void ClearFailures();
}
