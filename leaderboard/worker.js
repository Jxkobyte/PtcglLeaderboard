// PrizeTracker community leaderboard - Cloudflare Worker over D1.
//
// Contract (all JSON):
//   POST /v1/snapshot       the mod's season record after a match   -> { ok, flags, rank }
//   GET  /v1/leaderboard    ?season=&limit=&player=                  -> { seasonId, endDate, total, players, me }
//   GET  /v1/player/:id     ?season=                                 -> { standing, snapshots }
//   GET  /v1/season         ?season=                                 -> { seasonId, endDate }
//   GET  /health
//
// Trust model: nothing is rejected for being suspicious. Every snapshot is checked against the
// player's previous one and anything that fails becomes a FLAG that the board displays. The only
// hard rejections are malformed input, a stale season, and submitting faster than a match could
// possibly be played. Fame is the only prize here, so the defences are proportionate: visible,
// cheap, and impossible to argue with.
//
// Budget: Workers Free is 10 ms CPU per request. Every route is a handful of indexed statements;
// nothing aggregates across the table on read. Standings are maintained at write time.

const MAX_BODY = 64 * 1024;
const MIN_SECONDS_BETWEEN_SNAPSHOTS = 15;
const MIN_SECONDS_PER_MATCH = 60;        // a conceded game can be short, but not shorter
const NEW_PLAYER_DAYS = 3;
const NEW_PLAYER_MATCHES = 30;

const ID_RE = /^[A-Za-z0-9_-]{8,64}$/;

export default {
  async fetch(request, env) {
    try {
      return await handle(request, env, () => Math.floor(Date.now() / 1000));
    } catch (err) {
      return json({ error: 'internal', detail: String(err && err.message || err) }, 500);
    }
  },
};

/** Exported so tests can pin the clock. */
export async function handle(request, env, now) {
  const url = new URL(request.url);
  const path = url.pathname.replace(/\/+$/, '') || '/';

  if (request.method === 'GET' && path === '/health') return json({ ok: true });

  if (request.method === 'POST' && path === '/v1/snapshot') return postSnapshot(request, env, now);
  if (request.method === 'GET' && path === '/v1/leaderboard') return getLeaderboard(url, env, now);
  if (request.method === 'GET' && path === '/v1/season') return getSeason(url, env);

  const m = path.match(/^\/v1\/player\/([^/]+)$/);
  if (request.method === 'GET' && m) return getPlayer(decodeURIComponent(m[1]), url, env, now);

  return json({ error: 'not found' }, 404);
}

// ---------------------------------------------------------------------------------------------
// POST /v1/snapshot

