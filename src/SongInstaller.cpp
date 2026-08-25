#include "SongInstaller.hpp"
#include "Web.hpp"
#include "main.hpp"

#include "beatsaber-hook/shared/config/rapidjson-utils.hpp"

#include "songcore/shared/SongCore.hpp"

#include "GlobalNamespace/LevelSelectionFlowCoordinator.hpp"
#include "GlobalNamespace/SelectLevelCategoryViewController.hpp"
#include "GlobalNamespace/SoloFreePlayFlowCoordinator.hpp"
#include "HMUI/NoTransitionsButton.hpp"
#include "System/Nullable_1.hpp"
#include "UnityEngine/GameObject.hpp"
#include "UnityEngine/Object.hpp"

#include "zip.h"

#include <algorithm>
#include <filesystem>
#include <thread>

using namespace GlobalNamespace;

namespace SnipeFeed::Installer {

    static constexpr long META_TIMEOUT = 15;
    static constexpr long DOWNLOAD_TIMEOUT = 120;

    static std::string ToLower(std::string s) {
        std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return std::tolower(c); });
        return s;
    }

    GlobalNamespace::BeatmapLevel* GetInstalledLevel(std::string const& hash) {
        if (hash.empty()) return nullptr;
        auto level = SongCore::API::Loading::GetLevelByHash(hash);
        if (!level)
            level = SongCore::API::Loading::GetLevelByHash(ToLower(hash));
        return level;
    }

    // The hash arrives from feed JSON; it becomes a URL segment and an
    // install folder name, so anything but exactly 40 hex chars is refused.
    static bool IsValidHash(std::string const& hash) {
        if (hash.size() != 40) return false;
        return std::all_of(hash.begin(), hash.end(), [](unsigned char c) {
            return (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
        });
    }

    // Per-entry extraction instead of zip_stream_extract: the bulk helper
    // recreates symlink entries with unrestricted targets, letting a hostile
    // archive write through them anywhere on disk. Writing each regular file
    // ourselves (zip_entry_fread) closes that door, and lets us refuse
    // escape-y names and cap the decompressed size.
    static bool ExtractArchiveSafely(std::string const& zipData, std::string const& targetDir, std::string& outError) {
        constexpr unsigned long long MAX_TOTAL_BYTES = 512ull * 1024 * 1024;

        zip_t* archive = zip_stream_open(zipData.data(), zipData.size(), 0, 'r');
        if (!archive) {
            outError = "The map archive could not be read.";
            return false;
        }
        int total = zip_entries_total(archive);
        if (total <= 0) {
            zip_close(archive);
            outError = "The map archive is empty.";
            return false;
        }

        unsigned long long extractedBytes = 0;
        for (int i = 0; i < total; i++) {
            if (zip_entry_openbyindex(archive, i) != 0) {
                zip_close(archive);
                outError = "The map archive could not be read.";
                return false;
            }
            char const* rawName = zip_entry_name(archive);
            std::string name = rawName ? rawName : "";
            bool isDir = zip_entry_isdir(archive) == 1;

            unsigned long long entrySize = zip_entry_size(archive);
            // A forged zip64 size could wrap the running total past the cap.
            if (entrySize > MAX_TOTAL_BYTES) {
                zip_entry_close(archive);
                zip_close(archive);
                outError = "The map archive is unreasonably large.";
                return false;
            }
            extractedBytes += entrySize;

            // Stricter than strictly necessary ("song..egg" is also refused)
            // — real beatmap zips contain plain relative names only.
            bool badName = name.empty()
                || name.front() == '/'
                || name.find('\\') != std::string::npos
                || name.find("..") != std::string::npos
                || name.find(':') != std::string::npos;
            if (badName || extractedBytes > MAX_TOTAL_BYTES) {
                zip_entry_close(archive);
                zip_close(archive);
                outError = badName ? "The map archive contains an unsafe file name."
                                   : "The map archive is unreasonably large.";
                return false;
            }

            std::string destination = targetDir + "/" + name;
            std::error_code fsError;
            if (isDir) {
                std::filesystem::create_directories(destination, fsError);
            } else {
                auto parent = std::filesystem::path(destination).parent_path();
                if (!parent.empty()) std::filesystem::create_directories(parent, fsError);
                if (zip_entry_fread(archive, destination.c_str()) != 0) {
                    zip_entry_close(archive);
                    zip_close(archive);
                    outError = "Failed to extract the map archive.";
                    return false;
                }
            }
            zip_entry_close(archive);
        }
        zip_close(archive);
        return true;
    }

    void DownloadAndInstallAsync(std::string hash, std::function<void(bool, std::string)> onDone) {
        std::thread worker([hash = ToLower(std::move(hash)), onDone = std::move(onDone)] {
            // onDone must fire exactly once; if the callback itself
            // throws, the catch below must not fire it again.
            bool doneCalled = false;
            auto finish = [&](bool ok, std::string message) {
                doneCalled = true;
                onDone(ok, std::move(message));
            };

            try {

                if (!IsValidHash(hash)) {
                    finish(false, "This score has an invalid map hash.");
                    return;
                }

                // 1) Look the map up on BeatSaver.
                std::string meta;
                long code = Web::Get("https://api.beatsaver.com/maps/hash/" + hash, META_TIMEOUT, meta);
                if (code < 0) {
                    finish(false, "Network error. Check your connection.");
                    return;
                }
                if (code == 404) {
                    finish(false, "Map not found on BeatSaver.");
                    return;
                }
                if (code != 200) {
                    finish(false, "BeatSaver lookup failed (HTTP " + std::to_string(code) + ").");
                    return;
                }

                rapidjson::Document doc;
                doc.Parse(meta);
                if (doc.HasParseError() || !doc.IsObject() || !doc.HasMember("versions") || !doc["versions"].IsArray() || doc["versions"].GetArray().Empty()) {
                    finish(false, "Unexpected BeatSaver response.");
                    return;
                }

                // Only the version matching the score's hash: a different
                // version would never match the post-install lookup and
                // silently changes the map under the score.
                std::string downloadUrl;
                for (auto const& version : doc["versions"].GetArray()) {
                    if (!version.IsObject() || !version.HasMember("downloadURL") || !version["downloadURL"].IsString())
                        continue;
                    if (version.HasMember("hash") && version["hash"].IsString() && ToLower(version["hash"].GetString()) == hash) {
                        downloadUrl = version["downloadURL"].GetString();
                        break;
                    }
                }
                if (downloadUrl.empty()) {
                    finish(false, "This score's map version is no longer available on BeatSaver.");
                    return;
                }

                // 2) Download the zip (binary-safe std::string buffer).
                std::string zipData;
                code = Web::Get(downloadUrl, DOWNLOAD_TIMEOUT, zipData);
                if (code < 0) {
                    finish(false, "Network error while downloading the map.");
                    return;
                }
                if (code != 200 || zipData.empty()) {
                    finish(false, "Download failed (HTTP " + std::to_string(code) + ").");
                    return;
                }

                // 3) Extract into a temp sibling and rename into place, so a
                //    failure never leaves a half-written folder for SongCore
                //    to choke on, and a retry never merges with stale files.
                auto levelsRoot = std::string(SongCore::API::Loading::GetPreferredCustomLevelPath());
                auto targetFolder = levelsRoot + "/" + hash;
                auto tempFolder = levelsRoot + "/.snipefeed_tmp_" + hash;

                std::error_code fsError;
                std::filesystem::remove_all(tempFolder, fsError);
                std::filesystem::create_directories(tempFolder, fsError);
                if (fsError) {
                    finish(false, "Couldn't create the install folder.");
                    return;
                }

                std::string extractError;
                if (!ExtractArchiveSafely(zipData, tempFolder, extractError)) {
                    std::filesystem::remove_all(tempFolder, fsError);
                    finish(false, extractError);
                    return;
                }

                std::filesystem::remove_all(targetFolder, fsError);
                fsError.clear();
                std::filesystem::rename(tempFolder, targetFolder, fsError);
                if (fsError) {
                    std::filesystem::remove_all(tempFolder, fsError);
                    finish(false, "Couldn't move the map into Custom Levels.");
                    return;
                }

                SnipeFeedLogger.info("Installed map {} to {}", hash, targetFolder);
                finish(true, "");
            } catch (std::exception const& e) {
                // An exception escaping a detached thread is std::terminate.
                SnipeFeedLogger.error("Map install crashed: {}", e.what());
                if (!doneCalled)
                    onDone(false, "Something went wrong installing the map.");
            }
        });
        worker.detach();
    }

    // Mirrors BeatLeader's MapDownloadDialog::OpenMap for our stack, split in
    // two: priming must happen while the solo flow coordinator is still
    // active (FindObjectOfType only sees active objects), pressing the Solo
    // button only works once the main menu is visible again.
    bool PrimeSoloFlow(GlobalNamespace::BeatmapLevel* level) {
        if (level == nullptr) return false;

        auto customLevelsPack = SongCore::API::Loading::GetCustomLevelPack();
        if (customLevelsPack == nullptr) return false;
        if (customLevelsPack->_beatmapLevels->get_Length() == 0) return false;

        auto levelCategory = System::Nullable_1<SelectLevelCategoryViewController::LevelCategory>();
        levelCategory.value = SelectLevelCategoryViewController::LevelCategory(SelectLevelCategoryViewController::LevelCategory::All);
        levelCategory.hasValue = true;

        auto state = LevelSelectionFlowCoordinator::State::New_ctor(customLevelsPack, level);
        state->___levelCategory = levelCategory;

        auto soloFlowCoordinator = UnityEngine::Object::FindObjectOfType<SoloFreePlayFlowCoordinator*>();
        if (!soloFlowCoordinator) {
            SnipeFeedLogger.error("SoloFreePlayFlowCoordinator not found");
            return false;
        }
        soloFlowCoordinator->Setup(state);
        return true;
    }

    bool PressSoloButton() {
        SafePtrUnity<UnityEngine::GameObject> songSelectButton = UnityEngine::GameObject::Find("SoloButton").unsafePtr();
        if (!songSelectButton)
            songSelectButton = UnityEngine::GameObject::Find("Wrapper/BeatmapWithModifiers/BeatmapSelection/EditButton");
        if (!songSelectButton) {
            SnipeFeedLogger.error("Could not find the solo menu button to press");
            return false;
        }
        auto button = songSelectButton->GetComponent<HMUI::NoTransitionsButton*>();
        if (!button)
            button = songSelectButton->GetComponentInChildren<HMUI::NoTransitionsButton*>();
        if (!button) {
            SnipeFeedLogger.error("Solo menu object found but has no NoTransitionsButton");
            return false;
        }
        button->Press();
        return true;
    }
}
