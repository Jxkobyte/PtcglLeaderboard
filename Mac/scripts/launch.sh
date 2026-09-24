#!/bin/bash
# PTCGL Leaderboard & Match History - the macOS launcher and game hook.
#
#   launch.sh launch               start the game with the leaderboard (what the app runs)
#   launch.sh check-update         print the newer version, if there is one (checks once a day)
#   launch.sh find-game            print where Pokemon TCG Live is
#   launch.sh game-exe [app]       print the name of the game's executable
#   launch.sh game-pid [app]       print the running game's process id, if it is running
#   launch.sh install-hook [app]   list the loader in the game's Unity manifests
#   launch.sh remove-hook [app]    take it out again
#   launch.sh version
#
# Windows loads BepInEx through Doorstop (winhttp.dll). A Mac cannot: DYLD_INSERT_LIBRARIES is
# ignored for a notarized app. So the game is asked to load it itself - the loader assembly is
# listed in the game's ScriptingAssemblies.json and RuntimeInitializeOnLoads.json (unity-hook.js),
# and it starts BepInEx only when PTCGL_LEADERBOARD_BEPINEX is set, which only this script does.
# The game opened from the Dock runs exactly as it shipped; opened through PTCGL Leaderboard, it
# runs with the leaderboard.
#
# A game update replaces those manifests, so every launch checks the hook and puts it back. On
# Apple Silicon the game is started under Rosetta, because the Harmony that BepInEx 5 ships can
# only rewrite Intel code.
#
# Written for the bash 3.2 every Mac has.

set -u
if [ -z "${PTCGL_TEST:-}" ]; then PATH=/usr/bin:/bin:/usr/sbin:/sbin; export PATH; fi

SUPPORT="$HOME/Library/Application Support/PtcglLeaderboard"
BEPINEX="$SUPPORT/BepInEx"
MAC="$SUPPORT/mac"
HERE="$(cd "$(dirname "$0")" && pwd)"
LOADER="PtcglLeaderboard.MacLoader.dll"
LOADER_LOG="$BEPINEX/PtcglLeaderboardLoader.log"
PLISTBUDDY="${PTCGL_PLISTBUDDY:-/usr/libexec/PlistBuddy}"
LATEST_API="https://api.github.com/repos/Jxkobyte/ptcgl-leaderboard/releases/latest"
GAME="Pokemon TCG Live"
VERSION="$(cat "$HERE/version.txt" 2>/dev/null)"
[ -n "$VERSION" ] || VERSION="0.0.0"

# ---------------------------------------------------------------------------------------------
# messages

note() {
    mkdir -p "$MAC" 2>/dev/null
    printf '%s  %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" >> "$MAC/launcher.log" 2>/dev/null
    return 0
}

# The message goes to stderr, which the app shows in an alert.
fail() {
    printf '%s\n' "$*" >&2
    note "stopped: $*"
    exit 1
}

blocked() {
    fail "macOS didn't let the leaderboard change Pokemon TCG Live.

To allow it, open System Settings > Privacy & Security > App Management and turn on PTCGL Leaderboard (and Terminal, if you are running the installer). Then try again.

If Pokemon TCG Live was installed from a different user account on this Mac, reinstall it from yours."
}

trim_log() {
    local f="$MAC/launcher.log" n
    [ -f "$f" ] || return 0
    n="$(wc -l < "$f" | tr -d ' ')"
    case "$n" in ''|*[!0-9]*) return 0 ;; esac
    if [ "$n" -gt 500 ]; then tail -n 300 "$f" > "$f.tmp" 2>/dev/null && mv -f "$f.tmp" "$f"; fi
    return 0
}

# ---------------------------------------------------------------------------------------------
# the game

is_game() {
    [ -n "$1" ] && [ -d "$1/Contents/MacOS" ] && [ -d "$1/Contents/Resources/Data/Managed" ]
}

find_game() {
    local c
    if [ -f "$MAC/game-path.txt" ]; then
        c="$(cat "$MAC/game-path.txt" 2>/dev/null)"
        if is_game "$c"; then printf '%s\n' "$c"; return 0; fi
    fi
    for c in "/Applications/$GAME.app" "$HOME/Applications/$GAME.app"; do
        if is_game "$c"; then printf '%s\n' "$c"; return 0; fi
    done
    # Anywhere else Spotlight knows about.
    c="$(mdfind "kMDItemFSName == '$GAME.app'" 2>/dev/null | head -n 1)"
    if is_game "$c"; then printf '%s\n' "$c"; return 0; fi
    return 1
}

