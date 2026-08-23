#include "FeedView.hpp"
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
#include "bsml/shared/Helpers/utilities.hpp"

#include "songcore/shared/SongCore.hpp"

#include "GlobalNamespace/BeatmapLevelPack.hpp"
#include "GlobalNamespace/LevelCollectionNavigationController.hpp"
#include "GlobalNamespace/LevelFilteringNavigationController.hpp"
#include "GlobalNamespace/MainFlowCoordinator.hpp"
#include "HMUI/FlowCoordinator.hpp"
#include "HMUI/ScrollView.hpp"
#include "HMUI/ViewController.hpp"
#include "UnityEngine/Color.hpp"
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

DEFINE_TYPE(SnipeFeed, FeedView);

using namespace SnipeFeed;

namespace {
    // Plain data, deliberately file-static: it survives the tab GameObject
    // being recreated, which is what lets the feed persist across menu
    // visits.
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

    // Shared width for the header row, the status line, and the list, so
    // everything lines up on the same left/right edges. The gameplay setup
    // panel's tab area is far smaller than a full screen (Qounters++ sizes
    // its rows to 85 units there), so everything below is sized to fit
    // roughly 90x46.
    constexpr float CONTENT_WIDTH = 90.0f;
    // Vertical space reserved above the list for the control row + status
    // line. The list itself stretches from here to the tab's real bottom
    // edge (anchor-driven), so it fills whatever height the gameplay setup
    // panel actually provides instead of guessing it.
    constexpr float HEADER_HEIGHT = 13.5f;
}

void FeedView::TabActivated(UnityEngine::GameObject* gameObject, bool firstActivation) {
    auto view = gameObject->GetComponent<FeedView*>();
    if (!view) view = gameObject->AddComponent<FeedView*>();
    view->DidActivate(firstActivation);
}

void FeedView::RebuildFilter() {
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
    // Empty label: the value itself ("All players" / a player name) says
    // what the dropdown is.
    BSML::Lite::CreateDropdown(filterContainer, "", current, views, [self](StringW value) {
        std::string selected = static_cast<std::string>(value);
        state.playerFilter = (selected == FILTER_ALL) ? "" : selected;
        self->RebuildList();
    });
}

