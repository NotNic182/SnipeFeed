#include "FeedCell.hpp"
#include "Format.hpp"
#include "SpriteCache.hpp"

#include "bsml/shared/BSML.hpp"
#include "HMUI/Touchable.hpp"
#include "UnityEngine/Color.hpp"
#include "UnityEngine/GameObject.hpp"

DEFINE_TYPE(SnipeFeed, FeedCell);

using namespace SnipeFeed;

namespace {
    constexpr auto REUSE_ID = "SnipeFeedCellReuse";

    // Card layout, BeatLeader style. ids bind to the DECLARE_INSTANCE_FIELDs.
    constexpr auto CELL_BSML = R"(
<horizontal id='bgContainer' bg='round-rect-panel' bg-color='#00000073' pad='1' spacing='2' horizontal-fit='Unconstrained' child-expand-width='false' child-control-width='true' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='https://raw.githubusercontent.com/RedBrumbler/Quest-BSML-Docs/gh-pages/schema.xsd'>
    <image id='coverImage' pref-width='10' pref-height='10' preserve-aspect='true'/>
    <vertical spacing='0' pref-width='78' child-expand-height='false' child-control-height='true'>
        <horizontal spacing='1' pref-height='4' child-expand-width='false' child-control-width='true'>
            <image id='avatarImage' pref-width='4' pref-height='4' preserve-aspect='true'/>
            <text id='playerText' font-size='3.5' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false' flexible-width='1000'/>
            <text id='timeText' font-size='2.5' color='#888888' align='MidlineRight' word-wrapping='false'/>
        </horizontal>
        <text id='songText' font-size='3.2' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
        <text id='statsText' font-size='3' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
    </vertical>
</horizontal>)";

    // While an image is loading (or failed) the ImageView shows as a dim
    // tile instead of a stark white square.
    constexpr UnityEngine::Color PLACEHOLDER_TINT = {1.0f, 1.0f, 1.0f, 0.15f};
    constexpr UnityEngine::Color LOADED_TINT = {1.0f, 1.0f, 1.0f, 1.0f};

    constexpr float BG_ALPHA_IDLE = 0.45f;
    constexpr float BG_ALPHA_ACTIVE = 0.8f;
}

FeedCell* FeedCell::GetCell(HMUI::TableView* tableView) {
    auto tableCell = tableView->DequeueReusableCellForIdentifier(REUSE_ID);
    if (!tableCell) {
        auto go = UnityEngine::GameObject::New_ctor("SnipeFeedCell");
        auto cell = go->AddComponent<FeedCell*>();
        cell->set_interactable(true);
        cell->set_reuseIdentifier(REUSE_ID);
        BSML::parse_and_construct(CELL_BSML, cell->get_transform(), cell);
        go->AddComponent<HMUI::Touchable*>();
        return cell;
    }
    return tableCell.cast<FeedCell>();
}

void FeedCell::SetData(FeedEntry const& entry) {
    playerText->set_text(entry.playerName);
    timeText->set_text(Format::TimeAgo(entry.timepost));
    songText->set_text(Format::SongLine(entry));
    statsText->set_text(Format::StatsLine(entry));

    ResetImages();

    pendingCoverUrl = entry.coverUrl;
    pendingAvatarUrl = entry.avatarUrl;

    auto self = UnityW<FeedCell>(this);
    if (!entry.coverUrl.empty()) {
        SpriteCache::GetSprite(entry.coverUrl, [self, url = entry.coverUrl](UnityEngine::Sprite* sprite) {
            if (!self || !self->pendingCoverUrl || static_cast<std::string>(self->pendingCoverUrl) != url) return;
            self->coverImage->set_sprite(sprite);
            self->coverImage->set_color(LOADED_TINT);
        });
    }
    if (!entry.avatarUrl.empty()) {
        SpriteCache::GetSprite(entry.avatarUrl, [self, url = entry.avatarUrl](UnityEngine::Sprite* sprite) {
            if (!self || !self->pendingAvatarUrl || static_cast<std::string>(self->pendingAvatarUrl) != url) return;
            self->avatarImage->set_sprite(sprite);
            self->avatarImage->set_color(LOADED_TINT);
        });
    }

    RefreshBackground();
}

void FeedCell::ResetImages() {
    pendingCoverUrl = StringW(nullptr);
    pendingAvatarUrl = StringW(nullptr);
    coverImage->set_sprite(nullptr);
    coverImage->set_color(PLACEHOLDER_TINT);
    avatarImage->set_sprite(nullptr);
    avatarImage->set_color(PLACEHOLDER_TINT);
}

void FeedCell::RefreshBackground() {
    if (!bgContainer) return;
    bool active = get_selected() || get_highlighted();
    bgContainer->set_color({0.0f, 0.0f, 0.0f, active ? BG_ALPHA_ACTIVE : BG_ALPHA_IDLE});
}

void FeedCell::SelectionDidChange(HMUI::SelectableCell::TransitionType) { RefreshBackground(); }
void FeedCell::HighlightDidChange(HMUI::SelectableCell::TransitionType) { RefreshBackground(); }
void FeedCell::WasPreparedForReuse() { ResetImages(); }
