#include "main.hpp"
#include "FeedView.hpp"
#include "ModConfig.hpp"

#include "custom-types/shared/register.hpp"

#include "bsml/shared/BSML.hpp"

// Called at the early stages of game loading
MOD_EXPORT void setup(CModInfo* info) noexcept {
    *info = modInfo.to_c();

    getModConfig().Init(modInfo);

    Paper::Logger::RegisterFileContextId(SnipeFeedLogger.tag);

    SnipeFeedLogger.info("Completed setup!");
}

// Called later on in the game loading - a good time to install function hooks
MOD_EXPORT void late_load() {
    il2cpp_functions::Init();
    custom_types::Register::AutoRegister();

    BSML::Init();
    // Lives in the gameplay setup panel's Mods section (left screen of the
    // Solo song selection), alongside tabs like ReeSabers and Qounters++.
    // MenuType::Solo also covers Party (both use the single player flow).
    BSML::Register::RegisterGameplaySetupTab("Snipe Feed", SnipeFeed::FeedView::TabActivated, BSML::MenuType::Solo);

    SnipeFeedLogger.info("SnipeFeed {} loaded! Game version: {}", VERSION, "1.40.8");
}
