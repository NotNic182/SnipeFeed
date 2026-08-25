#pragma once

#include "GlobalNamespace/BeatmapLevel.hpp"

#include <functional>
#include <string>

namespace SnipeFeed::Installer {
    // Returns the installed level for a song hash, or nullptr.
    GlobalNamespace::BeatmapLevel* GetInstalledLevel(std::string const& hash);

    // Downloads a map zip from BeatSaver by hash and extracts it into the
    // custom levels folder on a worker thread. onDone(success, error) is
    // called FROM THE WORKER THREAD; caller must refresh SongCore afterwards
    // on the main thread.
    void DownloadAndInstallAsync(std::string hash, std::function<void(bool, std::string)> onDone);

    // Primes the solo flow coordinator so its next activation opens with
    // the given level selected. Must be called on the main thread while the
    // coordinator is still active (i.e. before dismissing back to the main
    // menu). Returns false if the coordinator could not be found.
    bool PrimeSoloFlow(GlobalNamespace::BeatmapLevel* level);

    // Presses the main menu's Solo button, activating the (primed) solo
    // flow. Must be called on the main thread with the main menu visible.
    // Returns false if the button could not be found.
    bool PressSoloButton();
}
