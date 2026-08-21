#include "main.hpp"
#include "FeedViewController.hpp"
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
    BSML::Register::RegisterMainMenu<SnipeFeed::FeedViewController*>("Snipe Feed", "Snipe Feed", "Your BeatLeader following feed: friends' latest scores to snipe");

    SnipeFeedLogger.info("SnipeFeed {} loaded! Game version: {}", VERSION, "1.40.8");
}
