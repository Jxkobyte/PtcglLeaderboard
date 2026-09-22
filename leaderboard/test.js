// End-to-end tests for the leaderboard Worker, against a real SQLite via d1shim.
// Run:  node --test leaderboard/test.js     (Node 22.5+ - uses the built-in node:sqlite)
import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { openMemoryDb } from './d1shim.js';
import { handle, checkSnapshot, validateSnapshot, cleanName } from './worker.js';

const here = dirname(fileURLToPath(import.meta.url));
const SCHEMA = readFileSync(join(here, 'schema.sql'), 'utf8');

function env() { return { DB: openMemoryDb(SCHEMA) }; }

// A pinned clock: every call to now() returns clock.t, and tests advance it explicitly.
function clock(start = 1_800_000_000) { const c = { t: start, now: () => c.t }; return c; }

async function post(e, c, body) {
  const req = new Request('https://x/v1/snapshot', {
    method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify(body),
  });
  const res = await handle(req, e, c.now);
  return { status: res.status, body: await res.json() };
}

async function get(e, c, path) {
  const res = await handle(new Request('https://x' + path), e, c.now);
  return { status: res.status, body: await res.json() };
}

// master: true throughout - the board ranks Master (Arceus) players only, and the CLIENT decides
// who is one, because only it has the season config that says where Master begins.
const base = {
  playerId: 'player-aaaaaaaa', displayName: 'Jakobi', seasonId: 54,
  exp: 300, wins: 20, losses: 10, seasonMatches: 30, consecutiveWins: 2, localMatches: 29,
  elo: 1500, master: true, endDate: '2026-11-05T17:00:00Z',
};

test('a clean snapshot is accepted, ranked, and appears on the board', async () => {
  const e = env(), c = clock();
  const r = await post(e, c, base);
  assert.equal(r.status, 200);
  assert.deepEqual(r.body.flags, []);
  assert.equal(r.body.rank, 1);

  const b = await get(e, c, '/v1/leaderboard');
  assert.equal(b.body.seasonId, 54);
  assert.equal(b.body.endDate, '2026-11-05T17:00:00Z');
  assert.equal(b.body.total, 1);
  assert.equal(b.body.players[0].displayName, 'Jakobi');
  assert.equal(b.body.players[0].wins, 20);
  assert.equal(b.body.players[0].rank, 1);
});

test('the board orders by elo, then by matches played', async () => {
  const e = env(), c = clock();
  await post(e, c, { ...base, playerId: 'player-low00000', displayName: 'Low', elo: 1400 });
  c.t += 100;
  await post(e, c, { ...base, playerId: 'player-high0000', displayName: 'High', elo: 2100 });
  c.t += 100;
  await post(e, c, { ...base, playerId: 'player-mid00000', displayName: 'MidMore', elo: 1700, wins: 40, losses: 10, seasonMatches: 50, localMatches: 0 });
  c.t += 100;
  await post(e, c, { ...base, playerId: 'player-mid11111', displayName: 'MidLess', elo: 1700, wins: 5, losses: 5, seasonMatches: 10, localMatches: 0 });

  const b = await get(e, c, '/v1/leaderboard?player=player-mid11111');
  assert.deepEqual(b.body.players.map(p => p.displayName), ['High', 'MidMore', 'MidLess', 'Low']);
  assert.deepEqual(b.body.players.map(p => p.rank), [1, 2, 3, 4]);
  assert.equal(b.body.me.displayName, 'MidLess');
  assert.equal(b.body.me.rank, 3);
});

test('players below Master are recorded but never ranked', async () => {
  const e = env(), c = clock();
  await post(e, c, { ...base, playerId: 'player-master01', displayName: 'Arceus', elo: 1900 });
  c.t += 100;
  const below = await post(e, c, {
    ...base, playerId: 'player-ultra001', displayName: 'Ultra', exp: 529, elo: 1500,
    master: false, localMatches: 0,
  });
  assert.equal(below.status, 200);
  assert.equal(below.body.ranked, false);
  assert.equal(below.body.rank, 0);

  const b = await get(e, c, '/v1/leaderboard?player=player-ultra001');
  assert.deepEqual(b.body.players.map(p => p.displayName), ['Arceus']);
  assert.equal(b.body.total, 1);

  // Their record still exists, and says plainly that it is not ranked.
  assert.equal(b.body.me.displayName, 'Ultra');
  assert.equal(b.body.me.master, false);
  assert.equal(b.body.me.rank, 0);
  const p = await get(e, c, '/v1/player/player-ultra001');
  assert.equal(p.body.standing.elo, 1500);
  assert.equal(p.body.snapshots.length, 1);
});

