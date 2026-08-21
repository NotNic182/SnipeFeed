#include "FeedViewController.hpp"
#include "Feed.hpp"
#include "FeedCell.hpp"
#include "Format.hpp"
#include "ModConfig.hpp"
#include "SongInstaller.hpp"
#include "SpriteCache.hpp"
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
    // Plain data, deliberately file-static: it survives BSML re-creating the
    // view, which is what lets the feed persist across menu visits.
    struct FeedState {
        std::atomic<int> refreshGeneration{0};
        std::atomic<bool> refreshInFlight{false};
        std::vector<FeedEntry> entries;
        std::vector<int> visible;           // list row -> entries index
        std::vector<std::string> players;   // unique player names, feed order
        std::vector<std::string> filterNames; // owned strings backing the dropdown
        std::string playerFilter;           // empty = all players
        int selected = -1;
        bool busyPlaying = false;
        long long lastFetchTime = 0;        // unix time of last successful fetch
    };
    FeedState state;

    constexpr auto FILTER_ALL = "All players";
    constexpr long long REFRESH_MAX_AGE_SECONDS = 120;
}

void FeedViewController::RebuildFilter() {
    if (!filterContainer) return;

    for (int i = filterContainer->get_childCount() - 1; i >= 0; i--)
        UnityEngine::Object::Destroy(filterContainer->GetChild(i)->get_gameObject());

    state.filterNames.clear();
    state.filterNames.push_back(FILTER_ALL);
    for (auto const& name : state.players)
        state.filterNames.push_back(name);
    std::vector<std::string_view> views(state.filterNames.begin(), state.filterNames.end());

    std::string current = state.playerFilter.empty() ? FILTER_ALL : state.playerFilter;

    auto self = this;
    BSML::Lite::CreateDropdown(filterContainer, "Player", current, views, [self](StringW value) {
        std::string selected = static_cast<std::string>(value);
        state.playerFilter = (selected == FILTER_ALL) ? "" : selected;
        self->RebuildList();
    });
}

void FeedViewController::RebuildList() {
    if (!listData || !listData->tableView) return;

    state.visible.clear();
    for (int i = 0; i < static_cast<int>(state.entries.size()); i++) {
        if (!state.playerFilter.empty() && state.entries[i].playerName != state.playerFilter)
            continue;
        state.visible.push_back(i);
    }
    listData->tableView->ReloadData();
    listData->tableView->ClearSelection();

    if (statusText) {
        if (state.entries.empty()) {
            // Keep whatever error/progress message is already showing.
        } else if (state.playerFilter.empty()) {
            statusText->set_text(std::format("{} recent scores. Newest first — go snipe!", state.entries.size()));
        } else {
            statusText->set_text(std::format("{} of {} scores by {}", state.visible.size(), state.entries.size(), state.playerFilter));
        }
    }
}

HMUI::TableCell* FeedViewController::CellForIdx(HMUI::TableView* tableView, int idx) {
    auto cell = FeedCell::GetCell(tableView);
    if (idx >= 0 && idx < static_cast<int>(state.visible.size()))
        cell->SetData(state.entries[state.visible[idx]]);
    return cell;
}

float FeedViewController::CellSize() { return FeedCell::CELL_HEIGHT; }

int FeedViewController::NumberOfCells() { return static_cast<int>(state.visible.size()); }

