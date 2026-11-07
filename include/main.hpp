#pragma once

#include "scotland2/shared/loader.hpp"

// beatsaber-hook 9 (Beat Saber 1.45.x) flattened its header tree: the old
// utils/{logging,il2cpp-functions,hooking}.hpp moved to shared/*.hpp and the
// il2cpp_utils/il2cpp_functions namespaces became i2c / i2c::functions.
#include "beatsaber-hook/shared/api.hpp"
#include "beatsaber-hook/shared/utils.hpp"

#include "paper2_scotland2/shared/logger.hpp"

#define MOD_EXPORT extern "C" __attribute__((visibility("default")))

inline modloader::ModInfo modInfo = {MOD_ID, VERSION, 0};

// The Beat Saber build this mod is compiled against; logged at load and kept
// in step with packageVersion in mod.template.json.
constexpr auto TARGET_GAME_VERSION = "1.45.3";

constexpr auto SnipeFeedLogger = Paper::ConstLoggerContext("SnipeFeed");
