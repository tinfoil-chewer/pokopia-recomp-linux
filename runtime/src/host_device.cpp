// SPDX-License-Identifier: GPL-3.0-or-later
// The machine the runtime runs on (include/nxrt/host_device.h).
#include "nxrt/host_device.h"

#include "nxrt/config.h"
#include "nxrt/log.h"

#include <windows.h>

#include <algorithm>
#include <cctype>
#include <fstream>
#include <string>

namespace nxrt {

namespace {

std::string lower_trimmed(std::string s) {
    while (!s.empty() && std::isspace(static_cast<unsigned char>(s.back()))) s.pop_back();
    size_t i = 0;
    while (i < s.size() && std::isspace(static_cast<unsigned char>(s[i]))) ++i;
    s.erase(0, i);
    std::transform(s.begin(), s.end(), s.begin(), [](unsigned char c) { return static_cast<char>(std::tolower(c)); });
    return s;
}

bool detect_steam_deck(std::string* why) {
    const std::string forced = lower_trimmed(config_get_str("host", "steam_deck", "auto"));
    if (forced == "1" || forced == "on" || forced == "true" || forced == "yes") {
        *why = "[host] steam_deck = on";
        return true;
    }
    if (forced == "0" || forced == "off" || forced == "false" || forced == "no") {
        *why = "[host] steam_deck = off";
        return false;
    }
    char env[16] = {};
    const DWORD n = GetEnvironmentVariableA("SteamDeck", env, sizeof env);
    if (n > 0 && n < sizeof env && lower_trimmed(std::string(env, n)) == "1") {
        *why = "Steam's SteamDeck=1";
        return true;
    }
    // Under Wine the Linux root is drive Z:; on Windows the path does not exist.
    std::ifstream f("Z:\\sys\\class\\dmi\\id\\product_name");
    std::string board;
    if (f && std::getline(f, board)) {
        board = lower_trimmed(board);
        if (board == "jupiter" || board == "galileo") {
            *why = "board " + board;
            return true;
        }
    }
    *why = "no SteamDeck=1, not a Deck board";
    return false;
}

}  // namespace

bool host_is_steam_deck() {
    static const bool deck = [] {
        std::string why;
        const bool d = detect_steam_deck(&why);
        NXLOG_I("host", "Steam Deck: %s (%s)", d ? "yes" : "no", why.c_str());
        return d;
    }();
    return deck;
}

}  // namespace nxrt