void FeedViewController::OnCellClicked(int listIdx) {
    if (listData && listData->tableView)
        listData->tableView->ClearSelection();
    if (listIdx < 0 || listIdx >= static_cast<int>(state.visible.size())) return;
    state.selected = state.visible[listIdx];
    auto const& e = state.entries[state.selected];

    if (detailText) {
        std::string info = "<b>" + e.songName + "</b>";
        if (!e.songAuthor.empty())
            info += "\n<size=80%><color=#888888>" + e.songAuthor + "</color></size>";
        if (!e.difficulty.empty() || e.stars > 0.0f)
            info += "\n<size=85%>" + Format::SongLine(e).substr(e.songName.size()) + "</size>";
        info += "\n" + Format::StatsLine(e);
        info += "\n<size=75%><color=#777777>" + Format::TimeAgo(e.timepost) + "</color></size>";
        detailText->set_text(info);
    }
    if (modalPlayerText) modalPlayerText->set_text(e.playerName);

    // Images, with the same stale-guard the cells use.
    modalPendingCover = e.coverUrl;
    modalPendingAvatar = e.avatarUrl;
    if (modalCover) {
        modalCover->set_sprite(nullptr);
        modalCover->set_color({1.0f, 1.0f, 1.0f, 0.15f});
    }
    if (modalAvatar) {
        modalAvatar->set_sprite(nullptr);
        modalAvatar->set_color({1.0f, 1.0f, 1.0f, 0.15f});
    }
    auto weakSelf = UnityW<FeedViewController>(this);
    if (!e.coverUrl.empty()) {
        SpriteCache::GetSprite(e.coverUrl, [weakSelf, url = e.coverUrl](UnityEngine::Sprite* sprite) {
            if (!weakSelf || !weakSelf->modalCover) return;
            if (!weakSelf->modalPendingCover || static_cast<std::string>(weakSelf->modalPendingCover) != url) return;
            weakSelf->modalCover->set_sprite(sprite);
            weakSelf->modalCover->set_color({1.0f, 1.0f, 1.0f, 1.0f});
        });
    }
    if (!e.avatarUrl.empty()) {
        SpriteCache::GetSprite(e.avatarUrl, [weakSelf, url = e.avatarUrl](UnityEngine::Sprite* sprite) {
            if (!weakSelf || !weakSelf->modalAvatar) return;
            if (!weakSelf->modalPendingAvatar || static_cast<std::string>(weakSelf->modalPendingAvatar) != url) return;
            weakSelf->modalAvatar->set_sprite(sprite);
            weakSelf->modalAvatar->set_color({1.0f, 1.0f, 1.0f, 1.0f});
        });
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
        playButton->set_interactable(!e.songHash.empty() && !state.busyPlaying);

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
    if (state.busyPlaying) return;
    if (state.selected < 0 || state.selected >= static_cast<int>(state.entries.size())) return;
    auto entry = state.entries[state.selected];
    if (entry.songHash.empty()) return;

    if (auto level = Installer::GetInstalledLevel(entry.songHash)) {
        LaunchLevel(level);
        return;
    }

    state.busyPlaying = true;
    if (playButton) playButton->set_interactable(false);
    if (playButtonText) playButtonText->set_text("Downloading...");

    auto weakSelf = UnityW<FeedViewController>(this);
    Installer::DownloadAndInstallAsync(entry.songHash, [weakSelf, entry](bool success, std::string error) {
        BSML::MainThreadScheduler::Schedule([weakSelf, entry, success, error = std::move(error)] {
            if (!success) {
                state.busyPlaying = false;
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
                state.busyPlaying = false;
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
    if (state.refreshInFlight.load()) return;

    std::string playerInput = getModConfig().PlayerId.GetValue();

    state.refreshInFlight.store(true);
    int generation = ++state.refreshGeneration;
    auto weakSelf = UnityW<FeedViewController>(this);

    if (statusText) statusText->set_text("Loading...");

    int maxPlayers = std::clamp(getModConfig().MaxPlayers.GetValue(), 1, 50);
    int scoresPerPlayer = std::clamp(getModConfig().ScoresPerPlayer.GetValue(), 1, 10);

    FetchFeedAsync(
        playerInput, maxPlayers, scoresPerPlayer,
        [weakSelf, generation](std::string progress) {
            BSML::MainThreadScheduler::Schedule([weakSelf, generation, progress = std::move(progress)] {
                if (generation != state.refreshGeneration.load()) return;
                if (weakSelf && weakSelf->statusText)
                    weakSelf->statusText->set_text(progress);
            });
        },
        [weakSelf, generation](FeedResult result) {
            state.refreshInFlight.store(false);
            BSML::MainThreadScheduler::Schedule([weakSelf, generation, result = std::move(result)]() mutable {
                if (generation != state.refreshGeneration.load()) return;
                if (!weakSelf) return;

                if (!result.success) {
                    state.entries.clear();
                    state.players.clear();
                    weakSelf->RebuildFilter();
                    weakSelf->RebuildList();
                    if (weakSelf->statusText)
                        weakSelf->statusText->set_text(result.error);
                    return;
                }

                state.entries = std::move(result.entries);
                state.players.clear();
                for (auto const& e : state.entries) {
                    if (std::find(state.players.begin(), state.players.end(), e.playerName) == state.players.end())
                        state.players.push_back(e.playerName);
                }
                // Drop a stale filter if that player vanished from the feed.
                if (!state.playerFilter.empty() && std::find(state.players.begin(), state.players.end(), state.playerFilter) == state.players.end())
                    state.playerFilter.clear();

                state.lastFetchTime = static_cast<long long>(std::time(nullptr));

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
        // CreateScrollableList wires onCellWithIdxClicked to the TableView's
        // own didSelectCellWithIdxEvent (a field on HMUI::TableView itself,
        // confirmed via extern/includes/bs-cordl/include/HMUI/zzzz__TableView_def.hpp
        // — offset 0x50, independent of _dataSource). That event fires
        // whenever a cell reports selection regardless of which IDataSource
        // is installed, so swapping SetDataSource below does not disturb it
        // and no extra add_didSelectCellWithIdxEvent wiring is needed here.
        listData = BSML::Lite::CreateScrollableList(parent, {0.0f, 0.0f}, {95.0f, 50.0f}, [self](int idx) {
            self->OnCellClicked(idx);
        });
        listData->tableView->SetDataSource(reinterpret_cast<HMUI::TableView::IDataSource*>(this), false);

        // Detail modal: cover art + song text on top, avatar + player row,
        // stats, then the play button. Same data as before, card look.
        detailModal = BSML::Lite::CreateModal(get_transform(), {90.0f, 52.0f}, nullptr, true);
        auto modalLayout = BSML::Lite::CreateVerticalLayoutGroup(detailModal->get_transform());
        modalLayout->set_childControlWidth(true);
        modalLayout->set_childControlHeight(true);
        modalLayout->set_childForceExpandWidth(true);
        modalLayout->set_childForceExpandHeight(false);
        modalLayout->set_spacing(1.5f);
        modalLayout->set_padding(UnityEngine::RectOffset::New_ctor(3, 3, 3, 3));

        auto coverRow = BSML::Lite::CreateHorizontalLayoutGroup(modalLayout->get_transform());
        coverRow->set_childControlWidth(true);
        coverRow->set_childControlHeight(true);
        coverRow->set_childForceExpandWidth(false);
        coverRow->set_spacing(3.0f);
        modalCover = BSML::Lite::CreateImage(coverRow->get_transform(), nullptr, {0.0f, 0.0f}, {20.0f, 20.0f});
        auto coverElement = modalCover->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        coverElement->set_preferredWidth(20.0f);
        coverElement->set_preferredHeight(20.0f);
        modalCover->set_preserveAspect(true);
        detailText = BSML::Lite::CreateText(coverRow->get_transform(), "");
        detailText->set_fontSize(3.4f);
        detailText->set_enableWordWrapping(true);

        auto playerRow = BSML::Lite::CreateHorizontalLayoutGroup(modalLayout->get_transform());
        playerRow->set_childControlWidth(true);
        playerRow->set_childControlHeight(true);
        playerRow->set_childForceExpandWidth(false);
        playerRow->set_spacing(1.5f);
        modalAvatar = BSML::Lite::CreateImage(playerRow->get_transform(), nullptr, {0.0f, 0.0f}, {4.0f, 4.0f});
        auto avatarElement = modalAvatar->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
        avatarElement->set_preferredWidth(4.0f);
        avatarElement->set_preferredHeight(4.0f);
        modalAvatar->set_preserveAspect(true);
        modalPlayerText = BSML::Lite::CreateText(playerRow->get_transform(), "");
        modalPlayerText->set_fontSize(3.2f);

        playButton = BSML::Lite::CreateUIButton(modalLayout->get_transform(), "Play", [self]() {
            self->PlaySelected();
        });
        playButtonText = playButton->GetComponentInChildren<TMPro::TextMeshProUGUI*>();
    }

    bool stale = state.entries.empty()
        || (static_cast<long long>(std::time(nullptr)) - state.lastFetchTime) > REFRESH_MAX_AGE_SECONDS;
    if (stale) {
        Refresh();
    } else {
        RebuildFilter();
        RebuildList();
    }
}
