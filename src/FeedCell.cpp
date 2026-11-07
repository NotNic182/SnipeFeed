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
    // then a large song title line over (avatar + name + stats + time),
    // chevron last. ids bind to the DECLARE_INSTANCE_FIELDs.
    constexpr auto CELL_BSML = R"(
<horizontal id='bgContainer' bg='round-rect-panel' bg-color='#00000073' pad='1' spacing='2' horizontal-fit='Unconstrained' child-expand-width='false' child-control-width='true' child-align='MiddleLeft' xmlns:xsi='http://www.w3.org/2001/XMLSchema-instance' xsi:noNamespaceSchemaLocation='https://raw.githubusercontent.com/RedBrumbler/Quest-BSML-Docs/gh-pages/schema.xsd'>
    <text id='rankText' font-size='3' align='Center' word-wrapping='false' pref-width='3.5'/>
    <image id='coverImage' pref-width='8.5' pref-height='8.5' preserve-aspect='true'/>
    <vertical spacing='0' pref-width='60' flexible-width='1000' child-expand-height='false' child-control-height='true'>
        <text id='songText' font-size='3.4' align='MidlineLeft' overflow-mode='Ellipsis' word-wrapping='false'/>
        <horizontal spacing='1.5' pref-height='4.2' child-expand-width='false' child-control-width='true' child-align='MiddleLeft'>
            <image id='avatarImage' pref-width='3.5' pref-height='3.5' preserve-aspect='true'/>
            <text id='playerText' font-size='2.8' align='MidlineLeft' word-wrapping='false'/>
            <text id='statsText' font-size='2.6' align='MidlineLeft' word-wrapping='false'/>
            <text id='timeText' font-size='2.4' color='#8899AA' align='MidlineLeft' word-wrapping='false' flexible-width='1000'/>
        </horizontal>
    </vertical>
    <vertical spacing='0.15' pref-width='15' child-expand-width='false' child-control-width='true' child-expand-height='false' child-control-height='true' child-align='MiddleCenter'>
        <image id='triangleImage' pref-width='6.2' pref-height='6.2' preserve-aspect='true'/>
        <text id='styleText' font-size='1.55' color='#D7E5F3' align='Center' overflow-mode='Ellipsis' word-wrapping='false' pref-height='2'/>
    </vertical>
    <text id='chevronText' font-size='4' color='#5A6B7A' align='Center' word-wrapping='false' pref-width='3'/>
</horizontal>)";

    // While an image is loading (or failed) the ImageView shows as a dim
    // tile instead of a stark white square. Tints live on FeedCell so the
    // detail modal uses the exact same values.
    const UnityEngine::Color PLACEHOLDER_TINT = SnipeFeed::FeedCell::PlaceholderTint();
    const UnityEngine::Color LOADED_TINT = SnipeFeed::FeedCell::LoadedTint();

    constexpr float BG_ALPHA_IDLE = 0.55f;
    constexpr float BG_ALPHA_ACTIVE = 0.8f;

    // Row triangle is ~6 units tall; the texture has no mipmaps, so draw it
    // near display size rather than shrinking a big one.
    constexpr int TRIANGLE_PIXELS = 64;
    // Style/status line colors.
    const UnityEngine::Color STYLE_COLOR{0.84f, 0.91f, 0.97f, 0.95f};
    const UnityEngine::Color UNAVAILABLE_COLOR{0.48f, 0.56f, 0.63f, 0.9f};

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
        cell->styleText->set_raycastTarget(false);
        cell->triangleImage->set_raycastTarget(false);
        go->AddComponent<HMUI::Touchable*>();
        return cell;
    }
    return tableCell.cast<FeedCell>();
}

void FeedCell::SetData(FeedEntry const& entry, int rank, std::string const& sortValue) {
    if (rank <= 3)
        rankText->set_text(std::format("<b><color={}>{}</color></b>", RankColor(rank), rank));
    else
        rankText->set_text(std::format("<color={}>{}</color>", RankColor(rank), rank));
    songText->set_text(Format::TitleLine(entry));
    playerText->set_text("<b>" + Format::Escape(entry.playerName) + "</b>");
    statsText->set_text(Format::StatsLine(entry));
    std::string timeAgo = Format::TimeAgo(entry.timepost);
    timeText->set_text(sortValue.empty() ? timeAgo : sortValue + (timeAgo.empty() ? "" : "  " + timeAgo));
    UpdateSkillDisplay(entry);

    ResetImages();

    pendingCoverUrl = entry.coverUrl;
    pendingAvatarUrl = entry.avatarUrl;

    SafePtrUnity<FeedCell> self(this);
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

void FeedCell::UpdateSkillDisplay(FeedEntry const& entry) {
    // Style/status label under the bars (salvaged from the retired triangle):
    // prefer a server-authored style tag; else the real DifficultyStatus for a
    // known map; else an honest muted fallback. Never fabricated.
    std::string style = Format::MapStyleLabel(entry);
    if (!style.empty()) {
        styleText->set_text(style);
        styleText->set_color(STYLE_COLOR);
    } else if (entry.mapStatus >= 0) {
        styleText->set_text(Format::MapStatusLabel(entry.mapStatus));
        styleText->set_color(entry.hasRatings ? STYLE_COLOR : UNAVAILABLE_COLOR);
    } else {
        styleText->set_text(entry.hasRatings ? "" : "ratings unavailable");
        styleText->set_color(UNAVAILABLE_COLOR);
    }

    // No official ratings: no triangle; the style/status text above already
    // says "Unranked" or that ratings are unavailable.
    UnityEngine::Sprite* triangle = entry.hasRatings
        ? SpriteCache::GetTriangleSprite(entry.passRating, entry.accRating, entry.techRating, TRIANGLE_PIXELS)
        : nullptr;
    triangleImage->set_sprite(triangle);
    triangleImage->set_enabled(triangle != nullptr);
}

void FeedCell::ResetSkillDisplay() {
    styleText->set_text("");
    triangleImage->set_sprite(nullptr);
    triangleImage->set_enabled(false);
}

void FeedCell::RefreshBackground() {
    if (!bgContainer) return;
    bool active = get_selected() || get_highlighted();
    bgContainer->set_color({0.0f, 0.0f, 0.0f, active ? BG_ALPHA_ACTIVE : BG_ALPHA_IDLE});
}

void FeedCell::SelectionDidChange(HMUI::SelectableCell::TransitionType) { RefreshBackground(); }
void FeedCell::HighlightDidChange(HMUI::SelectableCell::TransitionType) { RefreshBackground(); }
void FeedCell::WasPreparedForReuse() {
    ResetImages();
    ResetSkillDisplay();
}
