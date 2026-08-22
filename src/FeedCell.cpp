#include "FeedCell.hpp"
#include "Format.hpp"
#include "SpriteCache.hpp"

#include "bsml/shared/BSML.hpp"
#include "HMUI/Touchable.hpp"
#include "UnityEngine/Color.hpp"
#include "UnityEngine/GameObject.hpp"

#include <format>

DEFINE_TYPE(SnipeFeed, FeedCell);

using namespace SnipeFeed;

namespace {
    constexpr auto REUSE_ID = "SnipeFeedCellReuse";

    // Row layout, BeatLeader style, reading left to right: rank, cover,
    // then (avatar + name + time) over one song/stats line, chevron last.
    // ids bind to the DECLARE_INSTANCE_FIELDs.
    constexpr auto CELL_BSML = R"(
<horizontal id='bgContainer' bg='round-rect-panel' bg-color='#00000073' pad='1' spacing='2' horizontal-fit='Unconstrained' child-expand-width='false' child-control-width='true' child-align='MiddleLeft' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='https://raw.githubusercontent.com/RedBrumbler/Quest-BSML-Docs/gh-pages/schema.xsd'>
    <text id='rankText' font-size='3.2' align='Center' word-wrapping='false' pref-width='4'/>
    <image id='coverImage' pref-width='10.5' pref-height='10.5' preserve-aspect='true'/>
    <vertical spacing='0' pref-width='77' flexible-width='1000' child-expand-height='false' child-control-height='true'>
        <horizontal spacing='1.5' pref-height='5.5' child-expand-width='false' child-control-width='true' child-align='MiddleLeft'>
            <image id='avatarImage' pref-width='4.5' pref-height='4.5' preserve-aspect='true'/>
            <text id='playerText' font-size='4' align='MidlineLeft' word-wrapping='false'/>
            <text id='timeText' font-size='2.8' color='#8899AA' align='MidlineLeft' word-wrapping='false' flexible-width='1000'/>
        </horizontal>
        <text id='infoText' font-size='3.2' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
    </vertical>
    <text id='chevronText' font-size='4.5' color='#5A6B7A' align='Center' word-wrapping='false' pref-width='3'/>
</horizontal>)";

    // While an image is loading (or failed) the ImageView shows as a dim
    // tile instead of a stark white square.
    constexpr UnityEngine::Color PLACEHOLDER_TINT = {1.0f, 1.0f, 1.0f, 0.15f};
    constexpr UnityEngine::Color LOADED_TINT = {1.0f, 1.0f, 1.0f, 1.0f};

    constexpr float BG_ALPHA_IDLE = 0.55f;
    constexpr float BG_ALPHA_ACTIVE = 0.8f;

    // Top three ranks get medal-ish colors, the rest stay muted.
    char const* RankColor(int rank) {
        switch (rank) {
            case 1: return "#FFB921";
            case 2: return "#C4CEDC";
            case 3: return "#D98E4A";
            default: return "#5A6B7A";
        }
    }
}

FeedCell* FeedCell::GetCell(HMUI::TableView* tableView) {
    auto tableCell = tableView->DequeueReusableCellForIdentifier(REUSE_ID);
    if (!tableCell) {
        auto go = UnityEngine::GameObject::New_ctor("SnipeFeedCell");
        auto cell = go->AddComponent<FeedCell*>();
        cell->set_interactable(true);
        cell->set_reuseIdentifier(REUSE_ID);
        BSML::parse_and_construct(CELL_BSML, cell->get_transform(), cell);
        cell->chevronText->set_text(">");
        go->AddComponent<HMUI::Touchable*>();
        return cell;
    }
    return tableCell.cast<FeedCell>();
}

void FeedCell::SetData(FeedEntry const& entry, int rank) {
    if (rank <= 3)
        rankText->set_text(std::format("<b><color={}>{}</color></b>", RankColor(rank), rank));
    else
        rankText->set_text(std::format("<color={}>{}</color>", RankColor(rank), rank));
    playerText->set_text("<b>" + entry.playerName + "</b>");
    timeText->set_text(Format::TimeAgo(entry.timepost));
    infoText->set_text(Format::InfoLine(entry));

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
