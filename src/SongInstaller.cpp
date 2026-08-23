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

    void DownloadAndInstallAsync(std::string hash, std::function<void(bool, std::string)> onDone) {
        std::thread worker([hash = ToLower(std::move(hash)), onDone = std::move(onDone)] {
            // 1) Look the map up on BeatSaver.
            std::string meta;
            long code = Web::Get("https://api.beatsaver.com/maps/hash/" + hash, META_TIMEOUT, meta);
            if (code == 404) {
                onDone(false, "Map not found on BeatSaver.");
                return;
            }
            if (code != 200) {
                onDone(false, "BeatSaver lookup failed (HTTP " + std::to_string(code) + ").");
                return;
            }

            rapidjson::Document doc;
            doc.Parse(meta);
            if (doc.HasParseError() || !doc.IsObject() || !doc.HasMember("versions") || !doc["versions"].IsArray() || doc["versions"].GetArray().Empty()) {
                onDone(false, "Unexpected BeatSaver response.");
                return;
            }

            // Prefer the version entry matching our hash; fall back to the first.
            std::string downloadUrl;
            for (auto const& version : doc["versions"].GetArray()) {
                if (!version.IsObject() || !version.HasMember("downloadURL") || !version["downloadURL"].IsString())
                    continue;
                if (downloadUrl.empty())
                    downloadUrl = version["downloadURL"].GetString();
                if (version.HasMember("hash") && version["hash"].IsString() && ToLower(version["hash"].GetString()) == hash) {
                    downloadUrl = version["downloadURL"].GetString();
                    break;
                }
            }
            if (downloadUrl.empty()) {
                onDone(false, "No download available for this map.");
                return;
            }

            // 2) Download the zip (binary-safe std::string buffer).
            std::string zipData;
            code = Web::Get(downloadUrl, DOWNLOAD_TIMEOUT, zipData);
            if (code != 200 || zipData.empty()) {
                onDone(false, "Download failed (HTTP " + std::to_string(code) + ").");
                return;
            }

            // 3) Extract into the custom levels folder, same as the BeatLeader
            //    mod's playlist installer does.
            auto targetFolder = std::string(SongCore::API::Loading::GetPreferredCustomLevelPath()) + "/" + hash;
            int args = 2;
            int status = zip_stream_extract(
                zipData.data(), zipData.length(), targetFolder.c_str(),
                +[](char const* name, void* arg) -> int { return 0; }, &args);
            if (status != 0) {
                onDone(false, "Failed to extract the map archive.");
                return;
            }

            SnipeFeedLogger.info("Installed map {} to {}", hash, targetFolder);
            onDone(true, "");
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
