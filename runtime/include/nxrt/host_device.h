// SPDX-License-Identifier: GPL-3.0-or-later
// The machine the runtime runs on, where it changes a default (src/host_device.cpp).
#pragma once

namespace nxrt {

// A Steam Deck (LCD or OLED), running the game through Proton. Detected from Steam's SteamDeck=1 in the environment or the board name
// (DMI product name Jupiter or Galileo, read through Wine's Z: drive). [host] steam_deck = auto (default) | on | off overrides it.
// What it changes: [display] render_scale auto renders at the window's size instead of at least 1920x1080 (the Deck's GPU), and
// [prompts] style auto keeps controller prompts when the trackpads or the on-screen keyboard send mouse or keyboard input.
bool host_is_steam_deck();

}  // namespace nxrt
