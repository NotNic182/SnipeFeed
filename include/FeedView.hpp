#pragma once

#include "custom-types/shared/macros.hpp"
#include "GlobalNamespace/BeatmapLevel.hpp"
#include "HMUI/ImageView.hpp"
#include "HMUI/TableCell.hpp"
#include "HMUI/TableView.hpp"
#include "TMPro/TextMeshProUGUI.hpp"
#include "UnityEngine/GameObject.hpp"
#include "UnityEngine/MonoBehaviour.hpp"
#include "UnityEngine/Transform.hpp"
#include "UnityEngine/UI/Button.hpp"
#include "bsml/shared/BSML/Components/CustomListTableData.hpp"
#include "bsml/shared/BSML/Components/ModalView.hpp"

// The feed lives as a "Snipe Feed" tab in the gameplay setup panel's Mods
// section (the left screen of song selection), next to tabs like ReeSabers
// and Qounters++. BSML calls TabActivated (registered in main.cpp) every
// time the tab is shown; it attaches this component to the tab GameObject
// on first use and forwards activations to DidActivate.
DECLARE_CLASS_CODEGEN_INTERFACES(SnipeFeed, FeedView, UnityEngine::MonoBehaviour, HMUI::TableView::IDataSource*)
{
    DECLARE_OVERRIDE_METHOD_MATCH(HMUI::TableCell*, CellForIdx, &HMUI::TableView::IDataSource::CellForIdx, HMUI::TableView* tableView, int idx);
    DECLARE_OVERRIDE_METHOD_MATCH(float, CellSize, &HMUI::TableView::IDataSource::CellSize);
    DECLARE_OVERRIDE_METHOD_MATCH(int, NumberOfCells, &HMUI::TableView::IDataSource::NumberOfCells);

    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, statusText);
    DECLARE_INSTANCE_FIELD(BSML::CustomListTableData*, listData);
    DECLARE_INSTANCE_FIELD(UnityEngine::Transform*, filterContainer);
    DECLARE_INSTANCE_FIELD(BSML::ModalView*, detailModal);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, detailText);
    DECLARE_INSTANCE_FIELD(UnityEngine::UI::Button*, playButton);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, playButtonText);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, modalCover);
    DECLARE_INSTANCE_FIELD(HMUI::ImageView*, modalAvatar);
    DECLARE_INSTANCE_FIELD(TMPro::TextMeshProUGUI*, modalPlayerText);
    DECLARE_INSTANCE_FIELD(StringW, modalPendingCover);
    DECLARE_INSTANCE_FIELD(StringW, modalPendingAvatar);

    DECLARE_INSTANCE_METHOD(void, Refresh);
    DECLARE_INSTANCE_METHOD(void, OnDestroy);

   public:
    // Registered as the gameplay setup tab callback in main.cpp.
    static void TabActivated(UnityEngine::GameObject* gameObject, bool firstActivation);

    void DidActivate(bool firstActivation);
    void RebuildFilter();
    void RebuildList();
    void OnCellClicked(int listIdx);
    void PlaySelected();
    void LaunchLevel(GlobalNamespace::BeatmapLevel* level);

   private:
    void BuildUI();

    // How many frames DidActivate has deferred waiting for the tab's
    // RectTransform to get its real height (il2cpp zero-initializes this).
    int buildAttempts;
};
