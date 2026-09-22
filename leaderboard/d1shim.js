// A Cloudflare D1 look-alike over Node's built-in node:sqlite, so the Worker can be exercised
// end to end with nothing installed. Covers exactly the surface worker.js uses:
//   db.prepare(sql).bind(...args).first() / .all() / .run()   and   db.batch([...])
import { DatabaseSync } from 'node:sqlite';

export function openMemoryDb(schemaSql) {
  const db = new DatabaseSync(':memory:');
  db.exec(schemaSql);
  return new D1Shim(db);
}

class D1Shim {
  constructor(db) { this.db = db; }
  prepare(sql) { return new Stmt(this.db, sql); }
  async batch(stmts) {
    // D1 runs a batch as one transaction. Mirror that, since the Worker relies on it.
    this.db.exec('BEGIN');
    try {
      const out = [];
      for (const s of stmts) out.push(await s.run());
      this.db.exec('COMMIT');
      return out;
    } catch (e) {
      this.db.exec('ROLLBACK');
      throw e;
    }
  }
}

class Stmt {
  constructor(db, sql) { this.db = db; this.sql = sql; this.args = []; }
  bind(...args) { this.args = args.map(a => a === undefined ? null : a); return this; }
  async first(col) {
    const row = this.db.prepare(this.sql).get(...this.args);
    if (row === undefined) return null;
    return col ? row[col] : row;
  }
  async all() {
    return { results: this.db.prepare(this.sql).all(...this.args), success: true };
  }
  async run() {
    const r = this.db.prepare(this.sql).run(...this.args);
    return { success: true, meta: { changes: r.changes, last_row_id: r.lastInsertRowid } };
  }
}
