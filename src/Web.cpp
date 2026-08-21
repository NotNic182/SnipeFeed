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
        // Matches the approach used by the BeatLeader Quest mod: Android's cert
        // store is not reliably available to native code, so peer verification
        // is disabled. Endpoints are public read-only data over HTTPS.
        curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);

        long httpCode = 0;
        auto res = curl_easy_perform(curl);
        if (res != CURLE_OK) {
            SnipeFeedLogger.error("curl failed for {}: {} {}", url, static_cast<int>(res), curl_easy_strerror(res));
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
