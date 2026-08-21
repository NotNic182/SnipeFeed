#pragma once

#include "UnityEngine/Sprite.hpp"

#include <functional>
#include <string>

// URL -> Sprite cache for avatars and cover art, modeled on the official
// BeatLeader mod's Sprites::get_Icon. Main-thread only: callbacks fire on
// the main thread (synchronously on a cache hit) and never with nullptr —
// on failure the callback simply never fires, so callers keep their
// placeholder. Failed URLs are not retried until the next successful feed
// refresh.
namespace SnipeFeed::SpriteCache {
    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite);
    void ClearCache();
    // Clears only the failed-URL memo (not the sprite cache or in-flight
    // downloads), so images that failed during a transient network loss get
    // another chance after a successful feed refresh.
    void ClearFailures();
}