# The game's executable name, from its Info.plist.
exe_name() {
    local n="" f
    if [ -x "$PLISTBUDDY" ]; then
        n="$("$PLISTBUDDY" -c 'Print :CFBundleExecutable' "$1/Contents/Info.plist" 2>/dev/null)"
    fi
    if [ -n "$n" ] && [ -f "$1/Contents/MacOS/$n" ]; then printf '%s\n' "$n"; return 0; fi
    if [ -f "$1/Contents/MacOS/$GAME" ]; then printf '%s\n' "$GAME"; return 0; fi
    for f in "$1/Contents/MacOS/"*; do
        if [ -f "$f" ]; then printf '%s\n' "${f##*/}"; return 0; fi
    done
    printf '%s\n' "$GAME"
}

# The running game's process id, or nothing. "comm" is the executable's path without its
# arguments (argv[0]), so it must END in ".../Contents/MacOS/<exe>" exactly - a "<exe> Helper"
# process, or anything merely mentioning the game on its command line, does not count.
game_pid() {
    ps -axo pid=,comm= 2>/dev/null | PTCGL_EXE="$1" awk '
        {
            pid = $1; line = $0
            sub(/^[ ]*[0-9]+[ ]+/, "", line)
            sub(/[ ]+$/, "", line)
            exe = ENVIRON["PTCGL_EXE"]
            m = "/Contents/MacOS/" exe
            if (line == exe || (length(line) > length(m) && substr(line, length(line) - length(m) + 1) == m)) {
                print pid
                exit
            }
        }'
}

is_apple_silicon() { [ "$(sysctl -n hw.optional.arm64 2>/dev/null)" = "1" ]; }
has_rosetta() { arch -x86_64 /usr/bin/true >/dev/null 2>&1; }
has_intel_code() { file -b "$1" 2>/dev/null | grep -q 'x86_64'; }

macos_major() {
    local v
    v="$(sw_vers -productVersion 2>/dev/null)"
    v="${v%%.*}"
    case "$v" in ''|*[!0-9]*) v=0 ;; esac
    printf '%s\n' "$v"
}

# The loader wrote "pid=<n>" and then "BepInEx started" for this process.
started_with_leaderboard() {
    [ -f "$LOADER_LOG" ] && grep -Eq "pid=$1[[:space:]]*\$" "$LOADER_LOG" && grep -q "BepInEx started" "$LOADER_LOG"
}

# ---------------------------------------------------------------------------------------------
# the hook

install_hook() {
    local app="$1" data managed out
    data="$app/Contents/Resources/Data"
    managed="$data/Managed"
    if [ ! -f "$HERE/$LOADER" ] || [ ! -f "$HERE/unity-hook.js" ]; then
        fail "Some of the leaderboard's files are missing. Please run the installer again."
    fi
    if [ ! -f "$data/ScriptingAssemblies.json" ] || [ ! -f "$data/RuntimeInitializeOnLoads.json" ]; then
        fail "This version of Pokemon TCG Live is laid out differently from the one the leaderboard was made for, so it can't be added. Please tell jakobi_ on Discord."
    fi
    if ! cmp -s "$HERE/$LOADER" "$managed/$LOADER"; then
        cp -f "$HERE/$LOADER" "$managed/$LOADER" 2>/dev/null || blocked
        note "copied the loader into the game"
    fi
    if ! out="$(osascript -l JavaScript "$HERE/unity-hook.js" install "$data" 2>&1)"; then
        case "$out" in *"not permitted"*|*"could not write"*) blocked ;; esac
        fail "Couldn't add the leaderboard to Pokemon TCG Live: $out"
    fi
    if [ "$out" = "changed" ]; then
        note "listed the loader in the game's Unity manifests"
        # A changed app can be re-checked by Gatekeeper if it still carries the download flag.
        xattr -dr com.apple.quarantine "$app" 2>/dev/null || true
    fi
    return 0
}