async function postSnapshot(request, env, now) {
  const body = await readJson(request);
  if (body.error) return json({ error: body.error }, 400);
  const v = validateSnapshot(body.value);
  if (v.error) return json({ error: v.error }, 400);
  const s = v.value;
  const t = now();

  // A season the service has never seen is registered; a season older than the previous one is
  // refused - it can only be a client with a stale config, and it would land on a board that
  // has already closed.
  const latest = await env.DB.prepare('SELECT MAX(season_id) AS id FROM season').first();
  const latestId = latest && latest.id != null ? Number(latest.id) : null;
  if (latestId != null && s.seasonId < latestId - 1) return json({ error: 'stale season' }, 400);

  const prev = await env.DB
    .prepare('SELECT * FROM snapshot WHERE player_id = ? AND season_id = ? ORDER BY received_ts DESC, id DESC LIMIT 1')
    .bind(s.playerId, s.seasonId).first();

  if (prev && t - Number(prev.received_ts) < MIN_SECONDS_BETWEEN_SNAPSHOTS) {
    return json({ error: 'too soon' }, 429);
  }

  const flags = checkSnapshot(s, prev, t);

  const player = await env.DB.prepare('SELECT * FROM player WHERE player_id = ?').bind(s.playerId).first();
  const firstSeen = player ? Number(player.first_seen) : t;

  const standing = await env.DB
    .prepare('SELECT flags, snapshots FROM standing WHERE season_id = ? AND player_id = ?')
    .bind(s.seasonId, s.playerId).first();
  const unionFlags = union(standing ? parseFlags(standing.flags) : [], flags);
  const snapshots = standing ? Number(standing.snapshots) + 1 : 1;

  const writes = [
    env.DB.prepare(
      'INSERT INTO season (season_id, end_date, first_seen) VALUES (?, ?, ?) ' +
      'ON CONFLICT(season_id) DO UPDATE SET end_date = COALESCE(season.end_date, excluded.end_date)')
      .bind(s.seasonId, s.endDate, t),
    env.DB.prepare(
      'INSERT INTO player (player_id, display_name, first_seen, last_seen) VALUES (?, ?, ?, ?) ' +
      'ON CONFLICT(player_id) DO UPDATE SET display_name = excluded.display_name, last_seen = excluded.last_seen')
      .bind(s.playerId, s.displayName, firstSeen, t),
    env.DB.prepare(
      'INSERT INTO snapshot (player_id, season_id, exp, wins, losses, season_matches, consecutive_wins, ' +
      'elo, local_matches, client_ts, received_ts, flags) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)')
      .bind(s.playerId, s.seasonId, s.exp, s.wins, s.losses, s.seasonMatches, s.consecutiveWins,
            s.elo, s.localMatches, s.clientTs, t, JSON.stringify(flags)),
    env.DB.prepare(
      'INSERT INTO standing (season_id, player_id, display_name, exp, wins, losses, season_matches, ' +
      'consecutive_wins, elo, snapshots, first_seen, last_seen, flags) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?) ' +
      'ON CONFLICT(season_id, player_id) DO UPDATE SET display_name = excluded.display_name, ' +
      'exp = excluded.exp, wins = excluded.wins, losses = excluded.losses, ' +
      'season_matches = excluded.season_matches, consecutive_wins = excluded.consecutive_wins, ' +
      'elo = excluded.elo, snapshots = excluded.snapshots, last_seen = excluded.last_seen, ' +
      'flags = excluded.flags')
      .bind(s.seasonId, s.playerId, s.displayName, s.exp, s.wins, s.losses, s.seasonMatches,
            s.consecutiveWins, s.elo, snapshots, firstSeen, t, JSON.stringify(unionFlags)),
  ];
  if (s.endDate) {
    writes.push(env.DB.prepare(
      'INSERT INTO season_vote (season_id, player_id, end_date) VALUES (?, ?, ?) ' +
      'ON CONFLICT(season_id, player_id) DO UPDATE SET end_date = excluded.end_date')
      .bind(s.seasonId, s.playerId, s.endDate));
  }
  await env.DB.batch(writes);

  if (s.endDate) await adoptConsensusEndDate(env, s.seasonId);

  const rank = await rankOf(env, s.seasonId, s.exp, s.seasonMatches);
  return json({ ok: true, flags, rank });
}

/**
 * The season's end date is whatever MOST players' clients say it is. Every client reads it from
 * the same game config document, so disagreement means a stale cache on one machine, or someone
 * editing theirs - and one vote cannot move the majority. Ties break on the earliest date, which
 * is the conservative reading for a countdown.
 */
async function adoptConsensusEndDate(env, seasonId) {
  const top = await env.DB
    .prepare('SELECT end_date, COUNT(*) AS n FROM season_vote WHERE season_id = ? ' +
             'GROUP BY end_date ORDER BY n DESC, end_date ASC LIMIT 1')
    .bind(seasonId).first();
  if (!top) return;
  await env.DB.prepare('UPDATE season SET end_date = ? WHERE season_id = ?')
    .bind(top.end_date, seasonId).run();
}

/** How many votes the adopted end date has, out of how many cast. */
async function endDateAgreement(env, seasonId, endDate) {
  const total = await env.DB.prepare('SELECT COUNT(*) AS n FROM season_vote WHERE season_id = ?')
    .bind(seasonId).first();
  const votes = endDate == null ? null : await env.DB
    .prepare('SELECT COUNT(*) AS n FROM season_vote WHERE season_id = ? AND end_date = ?')
    .bind(seasonId, endDate).first();
  return { votes: votes ? Number(votes.n) : 0, total: total ? Number(total.n) : 0 };
}

/**
 * The checks. Each one names a way a record can be wrong without needing an opponent to say so.
 */