void FeedView::RebuildList() {
    if (!listData || !listData->tableView) return;

    state.visible.clear();
    for (int i = 0; i < static_cast<int>(state.entries.size()); i++) {
        if (!state.playerFilter.empty() && state.entries[i].playerName != state.playerFilter)
            continue;
        state.visible.push_back(i);
    }
    listData->tableView->ReloadData();
    listData->tableView->ClearSelection();
    // Re-evaluate the page arrows' enabled state against the new content.
    if (auto scrollView = listData->tableView->_scrollView)
        scrollView->RefreshButtons();

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

HMUI::TableCell* FeedView::CellForIdx(HMUI::TableView* tableView, int idx) {
    auto cell = FeedCell::GetCell(tableView);
    if (idx >= 0 && idx < static_cast<int>(state.visible.size()))
        cell->SetData(state.entries[state.visible[idx]], idx + 1);
    return cell;
}

float FeedView::CellSize() { return FeedCell::CELL_HEIGHT; }

int FeedView::NumberOfCells() { return static_cast<int>(state.visible.size()); }

void FeedView::OnCellClicked(int listIdx) {
    if (listData && listData->tableView)
        listData->tableView->ClearSelection();
    if (listIdx < 0 || listIdx >= static_cast<int>(state.visible.size())) return;
    state.selected = state.visible[listIdx];
    auto const& e = state.entries[state.selected];

    if (detailText) {
        std::string info = "<size=140%><b>" + e.songName + "</b></size>";
        if (!e.songAuthor.empty() || !e.mapper.empty()) {
            std::string byline = e.songAuthor;
            if (!e.mapper.empty())
                byline += (byline.empty() ? "[" : " [") + e.mapper + "]";
            info += "\n<color=#888888>" + byline + "</color>";
        }
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
    auto weakSelf = UnityW<FeedView>(this);
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

    // In a lobby (no song-select screen open) we can download maps but not
    // launch them — launching would require hijacking the lobby's flow.
    auto nav = UnityEngine::Object::FindObjectOfType<GlobalNamespace::LevelCollectionNavigationController*>();
    bool canLaunchHere = nav && nav->get_isActiveAndEnabled();
    bool installed = !e.songHash.empty() && Installer::GetInstalledLevel(e.songHash);

    if (playButtonText) {
        if (e.songHash.empty())
            playButtonText->set_text("Not a custom song");
        else if (installed)
            playButtonText->set_text(canLaunchHere ? "Play" : "In Custom Levels");
        else
            playButtonText->set_text(canLaunchHere ? "Download & Play" : "Download");
    }
    if (playButton)
        playButton->set_interactable(!e.songHash.empty() && !state.busyPlaying && (canLaunchHere || !installed));

    if (detailModal)
        detailModal->Show();
}

void FeedView::LaunchLevel(GlobalNamespace::BeatmapLevel* level) {
    if (!level) return;
    // Hide instantly (animated=false): an animated hide would be frozen
    // mid-flight by the flow transitions below, leaving the modal stuck on
    // screen when the gameplay setup panel comes back.
    if (detailModal) detailModal->HMUI::ModalView::Hide(false, nullptr);

    // This tab only exists inside a song-selection screen, so the level
    // pickers are alive RIGHT NOW — select the song in place instead of
    // backing out to the main menu and re-entering Solo. Works in solo,
    // party, and multiplayer song select alike (same controllers).
    auto collectionNav = UnityEngine::Object::FindObjectOfType<GlobalNamespace::LevelCollectionNavigationController*>();
    if (collectionNav && collectionNav->get_isActiveAndEnabled()) {
        auto filterNav = UnityEngine::Object::FindObjectOfType<GlobalNamespace::LevelFilteringNavigationController*>();
        if (filterNav && filterNav->get_isActiveAndEnabled()) {
            // Make sure the Custom Levels pack is the one being shown; the
            // level select below is deferred by the controller until the
            // pack finishes presenting (_beatmapLevelToBeSelectedAfterPresent).
            if (auto pack = SongCore::API::Loading::GetCustomLevelPack())
                filterNav->SelectAnnotatedBeatmapLevelCollection(static_cast<GlobalNamespace::BeatmapLevelPack*>(pack));
        }
        collectionNav->SelectLevel(level);
        return;
    }

    // No song-select screen is open (e.g. a multiplayer / Multiplayer+
    // lobby). NEVER hijack the solo flow from here — dismissing flow
    // coordinators under an active lobby corrupts the menu state (main
    // menu while still "in" the room, solo playback inside the lobby).
    // The map is installed; just point the player at it.
    if (statusText)
        statusText->set_text("Map installed — pick it in the song picker (Custom Levels).");
}

void FeedView::PlaySelected() {
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

    auto weakSelf = UnityW<FeedView>(this);
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

void FeedView::Refresh() {
    if (state.refreshInFlight.load()) {
        // A fetch from a previous (possibly destroyed) view is still running.
        // Give a freshly recreated view something other than a blank screen
        // while it completes.
        if (statusText) statusText->set_text("Loading...");
        return;
    }

    std::string playerInput = getModConfig().PlayerId.GetValue();

    state.refreshInFlight.store(true);
    int generation = ++state.refreshGeneration;
    auto weakSelf = UnityW<FeedView>(this);

    if (statusText) statusText->set_text("Loading...");

    int maxPlayers = std::clamp(getModConfig().MaxPlayers.GetValue(), 1, 50);
    int scoresPerPlayer = std::clamp(getModConfig().ScoresPerPlayer.GetValue(), 1, 10);
    int feedCount = std::clamp(getModConfig().FeedCount.GetValue(), 10, 100);

    FetchFeedAsync(
        playerInput, maxPlayers, scoresPerPlayer, feedCount,
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

                if (!result.success) {
                    state.entries.clear();
                    state.players.clear();
                    state.selected = -1;
                    if (weakSelf) {
                        weakSelf->RebuildFilter();
                        weakSelf->RebuildList();
                        if (weakSelf->statusText)
                            weakSelf->statusText->set_text(result.error);
                    }
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
                // Stale selection would otherwise index into the new entries
                // array and could launch the wrong song.
                state.selected = -1;
                // A successful refresh means the feed (and its images) are
                // current again — let previously-failed images retry.
                SnipeFeed::SpriteCache::ClearFailures();

                if (weakSelf) {
                    weakSelf->RebuildFilter();
                    weakSelf->RebuildList();
                    if (weakSelf->detailModal) weakSelf->detailModal->Hide();
                }
            });
        });
}

void FeedView::BuildUI() {
    auto self = this;

    // Vertical stack pinned to the TOP of the tab area so it grows downward.
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

    // Single control row: player filter dropdown on the left, scores-to-
    // pull setting and refresh on the right.
    auto topRow = BSML::Lite::CreateHorizontalLayoutGroup(parent);
    topRow->set_childControlWidth(true);
    topRow->set_childControlHeight(true);
    topRow->set_childForceExpandWidth(false);
    topRow->set_childAlignment(UnityEngine::TextAnchor::MiddleLeft);
    topRow->set_spacing(1.0f);
    auto topRowElement = topRow->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
    topRowElement->set_preferredWidth(CONTENT_WIDTH);

    // Filter dropdown holder (dropdown recreated after each refresh);
    // flexes so the remaining controls land on the row's right edge.
    auto filterHolder = BSML::Lite::CreateHorizontalLayoutGroup(topRow->get_transform());
    filterHolder->set_childControlWidth(true);
    filterHolder->set_childControlHeight(true);
    filterHolder->set_childForceExpandWidth(false);
    auto filterElement = filterHolder->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
    filterElement->set_preferredWidth(34.0f);
    filterElement->set_flexibleWidth(1000.0f);
    filterContainer = filterHolder->get_transform();

    auto countSetting = BSML::Lite::CreateIncrementSetting(topRow->get_transform(), "Scores", 0, 10.0f,
        static_cast<float>(std::clamp(getModConfig().FeedCount.GetValue(), 10, 100)), 10.0f, 100.0f,
        [](float value) {
            getModConfig().FeedCount.SetValue(static_cast<int>(value));
        });
    auto countElement = countSetting->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
    countElement->set_preferredWidth(34.0f);

    BSML::Lite::CreateUIButton(topRow->get_transform(), "Refresh", [self]() {
        self->Refresh();
    });

    // Secondary info line: small and muted so the score rows below stay
    // the visual focus.
    statusText = BSML::Lite::CreateText(parent, "");
    statusText->set_fontSize(2.4f);
    statusText->set_color({0.62f, 0.68f, 0.76f, 1.0f});
    statusText->set_alignment(TMPro::TextAlignmentOptions::Center);
    auto statusElement = statusText->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
    statusElement->set_preferredWidth(CONTENT_WIDTH);
    statusElement->set_preferredHeight(3.5f);

    // The scrollable list carries its own LayoutElement sized from the
    // sizeDelta we pass, so it slots into the stack as a normal child.
    // CreateScrollableList wires onCellWithIdxClicked to the TableView's
    // own didSelectCellWithIdxEvent (a field on HMUI::TableView itself,
    // confirmed via extern/includes/bs-cordl/include/HMUI/zzzz__TableView_def.hpp
    // — offset 0x50, independent of _dataSource). That event fires
    // whenever a cell reports selection regardless of which IDataSource
    // is installed, so swapping SetDataSource below does not disturb it
    // and no extra add_didSelectCellWithIdxEvent wiring is needed here.
    // Size the list to the tab's MEASURED height: the scroll viewport
    // inside CreateScrollableList is sized once at creation and does not
    // follow later RectTransform changes, so the height must be right up
    // front. DidActivate defers BuildUI until the rect reports a real
    // height; 34 is only the last-resort fallback.
    float tabHeight = 0.0f;
    if (auto tabRect = GetComponent<UnityEngine::RectTransform*>())
        tabHeight = tabRect->get_rect().get_height();
    float listHeight = tabHeight > 30.0f
        ? std::clamp(tabHeight - HEADER_HEIGHT - 1.0f, 20.0f, 200.0f)
        : 34.0f;
    SnipeFeedLogger.info("Gameplay setup tab height {:.1f}, list height {:.1f}", tabHeight, listHeight);

    listData = BSML::Lite::CreateScrollableList(parent, {0.0f, 0.0f}, {CONTENT_WIDTH, listHeight}, [self](int idx) {
        self->OnCellClicked(idx);
    });
    listData->tableView->SetDataSource(reinterpret_cast<HMUI::TableView::IDataSource*>(this), false);

    // Center the page up/down arrows over the rows; stock placement
    // leaves them offset to one side of the viewport. SetAsLastSibling
    // keeps them ABOVE the table viewport in raycast order — without it
    // the rows' Touchable swallows the pointer and the arrows never
    // receive the click (joystick scrolling works, arrows appear dead).
    if (auto scrollView = listData->tableView->_scrollView) {
        for (auto button : {scrollView->_pageUpButton, scrollView->_pageDownButton}) {
            if (!button) continue;
            auto rect = button->GetComponent<UnityEngine::RectTransform*>();
            rect->set_anchoredPosition({0.0f, rect->get_anchoredPosition().y});
            button->get_transform()->SetAsLastSibling();
        }
    }

    // Detail modal: cover art + song text on top, avatar + player row,
    // stats, then the play button. Sized to stay inside the gameplay
    // setup panel.
    detailModal = BSML::Lite::CreateModal(get_transform(), {76.0f, 46.0f}, nullptr, true);
    auto modalLayout = BSML::Lite::CreateVerticalLayoutGroup(detailModal->get_transform());
    modalLayout->set_childControlWidth(true);
    modalLayout->set_childControlHeight(true);
    modalLayout->set_childForceExpandWidth(true);
    modalLayout->set_childForceExpandHeight(false);
    modalLayout->set_spacing(1.0f);
    modalLayout->set_padding(UnityEngine::RectOffset::New_ctor(2, 2, 2, 2));

    auto coverRow = BSML::Lite::CreateHorizontalLayoutGroup(modalLayout->get_transform());
    coverRow->set_childControlWidth(true);
    coverRow->set_childControlHeight(true);
    coverRow->set_childForceExpandWidth(false);
    coverRow->set_spacing(2.0f);
    modalCover = BSML::Lite::CreateImage(coverRow->get_transform(), nullptr, {0.0f, 0.0f}, {16.0f, 16.0f});
    auto coverElement = modalCover->get_gameObject()->AddComponent<UnityEngine::UI::LayoutElement*>();
    coverElement->set_preferredWidth(16.0f);
    coverElement->set_preferredHeight(16.0f);
    modalCover->set_preserveAspect(true);
    detailText = BSML::Lite::CreateText(coverRow->get_transform(), "");
    detailText->set_fontSize(3.0f);
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
    modalPlayerText->set_fontSize(3.0f);

    playButton = BSML::Lite::CreateUIButton(modalLayout->get_transform(), "Play", [self]() {
        self->PlaySelected();
    });
    playButtonText = playButton->GetComponentInChildren<TMPro::TextMeshProUGUI*>();
}

void FeedView::DidActivate(bool firstActivation) {
    if (!listData) {
        // The tab's RectTransform may not have its final height on the very
        // first activation frame; the list viewport must be created at the
        // right size (see BuildUI), so wait a frame until layout settles.
        float height = 0.0f;
        if (auto rect = GetComponent<UnityEngine::RectTransform*>())
            height = rect->get_rect().get_height();
        if (height <= 30.0f && buildAttempts < 5) {
            buildAttempts++;
            auto weakSelf = UnityW<FeedView>(this);
            BSML::MainThreadScheduler::Schedule([weakSelf]() mutable {
                if (weakSelf) weakSelf->DidActivate(false);
            });
            return;
        }
        BuildUI();
    }

    // Defensive: if a hide was ever interrupted (menu hop, tab switch),
    // clear the modal the moment the tab shows again.
    if (detailModal) detailModal->HMUI::ModalView::Hide(false, nullptr);

    bool stale = state.entries.empty()
        || (static_cast<long long>(std::time(nullptr)) - state.lastFetchTime) > REFRESH_MAX_AGE_SECONDS;
    if (stale) {
        Refresh();
    } else {
        RebuildFilter();
        RebuildList();
    }
}