remove_hook() {
    local app="$1" data managed out
    data="$app/Contents/Resources/Data"
    managed="$data/Managed"
    if [ -f "$data/ScriptingAssemblies.json" ] && [ -f "$data/RuntimeInitializeOnLoads.json" ]; then
        if ! out="$(osascript -l JavaScript "$HERE/unity-hook.js" uninstall "$data" 2>&1)"; then
            case "$out" in *"not permitted"*|*"could not write"*) blocked ;; esac
            fail "Couldn't remove the leaderboard from Pokemon TCG Live: $out"
        fi
    fi
    # After the manifests, so the game is never told to load a file that is not there.
    if [ -e "$managed/$LOADER" ]; then rm -f "$managed/$LOADER" 2>/dev/null || blocked; fi
    return 0
}

# ---------------------------------------------------------------------------------------------
# launch

launch() {
    local app exe pid major
    trim_log
    app="$(find_game)" || fail "Pokemon TCG Live wasn't found. Install it from pokemon.com/tcgl into your Applications folder, then open PTCGL Leaderboard again."
    exe="$(exe_name "$app")"
    note "launch $VERSION: $app ($exe)"
    if [ ! -f "$BEPINEX/core/BepInEx.dll" ] || [ ! -f "$BEPINEX/plugins/PtcglLeaderboard.dll" ]; then
        fail "The leaderboard isn't fully installed. Please run the installer again."
    fi

    pid="$(game_pid "$exe")"
    if [ -n "$pid" ]; then
        if started_with_leaderboard "$pid"; then
            note "already running with the leaderboard (pid $pid)"
            open -a "$app" >/dev/null 2>&1 || true
            return 0
        fi
        fail "Pokemon TCG Live is already open, without the leaderboard. Quit it (Pokemon TCG Live menu > Quit), then open PTCGL Leaderboard again."
    fi

    if is_apple_silicon; then
        has_rosetta || fail "This Mac needs Apple's Rosetta to run Pokemon TCG Live with the leaderboard. Run the installer again to set it up."
        has_intel_code "$app/Contents/MacOS/$exe" || fail "This version of Pokemon TCG Live only contains Apple Silicon code, which the leaderboard can't load into. Please tell jakobi_ on Discord."
    fi

    install_hook "$app"
    rm -f "$LOADER_LOG" 2>/dev/null

    PTCGL_LEADERBOARD_BEPINEX="$BEPINEX"
    export PTCGL_LEADERBOARD_BEPINEX
    major="$(macos_major)"
    if is_apple_silicon; then note "starting on macOS $major, Apple Silicon (Rosetta)"; else note "starting on macOS $major, Intel"; fi

    # macOS 14 and later: through Launch Services, which passes this environment on to the app.
    # Earlier: the executable directly. The same split the long-running macOS PTCGL mods use.
    if [ "$major" -ge 14 ]; then
        if is_apple_silicon; then
            open --arch x86_64 -a "$app" || fail "macOS couldn't start Pokemon TCG Live."
        else
            open -a "$app" || fail "macOS couldn't start Pokemon TCG Live."
        fi
    else
        if is_apple_silicon; then
            nohup arch -x86_64 "$app/Contents/MacOS/$exe" >/dev/null 2>&1 &
        else
            nohup "$app/Contents/MacOS/$exe" >/dev/null 2>&1 &
        fi
    fi
    confirm_started "$exe"
}

