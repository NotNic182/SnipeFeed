#pragma once

#include "custom-types/shared/macros.hpp"
#include "GlobalNamespace/BeatmapLevel.hpp"
#include "HMUI/TableCell.hpp"
#include "HMUI/TableView.hpp"
#include "HMUI/ViewController.hpp"
#include "TMPro/TextMeshProUGUI.hpp"
#include "UnityEngine/Transform.hpp"
#include "UnityEngine/UI/Button.hpp"
#include "bsml/shared/BSML/Components/CustomListTableData.hpp"
#include "bsml/shared/BSML/Components/ModalView.hpp"

DECLARE_CLASS_CODEGEN_INTERFACES(SnipeFeed, FeedViewController, HMUI::ViewController, HMUI::TableView::IDataSource*)
{
    DECLARE_OVERRIDE_METHOD_MATCH(void, DidActivate, &HMUI::ViewController::DidActivate, bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling);

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

    DECLARE_INSTANCE_METHOD(void, Refresh);

   public:
    void RebuildFilter();
    void RebuildList();
    void OnCellClicked(int listIdx);
    void PlaySelected();
    void LaunchLevel(GlobalNamespace::BeatmapLevel* level);
};
