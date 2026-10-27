/**
 * Cross-account tab synchronization.
 *
 * A sync rule links a *source* account to a *target* account. When the source
 * account opens, closes, or updates a tab, the engine mirrors that change into
 * the target account's live browser session (if running) and persists it to
 * the store so the state survives restarts.
 *
 * Conflict resolution is last-writer-wins on (version, updated_at). Because
 * tabs carry a monotonic version, the newest write always wins regardless of
 * the order events arrive in.
 */
import { EventEmitter } from 'node:events';

export const SYNC_MODES = {
  /** Mirror every tab event into the target. */
  MIRROR: 'mirror',
  /** Only mirror the currently active tab. */
  ACTIVE_ONLY: 'active-only',
  /** Mirror tab metadata but never navigate the target. */
  METADATA_ONLY: 'metadata-only',
};

export class SyncEngine extends EventEmitter {
  /**
   * @param {object} opts
   * @param {import('../store/store.js').Store} opts.store
   * @param {(accountId:string)=>Promise<any>} opts.getSession resolve a live session for an account
   */
  constructor({ store, getSession }) {
    super();
    this.setMaxListeners(0);
    this.store = store;
    this.getSession = getSession;
    /** accountId -> Map<sourceTabId, {targetTabId, ruleId}> */
    this._mappings = new Map();
  }

  /** Register the mappings we already know about from the store. */
  loadMappings() {
    for (const rule of this.store.listSyncRules({ enabledOnly: true })) {
      const map = this._mappingFor(rule.target_account);
      const sourceTabs = this.store.listTabs(rule.source_account);
      const targetTabs = this.store.listTabs(rule.target_account);
      for (const src of sourceTabs) {
        const match = targetTabs.find((t) => t.url === src.url && t.title === src.title);
        if (match) map.set(src.id, { targetTabId: match.id, ruleId: rule.id });
      }
    }
  }

  _mappingFor(accountId) {
    if (!this._mappings.has(accountId)) this._mappings.set(accountId, new Map());
    return this._mappings.get(accountId);
  }

  /** Rules whose source is this account. */
  rulesForSource(accountId) {
    return this.store.listSyncRules({ enabledOnly: true }).filter((r) => r.source_account === accountId);
  }

  /**
   * Handle a tab opened in the source account.
   * @returns {Promise<Array<{accountId:string, tabId:string}>>}
   */
  async handleTabOpened(sourceAccountId, tab) {
    const results = [];
    for (const rule of this.rulesForSource(sourceAccountId)) {
      if (!this._shouldSync(rule, tab)) continue;

      const targetSession = await this.getSession(rule.target_account);
      let targetTabId = null;

      if (targetSession) {
        try {
          targetTabId = await targetSession.createPage(tab.url);
          // Mirror title once the page has a title.
          targetSession.connection.on?.('Target.targetInfoChanged', () => {});
          await this._applyTitle(targetSession, targetTabId, tab.title);
        } catch (err) {
          this.emit('error', err);
        }
      }

      const persisted = this.store.upsertTab({
        id: targetTabId ?? `${rule.id}:${tab.id}`,
        accountId: rule.target_account,
        url: tab.url,
        title: tab.title,
        groupId: tab.groupId,
        pinned: tab.pinned,
        origin: `sync:${rule.id}`,
      });

      this._mappingFor(rule.target_account).set(tab.id, {
        targetTabId: persisted.id,
        ruleId: rule.id,
      });

      this.store.logSync({
        ruleId: rule.id,
        tabId: tab.id,
        action: 'opened',
        targetAccount: rule.target_account,
        detail: tab.url,
      });
      results.push({ accountId: rule.target_account, tabId: persisted.id });
    }
    return results;
  }

  /**
   * Handle a tab closed in the source account.
   */
  async handleTabClosed(sourceAccountId, tabId) {
    const results = [];
    for (const rule of this.rulesForSource(sourceAccountId)) {
      const mapping = this._mappingFor(rule.target_account).get(tabId);
      if (!mapping || mapping.ruleId !== rule.id) continue;

      const targetSession = await this.getSession(rule.target_account);
      if (targetSession && mapping.targetTabId) {
        try {
          await targetSession.closePage(mapping.targetTabId);
        } catch (err) {
          this.emit('error', err);
        }
      }

      this.store.softDeleteTab(mapping.targetTabId);
      this.store.logSync({
        ruleId: rule.id,
        tabId,
        action: 'closed',
        targetAccount: rule.target_account,
      });
      this._mappingFor(rule.target_account).delete(tabId);
      results.push({ accountId: rule.target_account, tabId: mapping.targetTabId });
    }
    return results;
  }