# Wait for the game, then for the loader's verdict, so a problem is reported rather than just
# being a leaderboard that never appears.
confirm_started() {
    local exe="$1" pid="" i=0 reason
    while [ "$i" -lt 45 ]; do
        pid="$(game_pid "$exe")"
        [ -n "$pid" ] && break
        sleep 1
        i=$((i + 1))
    done
    [ -n "$pid" ] || fail "Pokemon TCG Live didn't start. Please open PTCGL Leaderboard again."
    note "game running, pid $pid"

    i=0
    while [ "$i" -lt 150 ]; do
        if [ -f "$LOADER_LOG" ] && grep -Eq "pid=$pid[[:space:]]*\$" "$LOADER_LOG"; then
            if grep -q "BepInEx started" "$LOADER_LOG"; then
                note "leaderboard started"
                return 0
            fi
            reason="$(grep -E 'NOT started|as it shipped|already loaded|nothing to start' "$LOADER_LOG" | head -n 1 | cut -c 22-)"
            if [ -n "$reason" ]; then
                fail "Pokemon TCG Live is running, but the leaderboard couldn't load:

$reason

Please send this file to jakobi_ on Discord:
$LOADER_LOG"
            fi
        fi
        if [ -z "$(game_pid "$exe")" ]; then
            sleep 5
            if [ -n "$(game_pid "$exe")" ]; then
                fail "Pokemon TCG Live restarted itself, probably to finish an update, so it is running without the leaderboard. Quit it, then open PTCGL Leaderboard again."
            fi
            fail "Pokemon TCG Live closed while it was starting. If that keeps happening, run \"Uninstall PTCGL Leaderboard\" and let jakobi_ know on Discord."
        fi
        sleep 1
        i=$((i + 1))
    done
    fail "Pokemon TCG Live is running, but the leaderboard hasn't reported in. If it's missing from the game, please send this file to jakobi_ on Discord:
$LOADER_LOG"
}

# ---------------------------------------------------------------------------------------------
# updates: at most once a day, a few seconds at most. A failed check is not counted, so the next
# launch tries again. Prints the newer version, or nothing.

newer() {
    local a b i x y
    IFS=. read -r -a a <<< "$1"
    IFS=. read -r -a b <<< "$2"
    for i in 0 1 2 3; do
        x="${a[$i]:-0}"
        y="${b[$i]:-0}"
        case "$x" in ''|*[!0-9]*) x=0 ;; esac
        case "$y" in ''|*[!0-9]*) y=0 ;; esac
        if [ "$((10#$x))" -gt "$((10#$y))" ]; then return 0; fi
        if [ "$((10#$x))" -lt "$((10#$y))" ]; then return 1; fi
    done
    return 1
}

check_update() {
    local stamp="$MAC/update-check.txt" now last body latest
    now="$(date +%s)"
    if [ -f "$stamp" ]; then
        last="$(cat "$stamp" 2>/dev/null)"
        case "$last" in ''|*[!0-9]*) last=0 ;; esac
        if [ $((now - last)) -lt 72000 ]; then return 0; fi
    fi
    body="$(curl -fsSL --max-time 8 -H 'Accept: application/vnd.github+json' -A "PtcglLeaderboard-mac/$VERSION" "$LATEST_API" 2>/dev/null)" || return 0
    mkdir -p "$MAC" 2>/dev/null
    printf '%s\n' "$now" > "$stamp" 2>/dev/null
    latest="$(printf '%s\n' "$body" | sed -n 's/.*"tag_name"[[:space:]]*:[[:space:]]*"v\{0,1\}\([0-9][0-9.]*\)".*/\1/p' | head -n 1)"
    [ -n "$latest" ] || return 0
    if newer "$latest" "$VERSION"; then
        note "update available: $latest (have $VERSION)"
        printf '%s\n' "$latest"
    fi
    return 0
}

# ---------------------------------------------------------------------------------------------

case "${1:-}" in
    launch)
        launch
        ;;
    check-update)
        check_update
        ;;
    find-game)
        find_game || exit 1
        ;;
    game-exe)
        app="${2:-}"
        if [ -z "$app" ]; then app="$(find_game)" || exit 1; fi
        exe_name "$app"
        ;;
    game-pid)
        app="${2:-}"
        if [ -z "$app" ]; then app="$(find_game)" || exit 0; fi
        game_pid "$(exe_name "$app")"
        ;;
    install-hook)
        app="${2:-}"
        if [ -z "$app" ]; then app="$(find_game)" || fail "Pokemon TCG Live wasn't found."; fi
        install_hook "$app"
        ;;
    remove-hook)
        app="${2:-}"
        if [ -z "$app" ]; then app="$(find_game)" || exit 0; fi
        remove_hook "$app"
        ;;
    version)
        printf '%s\n' "$VERSION"
        ;;
    *)
        printf 'usage: launch.sh launch|check-update|find-game|game-exe [app]|game-pid [app]|install-hook [app]|remove-hook [app]|version\n' >&2
        exit 2
        ;;
esac