test('submitting again too soon is refused, and the second snapshot updates the standing', async () => {
  const e = env(), c = clock();
  await post(e, c, base);
  c.t += 5;
  const tooSoon = await post(e, c, { ...base, wins: 21, seasonMatches: 31 });
  assert.equal(tooSoon.status, 429);

  c.t += 600;
  const ok = await post(e, c, { ...base, wins: 21, seasonMatches: 31, localMatches: 30 });
  assert.equal(ok.status, 200);
  assert.deepEqual(ok.body.flags, []);
  const b = await get(e, c, '/v1/leaderboard');
  assert.equal(b.body.players[0].wins, 21);
  assert.equal(b.body.players[0].snapshots, 2);
});

test('counters going backwards are flagged, kept, and the flag sticks on the standing', async () => {
  const e = env(), c = clock();
  await post(e, c, base);
  c.t += 600;
  const r = await post(e, c, { ...base, wins: 15, losses: 10, seasonMatches: 25, localMatches: 0 });
  assert.equal(r.status, 200);
  assert.ok(r.body.flags.includes('nonmonotonic'));

  // A later clean snapshot does not launder the history: the standing remembers.
  c.t += 600;
  const clean = await post(e, c, { ...base, wins: 16, losses: 10, seasonMatches: 26, localMatches: 0 });
  assert.deepEqual(clean.body.flags, []);
  const b = await get(e, c, '/v1/leaderboard');
  assert.ok(b.body.players[0].flags.includes('nonmonotonic'));
});

test('wins + losses must equal the match count', async () => {
  const e = env(), c = clock();
  const r = await post(e, c, { ...base, wins: 25, losses: 10, seasonMatches: 30 });
  assert.ok(r.body.flags.includes('inconsistent'));
});

test('more matches than the clock allows is flagged', async () => {
  const e = env(), c = clock();
  await post(e, c, base);
  c.t += 120;   // two minutes: room for at most 1 + 2 = 3 matches
  const r = await post(e, c, { ...base, wins: 30, losses: 10, seasonMatches: 40, localMatches: 0 });
  assert.ok(r.body.flags.includes('impossible-rate'));

  // The same jump over a whole day is fine.
  const e2 = env(), c2 = clock();
  await post(e2, c2, base);
  c2.t += 86400;
  const r2 = await post(e2, c2, { ...base, wins: 30, losses: 10, seasonMatches: 40, localMatches: 0 });
  assert.ok(!r2.body.flags.includes('impossible-rate'));
});

test('our own match count disagreeing with the game is flagged, small gaps are not', async () => {
  const e = env(), c = clock();
  const near = await post(e, c, { ...base, playerId: 'player-near0000', localMatches: 28 });
  assert.ok(!near.body.flags.includes('local-mismatch'));
  const far = await post(e, c, { ...base, playerId: 'player-far00000', localMatches: 5 });
  assert.ok(far.body.flags.includes('local-mismatch'));
  const none = await post(e, c, { ...base, playerId: 'player-none0000', localMatches: 0 });
  assert.ok(!none.body.flags.includes('local-mismatch'));
});

test('a brand-new player with a big record is labelled new at read time', async () => {
  const e = env(), c = clock();
  await post(e, c, { ...base, wins: 40, losses: 10, seasonMatches: 50, localMatches: 0 });
  let b = await get(e, c, '/v1/leaderboard');
  assert.ok(b.body.players[0].flags.includes('new'));

  c.t += 4 * 86400;   // four days later, the same row is no longer new
  b = await get(e, c, '/v1/leaderboard');
  assert.ok(!b.body.players[0].flags.includes('new'));
});

test('a stale season is refused once a newer one exists', async () => {
  const e = env(), c = clock();
  await post(e, c, { ...base, seasonId: 56 });
  c.t += 600;
  const prevSeason = await post(e, c, { ...base, playerId: 'player-bbbbbbbb', seasonId: 55 });
  assert.equal(prevSeason.status, 200);   // the one just closed is still allowed
  const old = await post(e, c, { ...base, playerId: 'player-cccccccc', seasonId: 54 });
  assert.equal(old.status, 400);
});

