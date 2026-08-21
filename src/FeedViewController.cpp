#include "FeedViewController.hpp"
#include "Feed.hpp"
#include "ModConfig.hpp"
#include "SongInstaller.hpp"
#include "main.hpp"

#include "bsml/shared/BSML-Lite.hpp"
#include "bsml/shared/BSML/MainThreadScheduler.hpp"
#include "bsml/shared/Helpers/delegates.hpp"
#include "bsml/shared/Helpers/getters.hpp"

#include "songcore/shared/SongCore.hpp"

#include "GlobalNamespace/MainFlowCoordinator.hpp"
#include "HMUI/FlowCoordinator.hpp"
#include "HMUI/Touchable.hpp"
#include "UnityEngine/GameObject.hpp"
#include "UnityEngine/Object.hpp"
#include "TMPro/TextAlignmentOptions.hpp"
#include "UnityEngine/TextAnchor.hpp"
#include "UnityEngine/RectOffset.hpp"
#include "UnityEngine/RectTransform.hpp"
#include "UnityEngine/UI/HorizontalLayoutGroup.hpp"
#include "UnityEngine/UI/ContentSizeFitter.hpp"
#include "UnityEngine/UI/LayoutElement.hpp"
#include "UnityEngine/UI/VerticalLayoutGroup.hpp"

#include <atomic>
#include <ctime>
#include <format>

DEFINE_TYPE(SnipeFeed, FeedViewController);

using namespace SnipeFeed;

namespace {
    // Bumped on every refresh so results from an outdated request are dropped.
    std::atomic<int> refreshGeneration{0};
    std::atomic<bool> refreshInFlight{false};

    // Plain data, safe as file statics across view re-creation.
    std::vector<FeedEntry> gEntries;
    std::vector<int> gVisible;          // list row -> gEntries index
    std::vector<std::string> gPlayers;  // unique player names, feed order
    std::string gPlayerFilter;          // empty = all players
    int gSelected = -1;
    bool gBusyPlaying = false;

    constexpr auto FILTER_ALL = "All players";

    std::string TimeAgo(long long timepost) {
        if (timepost <= 0) return "";
        long long diff = static_cast<long long>(std::time(nullptr)) - timepost;
        if (diff < 0) diff = 0;
        if (diff < 60) return "just now";
        if (diff < 3600) return std::to_string(diff / 60) + "m ago";
        if (diff < 86400) return std::to_string(diff / 3600) + "h ago";
        return std::to_string(diff / 86400) + "d ago";
    }

    // BetterSongSearch-style difficulty colors.
    char const* DiffColor(std::string const& diff) {
        if (diff == "Easy") return "#3cb371";
        if (diff == "Normal") return "#59b0f4";
        if (diff == "Hard") return "#ff6347";
        if (diff == "Expert") return "#bf2a42";
        if (diff == "ExpertPlus") return "#8f48db";
        return "#bbbbbb";
    }

    char const* AccColor(float acc) {
        if (acc >= 0.95f) return "#ffdd57";
        if (acc >= 0.90f) return "#57ff8a";
        if (acc >= 0.80f) return "#59b0f4";
        return "#bbbbbb";
    }

    std::string DiffLabel(std::string const& diff) {
        return diff == "ExpertPlus" ? "Ex+" : diff;
    }

    // Line 1: song, colored difficulty, stars.
    std::string CellTitle(FeedEntry const& e) {
        std::string line = e.songName;
        if (!e.difficulty.empty())
            line += "  <size=75%><color=" + std::string(DiffColor(e.difficulty)) + ">" + DiffLabel(e.difficulty) + "</color></size>";
        if (e.stars > 0.0f)
            line += std::format("  <size=75%><color=#ffaa22>{:.1f}★</color></size>", e.stars);
        return line;
    }

    // Line 2, list version: the LevelListTableCell subtitle does NOT parse
    // rich text (tags render literally), so this stays plain.
    std::string CellSubtitle(FeedEntry const& e) {
        std::string line = e.playerName;
        line += std::format("   {:.2f}%", e.accuracy * 100.0f);
        if (e.pp > 0.0f)
            line += std::format("   {:.0f}pp", e.pp);
        if (e.fullCombo)
            line += "   FC";
        if (!e.modifiers.empty())
            line += "   +" + e.modifiers;
        line += "   " + TimeAgo(e.timepost);
        return line;
    }

