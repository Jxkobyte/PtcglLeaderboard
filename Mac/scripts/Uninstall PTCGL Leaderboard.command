#!/bin/bash
# PTCGL Leaderboard & Match History - uninstaller for macOS.
#
# Takes the leaderboard out of Pokemon TCG Live, and removes the PTCGL Leaderboard app, BepInEx
# and the launcher. Your match history is kept (as on Windows): it is the one thing a reinstall
# can never bring back. Delete ~/Library/Application Support/PtcglLeaderboard to remove it too.

set -u
if [ -z "${PTCGL_TEST:-}" ]; then PATH=/usr/bin:/bin:/usr/sbin:/sbin; export PATH; fi

HERE="$(cd "$(dirname "$0")" && pwd)"
FILES="$HERE/files"
SUPPORT="$HOME/Library/Application Support/PtcglLeaderboard"
LAUNCHER_APP="$HOME/Applications/PTCGL Leaderboard.app"

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
say "PTCGL Leaderboard & Match History - uninstaller for macOS"
say "--------------------------------------------------------"
say ""

[ -f "$FILES/mac/launch.sh" ] || die "Some of the uninstaller's files are missing. Please run it from the unzipped download folder."
xattr -dr com.apple.quarantine "$HERE" 2>/dev/null || true

APP="$(/bin/bash "$FILES/mac/launch.sh" find-game 2>/dev/null)" || APP=""
if [ -n "$APP" ]; then
    if [ -n "$(/bin/bash "$FILES/mac/launch.sh" game-pid "$APP")" ]; then
        die "Pokemon TCG Live is open. Quit it (Pokemon TCG Live menu > Quit), then run this again."
    fi
    say "Taking the leaderboard out of Pokemon TCG Live..."
    if ! OUT="$(/bin/bash "$FILES/mac/launch.sh" remove-hook "$APP" 2>&1)"; then
        die "$OUT"
    fi
else
    say "Pokemon TCG Live wasn't found, so there is nothing to take out of it."
fi

say "Removing the PTCGL Leaderboard app, BepInEx and the launcher..."
if [ -d "$LAUNCHER_APP" ]; then rm -rf "$LAUNCHER_APP"; fi
if [ -d "$SUPPORT/BepInEx" ]; then rm -rf "$SUPPORT/BepInEx"; fi
if [ -d "$SUPPORT/mac" ]; then rm -rf "$SUPPORT/mac"; fi

say ""
say "Done. Pokemon TCG Live is back to how it shipped."
say ""
say "Your match history is still in ~/Library/Application Support/PtcglLeaderboard, so it"
say "comes back if you reinstall. Delete that folder if you want it gone too."
finish 0