  /**
   * Handle a tab update (title / url / active change) in the source account.
   */
  async handleTabUpdated(sourceAccountId, tab) {
    const results = [];
    for (const rule of this.rulesForSource(sourceAccountId)) {
      if (!this._shouldSync(rule, tab)) continue;
      const mapping = this._mappingFor(rule.target_account).get(tab.id);
      if (!mapping || mapping.ruleId !== rule.id) continue;

      const targetSession = await this.getSession(rule.target_account);
      if (targetSession && mapping.targetTabId) {
        try {
          if (tab.url && rule.mode !== SYNC_MODES.METADATA_ONLY) {
            const sessionId = await targetSession.attach(mapping.targetTabId);
            await targetSession.connection.trySend('Page.navigate', { url: tab.url }, sessionId);
          }
          await this._applyTitle(targetSession, mapping.targetTabId, tab.title);
        } catch (err) {
          this.emit('error', err);
        }
      }

      const existing = this.store.getTab(mapping.targetTabId);
      if (existing && existing.updated_at > (tab.updatedAt ?? 0) && existing.version >= (tab.version ?? 0)) {
        // The target already has a newer write; keep it (LWW).
        continue;
      }

      this.store.upsertTab({
        id: mapping.targetTabId,
        accountId: rule.target_account,
        url: tab.url ?? existing?.url ?? 'about:blank',
        title: tab.title ?? existing?.title ?? '',
        groupId: tab.groupId ?? existing?.group_id ?? null,
        pinned: tab.pinned ?? Boolean(existing?.pinned),
        origin: `sync:${rule.id}`,
      });

      this.store.logSync({
        ruleId: rule.id,
        tabId: tab.id,
        action: 'updated',
        targetAccount: rule.target_account,
        detail: tab.url ?? '',
      });
      results.push({ accountId: rule.target_account, tabId: mapping.targetTabId });
    }
    return results;
  }

  /** Push every open tab of the source account into the target account. */
  async syncFull(sourceAccountId, targetAccountId) {
    const rule = this.store.listSyncRules({ enabledOnly: true }).find(
      (r) => r.source_account === sourceAccountId && r.target_account === targetAccountId,
    );
    if (!rule) throw new Error(`No enabled sync rule between ${sourceAccountId} and ${targetAccountId}`);

    const sourceTabs = this.store.listTabs(sourceAccountId);
    const targetSession = await this.getSession(targetAccountId);
    const created = [];

    for (const tab of sourceTabs) {
      if (!this._shouldSync(rule, tab)) continue;
      const mapping = this._mappingFor(targetAccountId).get(tab.id);
      if (mapping) continue;

      let targetTabId = null;
      if (targetSession) {
        try {
          targetTabId = await targetSession.createPage(tab.url);
          await this._applyTitle(targetSession, targetTabId, tab.title);
        } catch (err) {
          this.emit('error', err);
        }
      }
      const persisted = this.store.upsertTab({
        id: targetTabId ?? `${rule.id}:${tab.id}`,
        accountId: targetAccountId,
        url: tab.url,
        title: tab.title,
        groupId: tab.groupId,
        pinned: tab.pinned,
        origin: `sync:${rule.id}`,
      });
      this._mappingFor(targetAccountId).set(tab.id, { targetTabId: persisted.id, ruleId: rule.id });
      created.push(persisted.id);
    }

    this.store.logSync({
      ruleId: rule.id,
      tabId: '*',
      action: 'full-sync',
      targetAccount: targetAccountId,
      detail: `${created.length} tabs`,
    });
    return created;
  }

  _shouldSync(rule, tab) {
    if (rule.mode === SYNC_MODES.ACTIVE_ONLY && !tab.active) return false;
    if (!rule.include_pinned && tab.pinned) return false;
    if (!rule.include_groups && tab.groupId) return false;
    return true;
  }

  async _applyTitle(session, targetId, title) {
    if (!title || !session) return;
    try {
      const sessionId = await session.attach(targetId);
      await session.connection.trySend('Runtime.evaluate', {
        expression: `(()=>{try{document.title=${JSON.stringify(title)}}catch(e){}})()`,
      }, sessionId);
    } catch { /* title is best-effort */ }
  }
}
