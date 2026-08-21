#pragma once

#include "UnityEngine/Sprite.hpp"

#include <functional>
#include <string>

// URL -> Sprite cache for avatars and cover art, modeled on the official
// BeatLeader mod's Sprites::get_Icon. Main-thread only: callbacks fire on
// the main thread (synchronously on a cache hit) and never with nullptr —
// on failure the callback simply never fires, so callers keep their
// placeholder. Failed URLs are not retried within the session.
namespace SnipeFeed::SpriteCache {
    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite);
    void ClearCache();
}
