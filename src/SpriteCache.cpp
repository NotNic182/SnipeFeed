#include "SpriteCache.hpp"
#include "Web.hpp"
#include "main.hpp"

#include "bsml/shared/BSML-Lite.hpp"
#include "bsml/shared/BSML/MainThreadScheduler.hpp"

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
    }

    void GetSprite(std::string const& url, std::function<void(UnityEngine::Sprite*)> onSprite) {
        if (url.empty() || failed.contains(url)) return;

        auto hit = cache.find(url);
        if (hit != cache.end() && hit->second) {
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
                for (auto& cb : callbacks) cb(sprite);
            });
        }).detach();
    }

    void ClearCache() {
        cache.clear();
        failed.clear();
    }

    void ClearFailures() {
        failed.clear();
    }
}
