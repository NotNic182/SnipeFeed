#pragma once

#include <string>

namespace SnipeFeed::Web {
    // Blocking GET, intended to be called from a worker thread only.
    // Returns the HTTP status code (or negative curl error code) and fills `out`.
    // If cookieFile is non-empty, cookies are read from it (never written).
    long Get(std::string const& url, long timeoutSeconds, std::string& out, std::string const& cookieFile = "");
}
