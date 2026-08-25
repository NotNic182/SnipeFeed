#include "Web.hpp"
#include "main.hpp"

#include "libcurl/shared/curl.h"
#include "libcurl/shared/easy.h"

namespace SnipeFeed::Web {

    static std::size_t WriteToString(void* contents, std::size_t size, std::size_t nmemb, std::string* s) {
        std::size_t newLength = size * nmemb;
        try {
            s->append((char*)contents, newLength);
        } catch (std::bad_alloc&) {
            SnipeFeedLogger.critical("Failed to allocate response buffer of size: {}", newLength);
            return 0;
        }
        return newLength;
    }

    long Get(std::string const& url, long timeoutSeconds, std::string& out, std::string const& cookieFile) {
        auto* curl = curl_easy_init();
        if (!curl) return -1;

        struct curl_slist* headers = nullptr;
        headers = curl_slist_append(headers, "Accept: application/json");
        curl_easy_setopt(curl, CURLOPT_HTTPHEADER, headers);

        if (!cookieFile.empty())
            curl_easy_setopt(curl, CURLOPT_COOKIEFILE, cookieFile.c_str());

        std::string userAgent = std::string("SnipeFeed/") + VERSION + " (Quest BeatSaber)";

        curl_easy_setopt(curl, CURLOPT_URL, url.c_str());
        curl_easy_setopt(curl, CURLOPT_TIMEOUT, timeoutSeconds);
        curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, 10L);
        curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 1L);
        curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, WriteToString);
        curl_easy_setopt(curl, CURLOPT_WRITEDATA, &out);
        curl_easy_setopt(curl, CURLOPT_USERAGENT, userAgent.c_str());
        // Threaded use: signal-based timeouts would deliver SIGALRM to a
        // random thread.
        curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
        // Advertise every codec this build can decode; BeatLeader's 100-score
        // JSON pages compress ~10x.
        curl_easy_setopt(curl, CURLOPT_ACCEPT_ENCODING, "");
        // Verify TLS peers against Android's system CA store — a c_rehash
        // style directory of PEM files, which CAPATH understands. This mod
        // sends the BeatLeader login cookie; without verification anyone who
        // can spoof DNS on the local network can read it.
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 1L);
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 2L);
        curl_easy_setopt(curl, CURLOPT_CAPATH, "/system/etc/security/cacerts");

        long httpCode = 0;
        auto res = curl_easy_perform(curl);
        if (res != CURLE_OK) {
            SnipeFeedLogger.error("curl failed for {}: {} {}", url, static_cast<int>(res), curl_easy_strerror(res));
            if (res == CURLE_PEER_FAILED_VERIFICATION || res == CURLE_SSL_CACERT_BADFILE)
                SnipeFeedLogger.error("TLS verification failed — the system CA path may be unusable with this libcurl build; see docs/superpowers/plans/2026-08-25-reliability-fixes.md Task 6");

            curl_slist_free_all(headers);
            curl_easy_cleanup(curl);
            return -static_cast<long>(res);
        }
        curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &httpCode);
        curl_slist_free_all(headers);
        curl_easy_cleanup(curl);
        return httpCode;
    }
}