test('the board for a specific season is separate', async () => {
  const e = env(), c = clock();
  await post(e, c, { ...base, seasonId: 54 });
  c.t += 600;
  await post(e, c, { ...base, seasonId: 55, exp: 50, wins: 1, losses: 0, seasonMatches: 1, localMatches: 0 });
  const s54 = await get(e, c, '/v1/leaderboard?season=54');
  const s55 = await get(e, c, '/v1/leaderboard?season=55');
  assert.equal(s54.body.players[0].exp, 300);
  assert.equal(s55.body.players[0].exp, 50);
  const latest = await get(e, c, '/v1/season');
  assert.equal(latest.body.seasonId, 55);
});

test('player detail returns the standing and the snapshot series', async () => {
  const e = env(), c = clock();
  await post(e, c, base);
  c.t += 600;
  await post(e, c, { ...base, wins: 21, seasonMatches: 31, localMatches: 0 });
  const p = await get(e, c, '/v1/player/player-aaaaaaaa');
  assert.equal(p.body.standing.rank, 1);
  assert.equal(p.body.snapshots.length, 2);
  assert.equal(p.body.snapshots[0].wins, 21);   // newest first
});

test('malformed input is refused with a reason', async () => {
  const e = env(), c = clock();
  assert.equal((await post(e, c, { ...base, playerId: 'x' })).body.error, 'playerId');
  assert.equal((await post(e, c, { ...base, displayName: '   ' })).body.error, 'displayName');
  assert.equal((await post(e, c, { ...base, exp: -1 })).body.error, 'counters');
  assert.equal((await post(e, c, { ...base, wins: 1.5 })).body.error, 'counters');
  assert.equal((await post(e, c, { ...base, seasonId: 0 })).body.error, 'seasonId');

  const bad = await handle(new Request('https://x/v1/snapshot', { method: 'POST', body: '{not json' }), e, c.now);
  assert.equal(bad.status, 400);

  const missing = await handle(new Request('https://x/nope'), e, c.now);
  assert.equal(missing.status, 404);
});

test('the season end date is what most clients report, and one outlier cannot move it', async () => {
  const e = env(), c = clock();
  const A = '2026-11-05T17:00:00Z', B = '2026-12-01T17:00:00Z';
  await post(e, c, { ...base, playerId: 'player-vote0001', endDate: B });   // first voter, stale cache
  let s = await get(e, c, '/v1/season');
  assert.equal(s.body.endDate, B);                                          // only vote so far
  assert.deepEqual(s.body.endDateAgreement, { votes: 1, total: 1 });

  c.t += 60;
  await post(e, c, { ...base, playerId: 'player-vote0002', endDate: A });
  c.t += 60;
  await post(e, c, { ...base, playerId: 'player-vote0003', endDate: A });
  s = await get(e, c, '/v1/season');
  assert.equal(s.body.endDate, A);                                          // majority wins
  assert.deepEqual(s.body.endDateAgreement, { votes: 2, total: 3 });

  // The same player re-voting does not count twice.
  c.t += 60;
  await post(e, c, { ...base, playerId: 'player-vote0001', endDate: B, wins: 21, seasonMatches: 31, localMatches: 0 });
  s = await get(e, c, '/v1/season');
  assert.equal(s.body.endDate, A);
  assert.deepEqual(s.body.endDateAgreement, { votes: 2, total: 3 });

  // The board carries the same figures.
  const b = await get(e, c, '/v1/leaderboard');
  assert.equal(b.body.endDate, A);
  assert.equal(b.body.endDateAgreement.total, 3);
});

test('display names are cleaned, not rejected, when they can be', () => {
  assert.equal(cleanName('  Jakobi  '), 'Jakobi');
  assert.equal(cleanName('a bc'), 'abc');
  assert.equal(cleanName('x'.repeat(40)).length, 24);
  assert.equal(cleanName('two   spaces'), 'two spaces');
  assert.equal(cleanName(42), '');
});

test('checkSnapshot in isolation', () => {
  const s = { wins: 10, losses: 5, seasonMatches: 15, localMatches: 0 };
  assert.deepEqual(checkSnapshot(s, null, 0), []);
  const prev = { wins: 10, losses: 5, season_matches: 15, received_ts: 0 };
  assert.deepEqual(checkSnapshot({ ...s, wins: 11, seasonMatches: 16 }, prev, 100), []);
  assert.deepEqual(checkSnapshot({ ...s, wins: 9, seasonMatches: 14 }, prev, 100), ['nonmonotonic']);
  assert.ok(checkSnapshot({ ...s, wins: 20, seasonMatches: 25 }, prev, 100).includes('impossible-rate'));
  assert.ok(validateSnapshot(null).error);
});
