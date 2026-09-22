# PTCGL Prize Tracker

A BepInEx overlay for Pokémon TCG Live: prize tracking, deck tracking, opponent tracking, and a
frame-rate limiter.

**Standalone.** This project has nothing to do with the PokeAI simulator/AI work. It contains no
card-data export, no bulk card fetching and no engine tooling — it only reads the live match state
and draws an overlay. Keep it that way.

## How it reads the game

The tracker reads the client's **real match state** — `MatchManager.currentMatch.GetBoardState()`,
which returns the `MatchLogic.MatchBoard` the rules engine itself runs on. That gives every zone for
**both** seats in one read, with card conservation guaranteed by the engine.

It does **not** scrape Unity `GameObject` paths or `Card3D` components. That approach broke on any
UI change, only ever saw the local player, and could not see a zone that was not currently rendered.

Cards the server hides from you arrive as redacted entities with `cardSourceID == ""` (confirmed in
`MatchBoard.PrivatizeEntity` and the `CardEntity(entityID, …)` constructor). That empty id is
exactly what separates "known" from "hidden", and the prize maths is built on it.

## Prize tracking

Your 60-card list comes from the client's own inventory (`PlayerInventoryManager.TryGetActiveDeck`),
so there is nothing to paste or click before a match.

Everything you cannot see sits in a set of interchangeable hidden slots — face-down deck plus
face-down prizes. Which slot a copy occupies is uniformly random, so "is it prized?" is plain
hypergeometry over that pool:

- **odds from turn one**, sharpening every time a card is revealed
  (a fresh 4-of is 35.1% to have at least one copy prized, a 1-of is 10.0%)
- **provably prized** — when there is not enough room left in the deck to hold every unaccounted
  copy, the overflow *must* be in the prizes, and it is shown as certain rather than a percentage
- a **mismatch guard**: if the loaded list does not exactly fill the hidden slots (wrong deck, an
  unresolvable name) it says so instead of showing confident wrong numbers

## Opponent tracking

Everything they have revealed, accumulated over the match and keyed by **entityID**, so a card that
moves zones (hand → discard → shuffled back) is counted exactly once and a genuine second copy is
counted separately. Plus live prize / hand / deck / discard / lost-zone counts.

## Frame-rate limiter

PTCGL renders an essentially static board as fast as the GPU allows. Three separate caps: in-match,
in-menus, and while alt-tabbed. vsync is switched off so the cap actually applies, and background
behaviour is only taken over when there is a real background cap to enforce.

## Hotkeys

| key | action |
|-----|--------|
| F1 | show/hide overlay |
| F3 | reload the active decklist |
| F4 | import a decklist from the clipboard (fallback) |

Settings persist to `BepInEx/config/ptcgl.prizetracker.cfg` (window position/size, frame caps) and
the caps can also be changed in the overlay's **SET** tab.

## Build

Requires the .NET SDK and a local PTCGL install. No Visual Studio needed — the net472 reference
assemblies come from NuGet.

```
dotnet build PrizeTracker.csproj
```

The plugin is copied into `BepInEx\plugins` automatically. **Close PTCGL first** — a running client
holds the old DLL locked, and the build will say so rather than silently leaving you on a stale
build. BepInEx only loads plugins at startup, so restart the game after a rebuild.

Game paths are overridable:

```
dotnet build PrizeTracker.csproj /p:GameRoot="D:\path\to\Pokemon Trading Card Game Live"
```

## Previewing the UI without launching the game

PTCGL takes about a minute to start, which makes iterating on the overlay inside the game painful.

```
dotnet run --project Preview/Preview.csproj
```

writes two pages:

- **`board.html`** — a 1920x1080 recreation of the PTCGL play field with the overlay drawn on top at
  its real pixel size. The board state is a **real** mid-game position lifted from a PokeAI
  self-play replay, with the real 60-card decklist and card ids resolved from the client's card
  database (`Preview/fixture.json`). The board and the overlay are driven by the *same* snapshot
  through the *same* Tracker, so what the panel claims can be checked against the board it is
  describing. This is what answers the questions you otherwise need a full game launch for: is the
  panel readable at 1080p, is it the right size, what does it cover.
- **`preview.html`** — the panel alone across three scenarios (turn 1, mid game, late game).

Open either in a browser, or run a local server:

```
dotnet run --project Preview/Preview.csproj -- --serve
```

then visit **http://localhost:8080** (add a port number to use a different one, e.g. `--serve 8081`).
Every request regenerates the pages, so editing `Preview/fixture.json` and hitting refresh is enough
to see the change; only a C# edit needs a rebuild. It drives the **real** `Tracker` with hand-built match
states (turn 1, mid game, late game) and renders the exact rows it produces, with real card art.

What it does and does not prove:

- the **data** is real — same Tracker, same accounting, ordering and probabilities as in game
- the **pixels** are a mock — the overlay draws with Unity IMGUI, the preview with HTML/CSS

So it is a design and content tool, not a pixel-accurate simulation. The preview's styling is kept
to things IMGUI can also do (solid fills, rectangles, plain text) so a design that looks right there
ports to the overlay. Card art in the preview comes from TCGdex URLs, because Unity asset bundles
cannot be read outside the game.

## Card art

The overlay shows a grid of card art rather than a list of names — recognising a card by its picture
is much faster mid-turn than reading text.

Art is loaded from the client's **own asset bundles**: the game stores one bundle per card where the
bundle name, bundle key and asset name are all the cardSourceID, so
`AssetBundleManager.LoadAssetBundleAndAsset<Texture>(id, id, id, …)` fetches it. That means art is
local, offline and always the correct printing.

Loads are throttled, cached, and every failure is remembered so a missing bundle is attempted once
rather than every frame. **If art cannot be loaded the tile falls back to the card's name**, so the
overlay stays fully usable either way.

## Tests

The accounting and prize maths are pure logic and are covered offline. The test project *links* the
real `Core` sources rather than re-implementing them, and drives them with hand-built board
snapshots.

```
dotnet build Tests/TrackerTests.csproj
Tests\bin\Debug\net472\TrackerTests.exe
```

Covers the hypergeometric core against known reference values, provably-prized deduction, revealed
prizes, the decklist mismatch guard, and opponent reveal counting across zone moves.

## Note on the old plugin

An earlier combined plugin (`GameStateReader.dll`, plugin GUID `PrizeChecker`) carried both an early
version of this tracker and the PokeAI data-export tooling. It must not run alongside this one, or
you get two overlays. It is disabled by renaming it to `GameStateReader.dll.disabled` in
`BepInEx\plugins` — BepInEx only scans `*.dll`, including subdirectories, so renaming the extension
is the way to disable it (moving it to a subfolder does not work).

Rebuilding the GameStateReader project will copy it back into `plugins` and re-enable it.
