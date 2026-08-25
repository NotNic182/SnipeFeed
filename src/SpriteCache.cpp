#include "SpriteCache.hpp"
#include "Web.hpp"
#include "main.hpp"

#include "bsml/shared/BSML-Lite.hpp"
#include "bsml/shared/BSML/MainThreadScheduler.hpp"

#include "UnityEngine/Object.hpp"

#include <algorithm>
#include <cstring>
#include <thread>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace SnipeFeed::SpriteCache {

    namespace {
        // Every container below is read and written on the main thread only;
        // the download thread hands its result back via MainThreadScheduler.
        std::unordered_map<std::string, SafePtrUnity<UnityEngine::Sprite>> cache;
        std::unordered_set<std::string> failed;
        std::unordered_map<std::string, std::vector<std::function<void(UnityEngine::Sprite*)>>> pending;

        constexpr long TIMEOUT_SECONDS = 15;

        constexpr size_t MAX_CACHED_SPRITES = 256;
        // Oldest-first access order; a hit moves the URL to the back. Sized
        // above one full 100-score feed's covers+avatars so eviction only
        // bites across many refreshes, never inside the current view.
        std::vector<std::string> loadOrder;

        void EvictIfNeeded() {
            while (loadOrder.size() > MAX_CACHED_SPRITES) {
                auto oldest = loadOrder.front();
                loadOrder.erase(loadOrder.begin());
                auto it = cache.find(oldest);
                if (it == cache.end()) continue;
                if (it->second) {
                    // The texture holds the memory; the sprite is a wrapper.
                    // Cells re-request evicted URLs on their next bind.
                    if (auto* sprite = it->second.ptr()) {
                        // get_texture() returns UnityW<Texture2D> (not a raw
                        // pointer), so the null-check binding must be `auto`
                        // — `auto*` cannot deduce through UnityW's implicit
                        // conversion operator.
                        if (auto texture = sprite->get_texture()) UnityEngine::Object::Destroy(texture);
                        UnityEngine::Object::Destroy(sprite);
                    }
                }
                cache.erase(it);
            }
        }
    }

    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite) {
        if (url.empty() || failed.contains(url)) return;

        auto hit = cache.find(url);
        if (hit != cache.end() && hit->second) {
            auto pos = std::find(loadOrder.begin(), loadOrder.end(), url);
            if (pos != loadOrder.end()) loadOrder.erase(pos);
            loadOrder.push_back(url);
            onSprite(hit->second.ptr());
            return;
        }

        // Coalesce: if a download for this URL is in flight, just queue up.
        auto inFlight = pending.find(url);
        if (inFlight != pending.end()) {
            inFlight->second.push_back(std::move(onSprite));
            return;
        }
        pending[url].push_back(std::move(onSprite));

        std::thread([url] {
            std::string body;
            long code = Web::Get(url, TIMEOUT_SECONDS, body);
            BSML::MainThreadScheduler::Schedule([url, code, body = std::move(body)] {
                auto callbacks = std::move(pending[url]);
                pending.erase(url);
                if (code != 200 || body.empty()) {
                    failed.insert(url);
                    return;
                }
                ArrayW<uint8_t> bytes(static_cast<il2cpp_array_size_t>(body.size()));
                std::memcpy(bytes.begin(), body.data(), body.size());
                auto sprite = BSML::Lite::ArrayToSprite(bytes);
                if (!sprite) {
                    SnipeFeedLogger.warn("Failed to decode image from {}", url);
                    failed.insert(url);
                    return;
                }
                cache[url] = sprite;
                loadOrder.push_back(url);
                EvictIfNeeded();
                for (auto& cb : callbacks) cb(sprite);
            });
        }).detach();
    }

    void ClearCache() {
        cache.clear();
        failed.clear();
        loadOrder.clear();
    }

    void ClearFailures() {
        failed.clear();
    }
}
