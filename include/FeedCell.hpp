#pragma once

#include "Feed.hpp"

#include "custom-types/shared/macros.hpp"
#include "HMUI/ImageView.hpp"
#include "HMUI/SelectableCell.hpp"
#include "HMUI/TableCell.hpp"
#include "HMUI/TableView.hpp"
#include "TMPro/TextMeshProUGUI.hpp"

// One feed entry as a BeatLeader-style card: cover art left; avatar +
// player + time-ago, song line, stats line right. Reused by the TableView
// (BetterSongSearch dequeue pattern).
DECLARE_CLASS_CODEGEN(SnipeFeed, FeedCell, HMUI::TableCell) {
    DECLARE_OVERRIDE_METHOD_MATCH(void, SelectionDidChange, &HMUI::SelectableCell::SelectionDidChange, HMUI::SelectableCell::TransitionType transitionType);
    DECLARE_OVERRIDE_METHOD_MATCH(void, HighlightDidChange, &HMUI::SelectableCell::HighlightDidChange, HMUI::SelectableCell::TransitionType transitionType);
    DECLARE_OVERRIDE_METHOD_MATCH(void, WasPreparedForReuse, &HMUI::TableCell::WasPreparedForReuse);

    // Bound by BSML ids in CELL_BSML.
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, bgContainer);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, coverImage);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, avatarImage);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, playerText);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, timeText);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, songText);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, statsText);

    // Stale-guard: URLs this cell is currently waiting for. A reused cell
    // gets new values before the old sprite callback can land.
    DECLARE_INSTANCE_FIELD(StringW, pendingCoverUrl);
    DECLARE_INSTANCE_FIELD(StringW, pendingAvatarUrl);

   public:
    static constexpr float CELL_HEIGHT = 12.0f;

    static FeedCell* GetCell(HMUI::TableView* tableView);
    void SetData(SnipeFeed::FeedEntry const& entry);

   private:
    void RefreshBackground();
    void ResetImages();
};
