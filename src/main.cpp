#include "main.hpp"
#include "FeedView.hpp"
#include "ModConfig.hpp"

#include "custom-types/shared/register.hpp"

#include "bsml/shared/BSML.hpp"
#include "libcurl/shared/curl.h"

// Called at the early stages of game loading
MOD_EXPORT void setup(CModInfo* info) noexcept {
    *info = modInfo.to_c();

    getModConfig().Init(modInfo);

    Paper::Logger::RegisterFileContextId(SnipeFeedLogger.tag);

    SnipeFeedLogger.info("Completed setup!");
}

// Called later on in the game loading - a good time to install function hooks
MOD_EXPORT void late_load() {
    // libcurl's lazy global init is not thread-safe, and this mod runs up
    // to 4 feed workers plus image threads concurrently — init exactly once
    // before any of them can race it.
    curl_global_init(CURL_GLOBAL_ALL);

    il2cpp_functions::Init();
    custom_types::Register::AutoRegister();

    BSML::Init();
    // Lives in the gameplay setup panel's Mods section (left screen of the
    // Solo song selection), alongside tabs like ReeSabers and Qounters++.
    // All = Solo/Party + Online (multiplayer) + Campaign + Custom
    // (mod-provided flows like Multiplayer+).
    BSML::Register::RegisterGameplaySetupTab("Snipe Feed", SnipeFeed::FeedView::TabActivated, BSML::MenuType::All);

    SnipeFeedLogger.info("SnipeFeed {} loaded! Game version: {}", VERSION, "1.40.8");
}
