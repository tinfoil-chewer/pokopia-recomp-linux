# Pokopia Recomp for Linux and Steam Deck

[![Latest release](https://img.shields.io/github/v/release/tinfoil-chewer/pokopia-recomp-linux?label=download&color=2ea44f)](../../releases/latest)
[![Platforms](https://img.shields.io/badge/runs%20on-Linux%20%7C%20Steam%20Deck%20%7C%20SteamOS-1a9fff)](#what-you-need)
[![License](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

Play **Pokémon Pokopia** on a Linux PC or a Steam Deck, with your own copy of the game.

This is the Linux edition of [Pokopia Recomp](https://github.com/drakolordx7/pokopia-recomp), a project that turns the Switch 2
game into a program that runs on a PC. This edition makes it run well through Steam's Proton: on NVIDIA and AMD graphics cards,
on SteamOS and on the Steam Deck.

**[Download the latest release](../../releases/latest)**

## What you need

- **A Linux PC with Steam**, or a **Steam Deck** (do the setup on a PC, see [Steam Deck](#steam-deck)). Tested on an RTX 4070
  desktop, a Steam Deck LCD and an AMD Steam Machine.
- **Your own copy of the game's files**, dumped from a cartridge or console you own: the base game and its **2.0.1a** update.
  Nothing from the game comes with this project.
- **About 40 GB of free space.**
- **An hour or two for the one-time setup.** It can be paused and continued.

## Getting started

1. Download `PokopiaLauncher-…-x86_64.AppImage` from the [latest release](../../releases/latest). Make it executable
   (right-click, Properties, "Executable", or `chmod +x`) and open it.
2. Press **Install**. The launcher downloads the parts of the original project it needs and checks them.
3. Choose the folder with your game files and press **Set up**. It builds the game (about 10 minutes), then prepares the game's
   graphics (1-2 hours on a desktop).
4. Press **Add to Steam**, then start Steam again so the game shows up in your library.
5. Press **Play**, or start the game from your Steam library.

The game goes to `~/Games/PokopiaRecomp` unless you pick another folder; your saves are in its `save` folder.

## Steam Deck

- Set up on a PC, then copy the whole game folder to the Deck, preferably to the internal SSD (much faster than a microSD
  card). The [detailed guide](linux/README.md#steam-deck) shows how.
- In Desktop Mode, add `pkp.exe` to Steam as a non-Steam game with Proton Experimental, then play from Game Mode.
- The Deck is recognised automatically: it renders at 720p, shows controller button prompts, and brings up Steam's on-screen
  keyboard when the game asks for your name.
- Expect 60 fps at the title screen and 40-60 fps while playing.

## Good to know

- **The first start takes longer** while your graphics driver prepares the game's shaders. Later starts are quick.
- **Rarely, the very first start stops on "Preparing shaders".** Close the game and start it again.
- **Sprinting can look a little jittery.** That comes from the original project's unlocked frame rate.
- **On Windows?** Use the [original project](https://github.com/drakolordx7/pokopia-recomp).
- **Something not working?** [Open an issue](../../issues/new/choose) and attach `pkp.log` from the game folder, with your
  graphics card and your Linux or SteamOS and Proton versions.

This project contains **no game files, assets, code or shaders, and never will**: everything game-specific is generated on your
computer from your own copy. Not affiliated with Nintendo, The Pokémon Company, Koei Tecmo or the original project.

---

## For the technically curious

### How it works

The game stays the Windows program the upstream project builds: `pkp.exe`, recompiled on your computer from the game's code in
your dump, and `pkp_rt.dll`, the runtime that stands in for the Switch 2 (its OS services, and NVN graphics translated to
Vulkan). It runs under Proton or Wine, and Wine hands Vulkan straight to the native driver. The game's shaders are translated to
SPIR-V once, during setup, and work on any GPU.

### What this fork changes

- **AMD GPUs and the Steam Deck.** Upstream gives the GPU the game's memory directly (`VK_EXT_external_memory_host`). The Linux
  AMD driver refuses that for memory Wine hands out, so on AMD the picture stayed black. This fork's runtime keeps a copy for the
  GPU in step with the game instead, and fixes the depth format AMD lacks (white fog over the ground). Vertex shader outputs
  start at (0,0,0,1) as on the console: the Linux AMD driver left the game's unwritten components as NaN, which turned the clouds
  and sky black (fix from [featureJosh's fork](https://github.com/featureJosh/pokopia-recomp)).
- **Steam Deck defaults.** Detected from `SteamDeck=1` or the board name: the back end's own scale renders at the window's size,
  and controller prompts stay on when the trackpads or the on-screen keyboard send mouse or keyboard input.
- **Name entry.** The Switch 2 keyboard config is read at its packed offsets (UTF-8 answers, so the whole name arrives) and the
  game's name check (`ngc:u`) gets an answer (both from featureJosh's fork). The Win32 prompt is replaced by an on-screen
  keyboard drawn by the overlay; on a Deck, SteamOS or Big Picture it also asks Steam for its keyboard through
  `steam://open/keyboard`, which Proton's `steam.exe` helper forwards to the Steam client.
- **Faster loading from slow storage.** The shaders the game needs at boot are packed into one zstd file, written by the game
  itself after the first start (boot shader wait on a microSD card: ~60 s down to ~10 s).
- **Fewer stalls under Wine/Proton.** The game's memory is a `SEC_COMMIT` section, so page tracking does not go through
  wineserver.
- **A Linux launcher AppImage** that installs, sets up, adds the game to Steam with Proton and starts it, with its own Python,
  Zig and shader translator.
- **Built on Linux.** The Windows runtime with clang-cl and the MSVC SDK through xwin; the shader translator from source with a
  half-float swizzle fix (sun and cloud shadows).
- **Debugging without a debugger at hand** (a Steam Deck): `[nvn] skip_fs_cycle`, `[nvn] shader_override`, a format support
  report and `[applet] swkbd_demo`. All settings: [runtime/docs/CONFIG.md](runtime/docs/CONFIG.md).

### Detailed guide and manual setup

[linux/README.md](linux/README.md): every step the launcher takes, setting up by hand with Wine, the settings worth knowing,
building, and what you are trusting.

### Building

- **The runtime** (`pkp_rt.dll`): `linux/winbuild/build.sh` (podman, clang-cl and lld-link). `linux/winbuild/build.sh sdk`
  downloads the MSVC C runtime and the Windows SDK once, with [xwin](https://github.com/Jake-Shadle/xwin); running it means you
  accept Microsoft's licence terms for them.
- **The AppImage**: `linux/appimage/build.sh` (podman and the .NET 10 SDK; build the runtime first). Every download it makes is
  pinned and checked.

### Contributing

- Pull requests against `main`. Say what you tested on which GPU (NVIDIA, AMD, Steam Deck) and how.
- `[nvn] pool_import = off` in `pkp.ini` runs the AMD code path on any GPU, `[nvn] test_no_d24 = 1` the AMD depth formats, and
  `[host] steam_deck = on` the Steam Deck defaults.
- Game-specific code goes in `recomp/games/pkp/` and `runtime/games/pkp/`; the rest is shared with other games.
- Never commit anything from a dump or made from one: exefs/romfs files, `pkp.exe`, shader caches (`.spv`, `results.tsv`,
  `*.preload.pack`), saves, logs, screenshots of copyrighted art. `.gitignore` covers the usual places.
- Problems that also happen on Windows belong [upstream](https://github.com/drakolordx7/pokopia-recomp).

### Credits

The upstream project by drakolordx7, built on [nxrecomp](https://github.com/drakolordx7/nxrecomp). Fixes from featureJosh's fork
(vertex output start values, the on-screen keyboard, the Switch 2 keyboard config and name check) keep their authors in the git
history. Uses code from [Ryujinx](https://github.com/Ryubing/Ryujinx), [Mesa](https://gitlab.freedesktop.org/mesa/mesa),
smo-recomp, ScarletNX, ssbu-recomp, SuperMarioWonderRecomp, yuzu, Eden, libnx,
[astc-encoder](https://github.com/ARM-software/astc-encoder), [bc7enc_rdo](https://github.com/richgel999/bc7enc_rdo),
[libvpx](https://chromium.googlesource.com/webm/libvpx), [zstd](https://github.com/facebook/zstd), AMD FidelityFX, NVIDIA Image
Scaling, [SDL3](https://github.com/libsdl-org/SDL), [Tauri](https://tauri.app), [Zig](https://ziglang.org),
[xwin](https://github.com/Jake-Shadle/xwin) and Python, plus [Kenney input prompts](https://kenney.nl/assets/input-prompts) and
DejaVu fonts. Licences are in `runtime/THIRD_PARTY_NOTICES.md`, `recomp/THIRD_PARTY.md` and `tools/shaders/licenses/`.

### License

GPL-3.0, like upstream. Third-party code keeps its own licence. The changes in this fork are listed in its git history.