export function checkSnapshot(s, prev, t) {
  const flags = [];

  // The game's own identity. Forging one of these numbers breaks it.
  if (s.wins + s.losses !== s.seasonMatches) flags.push('inconsistent');

  if (prev) {
    // Counters only ever go up within a season. (exp is deliberately not checked: the game
    // takes exp away for a loss in the upper leagues, so a drop there is legitimate.)
    if (s.wins < Number(prev.wins) || s.losses < Number(prev.losses) ||
        s.seasonMatches < Number(prev.season_matches)) {
      flags.push('nonmonotonic');
    }

    // More matches than the wall clock allows since the last snapshot.
    const elapsed = Math.max(0, t - Number(prev.received_ts));
    const played = s.seasonMatches - Number(prev.season_matches);
    if (played > 1 + Math.floor(elapsed / MIN_SECONDS_PER_MATCH)) flags.push('impossible-rate');
  }

  // Our own tracker's count of matches this season against the game's. They should track each
  // other; a large gap means one of them is not what it claims to be.
  if (s.localMatches > 0) {
    const gap = Math.abs(s.localMatches - s.seasonMatches);
    if (gap > Math.max(3, Math.floor(s.seasonMatches * 0.2))) flags.push('local-mismatch');
  }

  return flags;
}

export function validateSnapshot(b) {
  if (!b || typeof b !== 'object') return { error: 'body must be an object' };

  const playerId = typeof b.playerId === 'string' ? b.playerId : '';
  if (!ID_RE.test(playerId)) return { error: 'playerId' };

  const displayName = cleanName(b.displayName);
  if (!displayName) return { error: 'displayName' };

  const seasonId = int(b.seasonId, 1, 100000);
  if (seasonId == null) return { error: 'seasonId' };

  const exp = int(b.exp, 0, 1000000);
  const wins = int(b.wins, 0, 100000);
  const losses = int(b.losses, 0, 100000);
  const seasonMatches = int(b.seasonMatches, 0, 200000);
  if (exp == null || wins == null || losses == null || seasonMatches == null) return { error: 'counters' };

  const consecutiveWins = int(b.consecutiveWins, 0, 100000) ?? 0;
  const elo = int(b.elo, 0, 100000) ?? 0;
  const localMatches = int(b.localMatches, 0, 200000) ?? 0;
  const clientTs = int(b.clientTs, 0, 4102444800) ?? null;

  let endDate = null;
  if (typeof b.endDate === 'string' && b.endDate.length <= 40 && !Number.isNaN(Date.parse(b.endDate))) {
    endDate = b.endDate;
  }

  return { value: { playerId, displayName, seasonId, exp, wins, losses, seasonMatches,
                    consecutiveWins, elo, localMatches, clientTs, endDate } };
}

// ---------------------------------------------------------------------------------------------
// GET /v1/leaderboard

async function getLeaderboard(url, env, now) {
  const seasonId = await resolveSeason(url, env);
  if (seasonId == null) return json({ seasonId: null, endDate: null, total: 0, players: [], me: null });

  const limit = int(url.searchParams.get('limit'), 1, 100) ?? 50;
  const me = url.searchParams.get('player');
  const t = now();

  const season = await env.DB.prepare('SELECT end_date FROM season WHERE season_id = ?').bind(seasonId).first();
  const total = await env.DB.prepare('SELECT COUNT(*) AS n FROM standing WHERE season_id = ?').bind(seasonId).first();

  const rows = await env.DB
    // exp first, then elo: inside Master exp barely moves (one rank spans 550-15000) so elo is
    // what actually separates the top of the board.
    .prepare('SELECT * FROM standing WHERE season_id = ? ORDER BY exp DESC, elo DESC, season_matches DESC, last_seen ASC LIMIT ?')
    .bind(seasonId, limit).all();

  const players = (rows.results || []).map((r, i) => publicRow(r, i + 1, t));

  let mine = null;
  if (me && ID_RE.test(me)) {
    const r = await env.DB.prepare('SELECT * FROM standing WHERE season_id = ? AND player_id = ?')
      .bind(seasonId, me).first();
    if (r) mine = publicRow(r, await rankOf(env, seasonId, Number(r.exp), Number(r.season_matches)), t);
  }

  const endDate = season ? season.end_date : null;
  return json({
    seasonId,
    endDate,
    endDateAgreement: await endDateAgreement(env, seasonId, endDate),
    total: total ? Number(total.n) : 0,
    updatedTs: t,
    players,
    me: mine,
  });
}

