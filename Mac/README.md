# macOS

The same plugin DLL runs on a Mac. What differs is how BepInEx gets into the game.

## Why not the Windows way

On Windows, Doorstop (`winhttp.dll` beside the game) is loaded by the game and starts BepInEx.
The Mac equivalent, `DYLD_INSERT_LIBRARIES` (BepInEx's `run_bepinex.sh`), is ignored by macOS for
a notarized, hardened app, and Pokémon TCG Live is one.

## How it works instead

A Unity player reads two files from `Pokemon TCG Live.app/Contents/Resources/Data/` at start-up:

- `ScriptingAssemblies.json` lists the managed assemblies to load.
- `RuntimeInitializeOnLoads.json` lists the static methods to call before the first scene.

The installer copies `PtcglLeaderboard.MacLoader.dll` into the game's `Managed` folder and adds
one entry to each file (`scripts/unity-hook.js`). No game code is modified. Removing the two
entries gives back the original files byte for byte.

The loader (`Loader/Entrypoint.cs`) then does what BepInEx's own preloader does once it reaches
the game. It sets BepInEx's paths and calls `Chainloader.Initialize(null, false, null)` and
`Chainloader.Start()`, all by reflection. BepInEx itself lives outside the game, in
`~/Library/Application Support/PtcglLeaderboard/BepInEx`, where game updates cannot touch it.

The loader does nothing at all unless `PTCGL_LEADERBOARD_BEPINEX` is set, and only the launcher
sets it. So the game opened from the Dock runs exactly as it shipped, and the game opened with
**PTCGL Leaderboard** runs with the leaderboard.

## Apple Silicon

BepInEx 5's Harmony can only rewrite Intel machine code, so on Apple Silicon the game is started
under Rosetta (`open --arch x86_64`). The game ships as a universal app, so it has the Intel code
to run. The loader also refuses to start BepInEx in a native arm64 process, as a backstop.

## Pieces

| file | what it is |
|---|---|
| `Loader/` | `PtcglLeaderboard.MacLoader.dll`, the loader (net472, no BepInEx reference) |
| `scripts/launch.sh` | launches the game, re-hooks it after game updates, checks for updates |
| `scripts/unity-hook.js` | adds or removes the two manifest entries (osascript -l JavaScript) |
| `scripts/launcher.applescript` | compiled by the installer into `~/Applications/PTCGL Leaderboard.app` |
| `scripts/Install PTCGL Leaderboard.command` | the installer; needs no admin password |
| `scripts/Uninstall PTCGL Leaderboard.command` | the uninstaller; keeps match history |

The download zip is built by `build-mac.py` in the installer repo.

## Where things live on a Mac

| path | contents |
|---|---|
| `~/Library/Application Support/PtcglLeaderboard/` | match history, avatars, `player-id.txt` |
| `…/PtcglLeaderboard/BepInEx/` | BepInEx, the plugin, `LogOutput.log`, `PtcglLeaderboardLoader.log` |
| `…/PtcglLeaderboard/mac/` | `launch.sh` and its files, `launcher.log` |
| `~/Applications/PTCGL Leaderboard.app` | the launcher app |

## What was verified, and how

There was no Mac to test on. What could be checked on Windows was:

- **The hook.** It was checked against the real Windows game, which runs the same Unity 6 player
  code. With the manifest entries and the loader added and Doorstop taken out, Unity loaded the
  loader, called it before the first scene, and BepInEx started the plugin. It found the season,
  the match history and the player id.
  - Without the variable, the game ran untouched.
  - With the manifests listing a missing loader, the game still started normally.
- **The manifest editing.** `unity-hook.js` was tested in Node against the game's real files.
  This covered install, repeat install, uninstall back to the exact original bytes, half-installed
  states, duplicates, byte-order marks and malformed input.
- **The scripts.** They were run end to end against stand-ins for the macOS commands and a fake
  game. This covered install, launch on Intel and Apple Silicon (macOS 13 and 14+), re-hooking
  after a game update, a game already running, a game that crashes or restarts to update, a
  native-arm64 refusal, missing Rosetta, an arm64-only game, App Management denial, the update
  check, and uninstall.

Not verified: the macOS system tools themselves (`osascript` JXA file access, `osacompile`,
`open --arch` passing the environment on), Rosetta, and the game's Mac build. The first real Mac
run will say whether they behave as expected. Failures are reported in an alert that names the
log file to send.
