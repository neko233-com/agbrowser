/**
 * Persistent store for channels, accounts, tabs and sync rules.
 *
 * Uses Node's built-in `node:sqlite` (Node >= 22.5) so agbrowser ships with
 * zero runtime dependencies. The schema is intentionally relational and
 * small; everything is local-first.
 */
import { DatabaseSync } from 'node:sqlite';
import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';

export const SCHEMA = `
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS channels (
  id           TEXT PRIMARY KEY,
  label        TEXT NOT NULL,
  binary       TEXT NOT NULL,
  user_data_dir TEXT NOT NULL,
  enabled      INTEGER NOT NULL DEFAULT 1,
  created_at   INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS accounts (
  id           TEXT PRIMARY KEY,
  channel_id   TEXT NOT NULL REFERENCES channels(id) ON DELETE CASCADE,
  profile_dir  TEXT NOT NULL,
  name         TEXT NOT NULL,
  email        TEXT,
  enabled      INTEGER NOT NULL DEFAULT 1,
  created_at   INTEGER NOT NULL,
  UNIQUE(channel_id, profile_dir)
);

CREATE TABLE IF NOT EXISTS tabs (
  id            TEXT PRIMARY KEY,
  account_id    TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  url           TEXT NOT NULL,
  title         TEXT NOT NULL DEFAULT '',
  favicon       TEXT,
  group_id      TEXT REFERENCES tab_groups(id) ON DELETE SET NULL,
  pinned        INTEGER NOT NULL DEFAULT 0,
  active        INTEGER NOT NULL DEFAULT 0,
  index_in_win  INTEGER NOT NULL DEFAULT 0,
  last_active   INTEGER NOT NULL DEFAULT 0,
  created_at    INTEGER NOT NULL,
  updated_at    INTEGER NOT NULL,
  deleted_at    INTEGER,
  version       INTEGER NOT NULL DEFAULT 1,
  origin        TEXT NOT NULL DEFAULT 'local'
);

CREATE TABLE IF NOT EXISTS tab_groups (
  id         TEXT PRIMARY KEY,
  account_id TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  name       TEXT NOT NULL,
  color      TEXT,
  collapsed  INTEGER NOT NULL DEFAULT 0,
  index_in_win INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sync_rules (
  id              TEXT PRIMARY KEY,
  name            TEXT NOT NULL,
  source_account  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  target_account  TEXT NOT NULL REFERENCES accounts(id) ON DELETE CASCADE,
  mode            TEXT NOT NULL DEFAULT 'mirror',
  include_groups  INTEGER NOT NULL DEFAULT 1,
  include_pinned  INTEGER NOT NULL DEFAULT 1,
  enabled         INTEGER NOT NULL DEFAULT 1,
  created_at      INTEGER NOT NULL
);

CREATE TABLE IF NOT EXISTS sync_log (
  id          INTEGER PRIMARY KEY AUTOINCREMENT,
  rule_id     TEXT NOT NULL,
  tab_id      TEXT NOT NULL,
  action      TEXT NOT NULL,
  target_account TEXT NOT NULL,
  detail      TEXT,
  created_at  INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS idx_tabs_account ON tabs(account_id);
CREATE INDEX IF NOT EXISTS idx_tabs_group ON tabs(group_id);
CREATE INDEX IF NOT EXISTS idx_groups_account ON tab_groups(account_id);
CREATE INDEX IF NOT EXISTS idx_sync_rules_source ON sync_rules(source_account);
CREATE INDEX IF NOT EXISTS idx_sync_log_created ON sync_log(created_at);
`;

export class Store {
  /**
   * @param {string} [dbPath] file path, or ':memory:' for an ephemeral store
   */
  constructor(dbPath = ':memory:') {
    if (dbPath !== ':memory:') {
      mkdirSync(dirname(dbPath), { recursive: true });
    }
    this.db = new DatabaseSync(dbPath);
    this.db.exec(SCHEMA);
    this.path = dbPath;
  }

  close() {
    try {
      this.db.close();
    } catch { /* already closed */ }
  }

  // -- channels ------------------------------------------------------------

  upsertChannel({ id, label, binary, userDataDir, enabled = 1 }) {
    this.db.prepare(`
      INSERT INTO channels (id, label, binary, user_data_dir, enabled, created_at)
      VALUES (?, ?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        label = excluded.label,
        binary = excluded.binary,
        user_data_dir = excluded.user_data_dir,
        enabled = excluded.enabled
    `).run(id, label, binary, userDataDir, enabled, Date.now());
    return this.getChannel(id);
  }

  getChannel(id) {
    return this.db.prepare('SELECT * FROM channels WHERE id = ?').get(id) ?? null;
  }

  listChannels() {
    return this.db.prepare('SELECT * FROM channels ORDER BY id').all();
  }

  removeChannel(id) {
    this.db.prepare('DELETE FROM channels WHERE id = ?').run(id);
  }

  // -- accounts ------------------------------------------------------------

  upsertAccount({ id, channelId, profileDir, name, email = null, enabled = 1 }) {
    this.db.prepare(`
      INSERT INTO accounts (id, channel_id, profile_dir, name, email, enabled, created_at)
      VALUES (?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        channel_id = excluded.channel_id,
        profile_dir = excluded.profile_dir,
        name = excluded.name,
        email = excluded.email,
        enabled = excluded.enabled
    `).run(id, channelId, profileDir, name, email, enabled, Date.now());
    return this.getAccount(id);
  }

  getAccount(id) {
    return this.db.prepare('SELECT * FROM accounts WHERE id = ?').get(id) ?? null;
  }

