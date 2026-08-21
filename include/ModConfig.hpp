#pragma once

#include "config-utils/shared/config-utils.hpp"

DECLARE_CONFIG(ModConfig) {
    CONFIG_VALUE(PlayerId, std::string, "BeatLeader Player ID", "", "The number from your beatleader.com profile URL");
    CONFIG_VALUE(ScoresPerPlayer, int, "Scores per player", 3);
    CONFIG_VALUE(MaxPlayers, int, "Max followed players", 20);
};