    // Line 2, modal version: modal text is a normal TMP label, rich text works.
    std::string RichSubtitle(FeedEntry const& e) {
        std::string line = "<color=#ffffff>" + e.playerName + "</color>";
        line += std::format("   <color={}>{:.2f}%</color>", AccColor(e.accuracy), e.accuracy * 100.0f);
        if (e.pp > 0.0f)
            line += std::format("   <color=#8992e8>{:.0f}pp</color>", e.pp);
        if (e.fullCombo)
            line += "   <color=#57ff8a>FC</color>";
        if (!e.modifiers.empty())
            line += "   <color=#999999>+" + e.modifiers + "</color>";
        line += "   <color=#777777>" + TimeAgo(e.timepost) + "</color>";
        return line;
    }
}

void FeedViewController::RebuildFilter() {
    if (!filterContainer) return;

    for (int i = filterContainer->get_childCount() - 1; i >= 0; i--)
        UnityEngine::Object::Destroy(filterContainer->GetChild(i)->get_gameObject());

    static std::vector<std::string> ownedNames;
    ownedNames.clear();
    ownedNames.push_back(FILTER_ALL);
    for (auto const& name : gPlayers)
        ownedNames.push_back(name);

    std::vector<std::string_view> views(ownedNames.begin(), ownedNames.end());
    std::string current = gPlayerFilter.empty() ? FILTER_ALL : gPlayerFilter;

    auto self = this;
    BSML::Lite::CreateDropdown(filterContainer, "Player", current, views, [self](StringW value) {
        std::string selected = static_cast<std::string>(value);
        gPlayerFilter = (selected == FILTER_ALL) ? "" : selected;
        self->RebuildList();
    });
}

void FeedViewController::RebuildList() {
    if (!listData) return;

    gVisible.clear();
    listData->data->Clear();
    for (int i = 0; i < static_cast<int>(gEntries.size()); i++) {
        auto const& e = gEntries[i];
        if (!gPlayerFilter.empty() && e.playerName != gPlayerFilter)
            continue;
        gVisible.push_back(i);
        listData->data->Add(BSML::CustomCellInfo::construct(CellTitle(e), CellSubtitle(e)));
    }
    if (listData->tableView) {
        listData->tableView->ReloadData();
        listData->tableView->ClearSelection();
    }

    if (statusText) {
        if (gEntries.empty()) {
            // Keep whatever error/progress message is already showing.
        } else if (gPlayerFilter.empty()) {
            statusText->set_text(std::format("{} recent scores. Newest first — go snipe!", gEntries.size()));
        } else {
            statusText->set_text(std::format("{} of {} scores by {}", gVisible.size(), gEntries.size(), gPlayerFilter));
        }
    }
}

void FeedViewController::OnCellClicked(int listIdx) {
    if (listData && listData->tableView)
        listData->tableView->ClearSelection();
    if (listIdx < 0 || listIdx >= static_cast<int>(gVisible.size())) return;
    gSelected = gVisible[listIdx];
    auto const& e = gEntries[gSelected];

    if (detailText) {
        std::string info = "<b>" + e.songName + "</b>";
        if (!e.songAuthor.empty())
            info += "\n<size=80%><color=#bbbbbb>" + e.songAuthor + "</color></size>";
        info += "\n\n" + RichSubtitle(e);
        detailText->set_text(info);
    }

    if (playButtonText) {
        if (e.songHash.empty())
            playButtonText->set_text("Not a custom song");
        else if (Installer::GetInstalledLevel(e.songHash))
            playButtonText->set_text("Play");
        else
            playButtonText->set_text("Download & Play");
    }
    if (playButton)
        playButton->set_interactable(!e.songHash.empty() && !gBusyPlaying);

    if (detailModal)
        detailModal->Show();
}

void FeedViewController::LaunchLevel(GlobalNamespace::BeatmapLevel* level) {
    if (!level) return;
    if (detailModal) detailModal->Hide();

    // Our view lives inside a BSML-presented flow coordinator; dismiss it
    // first so the real main menu (and its Solo button) is visible again.
    auto mainFC = BSML::Helpers::GetMainFlowCoordinator();
    HMUI::FlowCoordinator* youngest = mainFC->YoungestChildFlowCoordinatorOrSelf();
    if (youngest && youngest != static_cast<HMUI::FlowCoordinator*>(mainFC) && youngest->_parentFlowCoordinator) {
        HMUI::FlowCoordinator* parent = youngest->_parentFlowCoordinator;
        parent->DismissFlowCoordinator(
            youngest, HMUI::ViewController::AnimationDirection::Horizontal,
            BSML::MakeSystemAction([level]() {
                Installer::OpenLevel(level);
            }),
            false);
    } else {
        Installer::OpenLevel(level);
    }
}