  listAccounts(channelId = null) {
    if (channelId) {
      return this.db.prepare('SELECT * FROM accounts WHERE channel_id = ? ORDER BY id').all(channelId);
    }
    return this.db.prepare('SELECT * FROM accounts ORDER BY id').all();
  }

  removeAccount(id) {
    this.db.prepare('DELETE FROM accounts WHERE id = ?').run(id);
  }

  // -- tab groups ----------------------------------------------------------

  upsertGroup({ id, accountId, name, color = null, collapsed = 0, indexInWin = 0 }) {
    this.db.prepare(`
      INSERT INTO tab_groups (id, account_id, name, color, collapsed, index_in_win, updated_at)
      VALUES (?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        name = excluded.name,
        color = excluded.color,
        collapsed = excluded.collapsed,
        index_in_win = excluded.index_in_win,
        updated_at = excluded.updated_at
    `).run(id, accountId, name, color, collapsed, indexInWin, Date.now());
    return id;
  }

  listGroups(accountId) {
    return this.db.prepare('SELECT * FROM tab_groups WHERE account_id = ? ORDER BY index_in_win').all(accountId);
  }

  removeGroup(id) {
    this.db.prepare('DELETE FROM tab_groups WHERE id = ?').run(id);
  }

  // -- tabs ----------------------------------------------------------------

  /**
   * Upsert a tab. Version is bumped on every write so that the sync engine
   * can resolve conflicts using last-writer-wins on (version, updated_at).
   */
  upsertTab(tab) {
    const now = Date.now();
    const existing = this.getTab(tab.id);
    const version = existing ? existing.version + 1 : 1;
    this.db.prepare(`
      INSERT INTO tabs (id, account_id, url, title, favicon, group_id, pinned, active,
                       index_in_win, last_active, created_at, updated_at, deleted_at, version, origin)
      VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        url = excluded.url,
        title = excluded.title,
        favicon = excluded.favicon,
        group_id = excluded.group_id,
        pinned = excluded.pinned,
        active = excluded.active,
        index_in_win = excluded.index_in_win,
        last_active = excluded.last_active,
        updated_at = excluded.updated_at,
        deleted_at = excluded.deleted_at,
        version = excluded.version,
        origin = excluded.origin
    `).run(
      tab.id,
      tab.accountId,
      tab.url,
      tab.title ?? '',
      tab.favicon ?? null,
      tab.groupId ?? null,
      tab.pinned ? 1 : 0,
      tab.active ? 1 : 0,
      tab.indexInWin ?? 0,
      tab.lastActive ?? now,
      existing?.created_at ?? now,
      now,
      tab.deletedAt ?? null,
      version,
      tab.origin ?? 'local',
    );
    return this.getTab(tab.id);
  }

  getTab(id) {
    return this.db.prepare('SELECT * FROM tabs WHERE id = ?').get(id) ?? null;
  }

  listTabs(accountId, { includeDeleted = false } = {}) {
    const sql = includeDeleted
      ? 'SELECT * FROM tabs WHERE account_id = ? ORDER BY index_in_win, created_at'
      : 'SELECT * FROM tabs WHERE account_id = ? AND deleted_at IS NULL ORDER BY index_in_win, created_at';
    return this.db.prepare(sql).all(accountId);
  }

  softDeleteTab(id) {
    const now = Date.now();
    this.db.prepare(`
      UPDATE tabs SET deleted_at = ?, updated_at = ?, version = version + 1
      WHERE id = ?
    `).run(now, now, id);
  }

  hardDeleteTab(id) {
    this.db.prepare('DELETE FROM tabs WHERE id = ?').run(id);
  }

  // -- sync rules ----------------------------------------------------------

  createSyncRule({ id, name, sourceAccount, targetAccount, mode = 'mirror', includeGroups = true, includePinned = true }) {
    this.db.prepare(`
      INSERT INTO sync_rules (id, name, source_account, target_account, mode, include_groups, include_pinned, enabled, created_at)
      VALUES (?, ?, ?, ?, ?, ?, ?, 1, ?)
    `).run(id, name, sourceAccount, targetAccount, mode, includeGroups ? 1 : 0, includePinned ? 1 : 0, Date.now());
    return this.getSyncRule(id);
  }

  getSyncRule(id) {
    return this.db.prepare('SELECT * FROM sync_rules WHERE id = ?').get(id) ?? null;
  }

  listSyncRules({ enabledOnly = false } = {}) {
    const sql = enabledOnly
      ? 'SELECT * FROM sync_rules WHERE enabled = 1'
      : 'SELECT * FROM sync_rules';
    return this.db.prepare(sql).all();
  }

  setSyncRuleEnabled(id, enabled) {
    this.db.prepare('UPDATE sync_rules SET enabled = ? WHERE id = ?').run(enabled ? 1 : 0, id);
  }

  removeSyncRule(id) {
    this.db.prepare('DELETE FROM sync_rules WHERE id = ?').run(id);
  }

  // -- sync log ------------------------------------------------------------

  logSync({ ruleId, tabId, action, targetAccount, detail = null }) {
    this.db.prepare(`
      INSERT INTO sync_log (rule_id, tab_id, action, target_account, detail, created_at)
      VALUES (?, ?, ?, ?, ?, ?)
    `).run(ruleId, tabId, action, targetAccount, detail, Date.now());
  }

  listSyncLog(limit = 100) {
    return this.db.prepare('SELECT * FROM sync_log ORDER BY created_at DESC LIMIT ?').all(limit);
  }
}
