# Community leaderboard (Cloudflare Workers + D1)

Mirrors each player's **official season record** - `SeasonRank.exp / wins / losses /
seasonMatches`, which the client fetches from TPCi - and ranks by exp, the game's own rank.
Nothing is computed from our match log; it only rides along as a cross-check.

Trust is **visible, not enforced**: every snapshot is checked against the player's previous one
(counters never decrease, `wins + losses == seasonMatches`, no more matches than the wall clock
allows, our local count agrees with the game's) and failures become flags the board shows.
Fame is the only prize, so nobody is blocked - they are labelled.

The season end date is a **vote**: every client reads it from the game's own config document
(`season_00NN_0.2`), one vote per player, majority wins, and the board reports the agreement.

## Files

| file | what |
|---|---|
| `schema.sql` | D1 tables: `player`, `snapshot`, `standing`, `season`, `season_vote` |
| `worker.js` | the API - `POST /v1/snapshot`, `GET /v1/leaderboard`, `GET /v1/player/:id`, `GET /v1/season` |
| `wrangler.toml` | deployment config; paste the D1 id in after creating the database |
| `d1shim.js` | D1 look-alike over Node's built-in `node:sqlite`, for tests |
| `test.js` | end-to-end tests: `node --test leaderboard/test.js` (needs nothing installed) |

## Deploy (once)

```bash
cd leaderboard
npm install -D wrangler
npx wrangler login
npx wrangler d1 create prizetracker-leaderboard        # paste the id into wrangler.toml
npx wrangler d1 execute prizetracker-leaderboard --remote --file=schema.sql
npx wrangler deploy                                     # prints the workers.dev URL
```

Then set that URL as `Server` under `[Leaderboard]` in
`BepInEx/config/ptcgl.prizetracker.cfg`. Players opt in from the in-game Settings card; the
board itself is viewable without opting in.

## Free-tier budget

Workers Free: 100,000 requests/day, **10 ms CPU per request**. D1 Free: 5M rows read/day,
100k rows written/day, 5 GB. A snapshot is ~6 indexed statements; the board is one indexed
query over `standing`. Nothing aggregates on read.
