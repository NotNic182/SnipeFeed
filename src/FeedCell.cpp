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
        <text id='passLabel' font-size='1.8' align='Center' word-wrapping='false'/>
        <text id='accLabel' font-size='1.8' align='Center' word-wrapping='false'/>
        <text id='techLabel' font-size='1.8' align='Center' word-wrapping='false'/>
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

    // Per-axis tier colors — green Pass / blue Acc / red Tech, matching the
    // detail modal's Pass/Acc/Tech line. STYLE/UNAVAILABLE for the status line.
    const UnityEngine::Color GRAPH_PASS_COLOR{0.34f, 0.84f, 0.55f, 0.95f};
    const UnityEngine::Color GRAPH_ACC_COLOR{0.35f, 0.66f, 1.0f, 0.95f};
    const UnityEngine::Color GRAPH_TECH_COLOR{1.0f, 0.42f, 0.42f, 0.95f};
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
        // The three tier labels are plain text flowing in a vertical layout —
        // no sprites, no manual geometry. Colors/values are set per bind in
        // UpdateSkillDisplay; keep them out of the raycast so taps hit the row.
        for (auto text : {cell->passLabel, cell->accLabel, cell->techLabel}) {
            text->set_raycastTarget(false);
        }
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
    songText->set_text(Format::TitleLine(entry));
    playerText->set_text("<b>" + Format::Escape(entry.playerName) + "</b>");
    statsText->set_text(Format::StatsLine(entry));
    timeText->set_text(Format::TimeAgo(entry.timepost));
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

    // No official ratings: hide the three rating lines; the style/status text
    // above already carries the honest "Unranked" or unavailable fallback.
    if (!entry.hasRatings) {
        for (auto text : {passLabel, accLabel, techLabel})
            if (text) text->set_enabled(false);
        return;
    }

    // Three plain colored lines: "<axis> <rating>" (green Pass / blue Acc /
    // red Tech), same values and colors as the detail modal. No graphic.
    struct Tier {
        TMPro::TextMeshProUGUI* label;
        char const* name;
        float rating;
        UnityEngine::Color color;
    };
    Tier tiers[3] = {
        {passLabel, "Pass", entry.passRating, GRAPH_PASS_COLOR},
        {accLabel,  "Acc",  entry.accRating,  GRAPH_ACC_COLOR},
        {techLabel, "Tech", entry.techRating, GRAPH_TECH_COLOR},
    };
    for (auto const& t : tiers) {
        if (!t.label) continue;
        t.label->set_enabled(true);
        t.label->set_color(t.color);
        t.label->set_text(std::format("{} {:.2f}", t.name, t.rating));
    }
}

void FeedCell::ResetSkillDisplay() {
    for (auto text : {passLabel, accLabel, techLabel, styleText}) {
        if (text) text->set_text("");
    }
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