void FeedViewController::PlaySelected() {
    if (gBusyPlaying) return;
    if (gSelected < 0 || gSelected >= static_cast<int>(gEntries.size())) return;
    auto entry = gEntries[gSelected];
    if (entry.songHash.empty()) return;

    if (auto level = Installer::GetInstalledLevel(entry.songHash)) {
        LaunchLevel(level);
        return;
    }

    gBusyPlaying = true;
    if (playButton) playButton->set_interactable(false);
    if (playButtonText) playButtonText->set_text("Downloading...");

    auto weakSelf = UnityW<FeedViewController>(this);
    Installer::DownloadAndInstallAsync(entry.songHash, [weakSelf, entry](bool success, std::string error) {
        BSML::MainThreadScheduler::Schedule([weakSelf, entry, success, error = std::move(error)] {
            if (!success) {
                gBusyPlaying = false;
                if (weakSelf) {
                    if (weakSelf->playButtonText) weakSelf->playButtonText->set_text("Download & Play");
                    if (weakSelf->playButton) weakSelf->playButton->set_interactable(true);
                    if (weakSelf->detailText) weakSelf->detailText->set_text("<color=#ff5555>" + error + "</color>");
                }
                return;
            }

            if (weakSelf && weakSelf->playButtonText)
                weakSelf->playButtonText->set_text("Installing...");
            SongCore::API::Loading::RefreshSongs(false);
            SongCore::API::Loading::RefreshLevelPacks();

            // Same grace period the BeatLeader mod uses before opening.
            BSML::MainThreadScheduler::ScheduleAfterTime(5, [weakSelf, entry]() mutable {
                gBusyPlaying = false;
                auto level = Installer::GetInstalledLevel(entry.songHash);
                if (!weakSelf) return;
                if (weakSelf->playButton) weakSelf->playButton->set_interactable(true);
                if (level) {
                    if (weakSelf->playButtonText) weakSelf->playButtonText->set_text("Play");
                    weakSelf->LaunchLevel(level);
                } else {
                    if (weakSelf->playButtonText) weakSelf->playButtonText->set_text("Download & Play");
                    if (weakSelf->detailText)
                        weakSelf->detailText->set_text("Downloaded! The song is still loading — it will appear in Custom Levels shortly.");
                }
            });
        });
    });
}

void FeedViewController::Refresh() {
    if (refreshInFlight.load()) return;

    std::string playerInput = getModConfig().PlayerId.GetValue();

    refreshInFlight.store(true);
    int generation = ++refreshGeneration;
    auto weakSelf = UnityW<FeedViewController>(this);

    if (statusText) statusText->set_text("Loading...");

    int maxPlayers = std::clamp(getModConfig().MaxPlayers.GetValue(), 1, 50);
    int scoresPerPlayer = std::clamp(getModConfig().ScoresPerPlayer.GetValue(), 1, 10);

    FetchFeedAsync(
        playerInput, maxPlayers, scoresPerPlayer,
        [weakSelf, generation](std::string progress) {
            BSML::MainThreadScheduler::Schedule([weakSelf, generation, progress = std::move(progress)] {
                if (generation != refreshGeneration.load()) return;
                if (weakSelf && weakSelf->statusText)
                    weakSelf->statusText->set_text(progress);
            });
        },
        [weakSelf, generation](FeedResult result) {
            refreshInFlight.store(false);
            BSML::MainThreadScheduler::Schedule([weakSelf, generation, result = std::move(result)]() mutable {
                if (generation != refreshGeneration.load()) return;
                if (!weakSelf) return;

                if (!result.success) {
                    gEntries.clear();
                    gPlayers.clear();
                    weakSelf->RebuildFilter();
                    weakSelf->RebuildList();
                    if (weakSelf->statusText)
                        weakSelf->statusText->set_text(result.error);
                    return;
                }

                gEntries = std::move(result.entries);
                gPlayers.clear();
                for (auto const& e : gEntries) {
                    if (std::find(gPlayers.begin(), gPlayers.end(), e.playerName) == gPlayers.end())
                        gPlayers.push_back(e.playerName);
                }
                // Drop a stale filter if that player vanished from the feed.
                if (!gPlayerFilter.empty() && std::find(gPlayers.begin(), gPlayers.end(), gPlayerFilter) == gPlayers.end())
                    gPlayerFilter.clear();

                weakSelf->RebuildFilter();
                weakSelf->RebuildList();
            });
        });
}

