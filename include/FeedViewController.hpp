#pragma once

#include "custom-types/shared/macros.hpp"
#include "GlobalNamespace/BeatmapLevel.hpp"
#include "HMUI/ViewController.hpp"
#include "TMPro/TextMeshProUGUI.hpp"
#include "UnityEngine/Transform.hpp"
#include "UnityEngine/UI/Button.hpp"
#include "bsml/shared/BSML/Components/CustomListTableData.hpp"
#include "bsml/shared/BSML/Components/ModalView.hpp"

DECLARE_CLASS_CODEGEN(SnipeFeed, FeedViewController, HMUI::ViewController) {
    DECLARE_OVERRIDE_METHOD_MATCH(void, DidActivate, &HMUI::ViewController::DidActivate, bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling);

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