async function getPlayer(id, url, env, now) {
  if (!ID_RE.test(id)) return json({ error: 'playerId' }, 400);
  const seasonId = await resolveSeason(url, env);
  if (seasonId == null) return json({ standing: null, snapshots: [] });

  const r = await env.DB.prepare('SELECT * FROM standing WHERE season_id = ? AND player_id = ?')
    .bind(seasonId, id).first();
  if (!r) return json({ standing: null, snapshots: [] });

  const t = now();
  const standing = publicRow(r, await rankOf(env, seasonId, Number(r.exp), Number(r.season_matches)), t);
  const snaps = await env.DB
    .prepare('SELECT exp, wins, losses, season_matches, elo, received_ts, flags FROM snapshot ' +
             'WHERE player_id = ? AND season_id = ? ORDER BY received_ts DESC, id DESC LIMIT 50')
    .bind(id, seasonId).all();

  return json({
    standing,
    snapshots: (snaps.results || []).map(s => ({
      exp: Number(s.exp), wins: Number(s.wins), losses: Number(s.losses),
      seasonMatches: Number(s.season_matches), elo: Number(s.elo || 0),
      receivedTs: Number(s.received_ts),
      flags: parseFlags(s.flags),
    })),
  });
}

async function getSeason(url, env) {
  const seasonId = await resolveSeason(url, env);
  if (seasonId == null) return json({ seasonId: null, endDate: null });
  const s = await env.DB.prepare('SELECT end_date FROM season WHERE season_id = ?').bind(seasonId).first();
  const endDate = s ? s.end_date : null;
  return json({ seasonId, endDate, endDateAgreement: await endDateAgreement(env, seasonId, endDate) });
}

// ---------------------------------------------------------------------------------------------
// helpers

async function resolveSeason(url, env) {
  const asked = int(url.searchParams.get('season'), 1, 100000);
  if (asked != null) return asked;
  const latest = await env.DB.prepare('SELECT MAX(season_id) AS id FROM season').first();
  return latest && latest.id != null ? Number(latest.id) : null;
}

/** 1-based rank: how many standings sort strictly ahead, plus one. Same order as the board. */
async function rankOf(env, seasonId, exp, matches) {
  const r = await env.DB
    .prepare('SELECT COUNT(*) AS n FROM standing WHERE season_id = ? AND (exp > ? OR (exp = ? AND season_matches > ?))')
    .bind(seasonId, exp, exp, matches).first();
  return (r ? Number(r.n) : 0) + 1;
}

/**
 * What the board shows for one player. The stored flags are the union of everything that has
 * ever been raised this season; "new" is added at read time because it is about the player's
 * age, which changes without a submission.
 */
function publicRow(r, rank, t) {
  const flags = parseFlags(r.flags);
  const ageDays = (t - Number(r.first_seen)) / 86400;
  if (ageDays < NEW_PLAYER_DAYS && Number(r.season_matches) >= NEW_PLAYER_MATCHES && !flags.includes('new')) {
    flags.push('new');
  }
  return {
    rank,
    playerId: r.player_id,
    displayName: r.display_name,
    exp: Number(r.exp),
    wins: Number(r.wins),
    losses: Number(r.losses),
    seasonMatches: Number(r.season_matches),
    consecutiveWins: Number(r.consecutive_wins),
    elo: Number(r.elo || 0),
    snapshots: Number(r.snapshots),
    firstSeen: Number(r.first_seen),
    lastSeen: Number(r.last_seen),
    flags,
  };
}

async function readJson(request) {
  const len = Number(request.headers.get('content-length') || 0);
  if (len > MAX_BODY) return { error: 'body too large' };
  let text;
  try { text = await request.text(); } catch { return { error: 'unreadable body' }; }
  if (text.length > MAX_BODY) return { error: 'body too large' };
  try { return { value: JSON.parse(text) }; } catch { return { error: 'invalid json' }; }
}

function int(v, min, max) {
  if (v === null || v === undefined || v === '') return null;
  const n = typeof v === 'number' ? v : Number(v);
  if (!Number.isFinite(n) || Math.floor(n) !== n) return null;
  if (n < min || n > max) return null;
  return n;
}

/** Trim, drop control characters, cap the length. Empty after cleaning means invalid. */
export function cleanName(v) {
  if (typeof v !== 'string') return '';
  let s = '';
  for (const ch of v) {
    const c = ch.codePointAt(0);
    if (c < 0x20 || c === 0x7f) continue;
    s += ch;
  }
  s = s.trim().replace(/\s+/g, ' ');
  if (s.length > 24) s = s.slice(0, 24);
  return s;
}

function parseFlags(v) {
  try { const a = JSON.parse(v || '[]'); return Array.isArray(a) ? a.filter(x => typeof x === 'string') : []; }
  catch { return []; }
}

function union(a, b) {
  const out = a.slice();
  for (const f of b) if (!out.includes(f)) out.push(f);
  return out;
}

function json(obj, status = 200) {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': 'no-store' },
  });
}