void FeedViewController::DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling) {
    if (firstActivation) {
        get_gameObject()->AddComponent<HMUI::Touchable*>();

        auto self = this;

        // Vertical stack pinned to the TOP of the view so it grows downward.
        // (Centered + ContentSizeFitter overflowed above the screen panel.)
        auto root = BSML::Lite::CreateVerticalLayoutGroup(get_transform());
        root->set_childControlWidth(true);
        root->set_childControlHeight(true);
        root->set_childForceExpandWidth(false);
        root->set_childForceExpandHeight(false);
        root->set_childAlignment(UnityEngine::TextAnchor::UpperCenter);
        root->set_spacing(0.5f);
        auto rootFitter = root->get_gameObject()->AddComponent<UnityEngine::UI::ContentSizeFitter*>();
        rootFitter->set_verticalFit(UnityEngine::UI::ContentSizeFitter::FitMode::PreferredSize);
        auto rootRect = root->GetComponent<UnityEngine::RectTransform*>();
        rootRect->set_anchorMin({0.5f, 1.0f});
        rootRect->set_anchorMax({0.5f, 1.0f});
        rootRect->set_pivot({0.5f, 1.0f});
        rootRect->set_anchoredPosition({0.0f, 0.0f});
        auto parent = root->get_transform();

        // Row 1: ID input + refresh.
        auto topRow = BSML::Lite::CreateHorizontalLayoutGroup(parent);
        topRow->set_childControlWidth(true);
        topRow->set_childControlHeight(true);
        topRow->set_childForceExpandWidth(false);
        topRow->set_spacing(2.0f);
        auto input = BSML::Lite::CreateStringSetting(topRow->get_transform(), "BeatLeader ID or alias", getModConfig().PlayerId.GetValue(),
            [](StringW value) {
                getModConfig().PlayerId.SetValue(static_cast<std::string>(value));
            });
        auto inputElement = input->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        inputElement->set_preferredWidth(70.0f);

        BSML::Lite::CreateUIButton(topRow->get_transform(), "Refresh", [self]() {
            self->Refresh();
        });

        // Row 2: player filter (dropdown recreated after each refresh).
        auto filterRow = BSML::Lite::CreateHorizontalLayoutGroup(parent);
        filterRow->set_childControlWidth(true);
        filterRow->set_childControlHeight(true);
        filterRow->set_childForceExpandWidth(false);
        auto filterElement = filterRow->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        filterElement->set_preferredWidth(90.0f);
        filterContainer = filterRow->get_transform();

        statusText = BSML::Lite::CreateText(parent, "");
        statusText->set_fontSize(3.0f);
        statusText->set_alignment(TMPro::TextAlignmentOptions::Center);
        auto statusElement = statusText->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        statusElement->set_preferredWidth(95.0f);
        statusElement->set_preferredHeight(5.0f);

        // The scrollable list carries its own LayoutElement sized from the
        // sizeDelta we pass, so it slots into the stack as a normal child.
        listData = BSML::Lite::CreateScrollableList(parent, {0.0f, 0.0f}, {95.0f, 50.0f}, [self](int idx) {
            self->OnCellClicked(idx);
        });

        // Detail modal with the play button.
        detailModal = BSML::Lite::CreateModal(get_transform(), {75.0f, 45.0f}, nullptr, true);
        auto modalLayout = BSML::Lite::CreateVerticalLayoutGroup(detailModal->get_transform());
        modalLayout->set_childControlWidth(true);
        modalLayout->set_childControlHeight(true);
        modalLayout->set_childForceExpandWidth(true);
        modalLayout->set_childForceExpandHeight(false);
        modalLayout->set_spacing(2.0f);
        modalLayout->set_padding(UnityEngine::RectOffset::New_ctor(3, 3, 3, 3));
        detailText = BSML::Lite::CreateText(modalLayout->get_transform(), "");
        detailText->set_fontSize(3.4f);
        detailText->set_enableWordWrapping(true);
        playButton = BSML::Lite::CreateUIButton(modalLayout->get_transform(), "Play", [self]() {
            self->PlaySelected();
        });
        playButtonText = playButton->GetComponentInChildren<TMPro::TextMeshProUGUI*>();
    }

    Refresh();
}
