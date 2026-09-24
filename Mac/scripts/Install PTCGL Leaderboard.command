#!/bin/bash
# PTCGL Leaderboard & Match History - installer for macOS.
#
# Double-click to run; Terminal opens and shows what it does. Safe to run again: it updates and
# repairs. Installs to ~/Library/Application Support/PtcglLeaderboard and creates the
# "PTCGL Leaderboard" app in ~/Applications, which is how the game is started with the
# leaderboard. See launch.sh for how the game is hooked. Needs no administrator password.

set -u
if [ -z "${PTCGL_TEST:-}" ]; then PATH=/usr/bin:/bin:/usr/sbin:/sbin; export PATH; fi

HERE="$(cd "$(dirname "$0")" && pwd)"
FILES="$HERE/files"
SUPPORT="$HOME/Library/Application Support/PtcglLeaderboard"
APPS="$HOME/Applications"
LAUNCHER_APP="$APPS/PTCGL Leaderboard.app"
PLISTBUDDY="${PTCGL_PLISTBUDDY:-/usr/libexec/PlistBuddy}"
VERSION="$(cat "$FILES/mac/version.txt" 2>/dev/null)"

say() { printf '%s\n' "$*"; }

finish() {
    printf '\n'
    read -r -p "Press Return to close this window. " _ || true
    exit "$1"
}

die() {
    printf '\n%s\n' "$*" >&2
    finish 1
}

say ""
say "PTCGL Leaderboard & Match History ${VERSION} - installer for macOS"
say "--------------------------------------------------------------"
say ""

if [ ! -f "$FILES/mac/launch.sh" ] || [ ! -f "$FILES/BepInEx/plugins/PtcglLeaderboard.dll" ] || [ ! -f "$FILES/BepInEx/core/BepInEx.dll" ]; then
    die "Some of the installer's files are missing. Please download it again, unzip it, and run it from that folder."
fi

# Everything unzipped from a download carries macOS's "downloaded from the internet" flag, and
# cp keeps it on the copies. Clear it here so nothing installed from this folder has it.
xattr -dr com.apple.quarantine "$HERE" 2>/dev/null || true

# ---------------------------------------------------------------------------------------------
say "Looking for Pokemon TCG Live..."
APP="$(/bin/bash "$FILES/mac/launch.sh" find-game)" || die "Pokemon TCG Live wasn't found in your Applications folder.

Install it from https://pokemon.com/tcgl, open it once, then run this installer again."
say "  found: $APP"

if [ -n "$(/bin/bash "$FILES/mac/launch.sh" game-pid "$APP")" ]; then
    die "Pokemon TCG Live is open. Quit it (Pokemon TCG Live menu > Quit), then run this installer again."
fi

# ---------------------------------------------------------------------------------------------
# Apple Silicon: the game has to run under Rosetta for BepInEx (see launch.sh).
if [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ]; then
    say "  Apple Silicon Mac: the game will run with the leaderboard under Rosetta."
    if ! arch -x86_64 /usr/bin/true >/dev/null 2>&1; then
        say ""
        say "Rosetta isn't installed yet. It's Apple's free translator for Intel apps, and the"
        say "leaderboard needs it. Installing it now: Apple shows its licence first - type A and"
        say "press Return if you agree."
        say ""
        softwareupdate --install-rosetta || true
        if ! arch -x86_64 /usr/bin/true >/dev/null 2>&1; then
            die "Rosetta couldn't be installed. Open Terminal, run this command (it asks for your Mac's
password), then run the installer again:

    sudo softwareupdate --install-rosetta"
        fi
        say "  Rosetta installed."
    fi
    EXE="$(/bin/bash "$FILES/mac/launch.sh" game-exe "$APP")"
    if ! file -b "$APP/Contents/MacOS/$EXE" 2>/dev/null | grep -q 'x86_64'; then
        die "This version of Pokemon TCG Live only contains Apple Silicon code, which the leaderboard can't load into. Please tell jakobi_ on Discord."
    fi
fi

# ---------------------------------------------------------------------------------------------
say ""
say "Installing to ~/Library/Application Support/PtcglLeaderboard ..."
mkdir -p "$SUPPORT/BepInEx/core" "$SUPPORT/BepInEx/plugins" "$SUPPORT/BepInEx/config" "$SUPPORT/mac" \
    || die "Couldn't create $SUPPORT"
cp -f "$FILES/BepInEx/core/"*.dll "$SUPPORT/BepInEx/core/" \
    && cp -f "$FILES/BepInEx/plugins/PtcglLeaderboard.dll" "$SUPPORT/BepInEx/plugins/" \
    && cp -f "$FILES/BepInEx/config/BepInEx.cfg" "$SUPPORT/BepInEx/config/" \
    && cp -f "$FILES/mac/"* "$SUPPORT/mac/" \
    || die "Couldn't copy the leaderboard's files into $SUPPORT"
chmod 755 "$SUPPORT/mac/launch.sh"
printf '%s\n' "$APP" > "$SUPPORT/mac/game-path.txt"
xattr -dr com.apple.quarantine "$SUPPORT" 2>/dev/null || true

say "Adding the leaderboard to Pokemon TCG Live..."
if ! OUT="$(/bin/bash "$SUPPORT/mac/launch.sh" install-hook "$APP" 2>&1)"; then
    die "$OUT"
fi

# ---------------------------------------------------------------------------------------------
say "Creating the PTCGL Leaderboard app in your Applications folder..."
mkdir -p "$APPS" || die "Couldn't create $APPS"
if [ -d "$LAUNCHER_APP" ]; then rm -rf "$LAUNCHER_APP"; fi
osacompile -o "$LAUNCHER_APP" "$SUPPORT/mac/launcher.applescript" >/dev/null \
    || die "Couldn't create the PTCGL Leaderboard app."
# The trophy icon. The applet's own icon also sits in an asset catalog, which macOS prefers, so
# that goes; then the app is re-signed (ad hoc, as osacompile signed it) to match its new files.
if [ -f "$SUPPORT/mac/PTCGL Leaderboard.icns" ]; then
    cp -f "$SUPPORT/mac/PTCGL Leaderboard.icns" "$LAUNCHER_APP/Contents/Resources/applet.icns" 2>/dev/null
    rm -f "$LAUNCHER_APP/Contents/Resources/Assets.car" 2>/dev/null
    "$PLISTBUDDY" -c 'Delete :CFBundleIconName' "$LAUNCHER_APP/Contents/Info.plist" >/dev/null 2>&1 || true
    codesign --force --sign - "$LAUNCHER_APP" >/dev/null 2>&1 || true
    touch "$LAUNCHER_APP"
fi

# ---------------------------------------------------------------------------------------------
say ""
say "Done."
say ""
say "Start the game with PTCGL Leaderboard, in your Applications folder (it's open in Finder"
say "now). Drag it into your Dock to keep it handy. Opening Pokemon TCG Live any other way"
say "starts the game without the leaderboard."
say ""
say "The first time, macOS may say PTCGL Leaderboard was prevented from modifying apps. If it"
say "does: System Settings > Privacy & Security > App Management, turn on PTCGL Leaderboard, and"
say "open it again. It needs that to put the leaderboard back after game updates."
open -R "$LAUNCHER_APP" >/dev/null 2>&1 || true
finish 0
