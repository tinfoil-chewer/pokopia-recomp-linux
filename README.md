# Pokopia Recomp for Linux and Steam Deck

Pokémon Pokopia (Switch 2) recompiled for PC, running on **Linux, the Steam Deck and AMD GPUs** through Proton or Wine.

This is a fork of [drakolordx7/pokopia-recomp](https://github.com/drakolordx7/pokopia-recomp), which runs the game natively on
Windows. The game is still the Windows program the upstream project builds; this fork adds what it takes to run it well on Linux:

- **AMD GPUs and the Steam Deck.** Upstream gives the GPU the game's memory directly (`VK_EXT_external_memory_host`). The
  Linux AMD driver refuses that for memory Wine hands out, so on AMD the picture stayed black. This fork's runtime keeps a
  copy for the GPU in step with the game instead, and fixes the depth format AMD lacks (white fog over the ground). Vertex
  shader outputs start at (0,0,0,1) as on the console: the Linux AMD driver left the game's unwritten components as NaN, which
  turned the clouds and sky black (fix from [featureJosh's fork](https://github.com/featureJosh/pokopia-recomp)).
- **Steam Deck defaults.** It is detected automatically: renders at 720p (60 fps at the title screen, 40-60 in the world) and
  shows controller button prompts.
- **Name entry with Steam's keyboard.** When the game asks for text, Steam's own on-screen keyboard comes up (Steam Deck,
  SteamOS, Big Picture) under a small text box; elsewhere an on-screen keyboard drawn in the game takes the pad, mouse or
  keyboard. No more Windows pop-up, and the whole name reaches the game.
- **Faster loading from slow storage.** The shaders the game needs at boot are packed into one compressed file, written by
  the game itself after the first start (boot shader wait on a microSD card: ~60 s down to ~10 s).
- **Fewer stalls under Wine/Proton** (the game's memory is set up so page tracking does not go through wineserver).
- **A Linux launcher AppImage** that sets up the game, adds it to Steam with Proton and runs on SteamOS.
- **The Windows runtime built on Linux** (clang-cl + the MSVC SDK through xwin), and the shader translator built from source
  with a half-float swizzle fix.

You need your own extracted copy of the game, version 2.0.1a. **This repository contains no game files, assets, code or
shaders, and never will:** everything game-specific is generated on your computer from your own dump. Not affiliated with
Nintendo, The Pokémon Company, Koei Tecmo or the upstream project.

## Use it

[linux/README.md](linux/README.md): Linux desktop, Steam with Proton, Steam Deck, and what each step does.

On Windows, use [upstream](https://github.com/drakolordx7/pokopia-recomp); this fork's changes only matter under Wine or Proton
(the runtime it builds still runs on Windows).

## Reporting problems

[Open an issue](../../issues/new/choose) with `pkp.log` from the session where it happened (it is in the game folder), your
GPU, your distribution or SteamOS version, and the Proton or Wine version. Problems that also happen on Windows belong
upstream.

## Contributing

- Pull requests against `main`. Say what you tested on which GPU (NVIDIA, AMD, Steam Deck) and how.
- `[nvn] pool_import = off` in `pkp.ini` runs the AMD code path on any GPU, `[nvn] test_no_d24 = 1` the AMD depth formats,
  and `[host] steam_deck = on` the Steam Deck defaults. All settings: [runtime/docs/CONFIG.md](runtime/docs/CONFIG.md).
- The runtime (`pkp_rt.dll`): `linux/winbuild/build.sh` (podman). The AppImage: `linux/appimage/build.sh`.
- Game-specific code goes in `recomp/games/pkp/` and `runtime/games/pkp/`; the rest is shared with other games.
- Never commit anything from a dump or made from one: exefs/romfs files, `pkp.exe`, shader caches (`.spv`, `results.tsv`,
  `*.preload.pack`), saves, logs, screenshots of copyrighted art. `.gitignore` covers the usual places.

## Credits

The upstream project by drakolordx7, built on [nxrecomp](https://github.com/drakolordx7/nxrecomp). Fixes from featureJosh's fork
(vertex output start values, the Switch 2 keyboard config and name check) keep their authors in the git history. Uses code from
[Ryujinx](https://github.com/Ryubing/Ryujinx), [Mesa](https://gitlab.freedesktop.org/mesa/mesa), smo-recomp, ScarletNX,
ssbu-recomp, SuperMarioWonderRecomp, yuzu, Eden, libnx, [astc-encoder](https://github.com/ARM-software/astc-encoder),
[bc7enc_rdo](https://github.com/richgel999/bc7enc_rdo), [libvpx](https://chromium.googlesource.com/webm/libvpx),
[zstd](https://github.com/facebook/zstd), AMD FidelityFX, NVIDIA Image Scaling, [SDL3](https://github.com/libsdl-org/SDL),
[Tauri](https://tauri.app), [Zig](https://ziglang.org), [xwin](https://github.com/Jake-Shadle/xwin) and Python, plus [Kenney input
prompts](https://kenney.nl/assets/input-prompts) and DejaVu fonts. Licences are in `runtime/THIRD_PARTY_NOTICES.md`,
`recomp/THIRD_PARTY.md` and `tools/shaders/licenses/`.

## License

GPL-3.0, like upstream. Third-party code keeps its own licence. The changes in this fork are listed in its git history.
