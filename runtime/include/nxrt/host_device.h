// SPDX-License-Identifier: GPL-3.0-or-later
// The machine the runtime runs on, where it changes a default (src/host_device.cpp).
#pragma once

namespace nxrt {

// A Steam Deck (LCD or OLED), running the game through Proton. Detected from Steam's SteamDeck=1 in the environment or the board name
// (DMI product name Jupiter or Galileo, read through Wine's Z: drive). [host] steam_deck = auto (default) | on | off overrides it.
// What it changes: [display] render_scale auto renders at the window's size instead of at least 1920x1080 (the Deck's GPU), and
// [prompts] style auto keeps controller prompts when the trackpads or the on-screen keyboard send mouse or keyboard input.
bool host_is_steam_deck();

// Steam's on-screen keyboard for text the game asks for (the software keyboard applet). Wanted on a Steam Deck, on SteamOS (SteamOS=1) and
// in Steam's gamepad UI (SteamGamepadUI=1); [applet] steam_keyboard = auto (default) | on | off overrides it.
bool host_steam_keyboard_wanted();
// Shows Steam's keyboard (steam://open/keyboard), keeping the text field at window pixels (x, y, w, h) uncovered; Steam types into the
// window like a real keyboard. Close hides it again (steam://close/keyboard). Both return at once: a worker hands the URLs to Steam in
// order (under Proton through the prefix's steam.exe, Proton's helper that forwards steam:// to the native client).
void host_steam_keyboard_open(int x, int y, int w, int h);
void host_steam_keyboard_close();

}  // namespace nxrt
