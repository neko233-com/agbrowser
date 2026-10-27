/**
 * Agent runtime: the layer that owns a browser session and exposes it to
 * tools. It keeps one active account/session at a time and transparently
 * restarts the browser when the account changes.
 */
import { EventEmitter } from 'node:events';
import { existsSync } from 'node:fs';
import { join } from 'node:path';

import { detectChannels, listProfiles } from '@agbrowser/core/browser/channels';
import { launchBrowser } from '@agbrowser/core/browser/launcher';
import { prepareProfile } from '@agbrowser/core/browser/profile';
import { installHardening } from '@agbrowser/core/browser/hardening';
import { PageController } from '@agbrowser/core/snapshot/page';
import { AdBlocker } from '@agbrowser/core/block/adblock';
import { RuleSet } from '@agbrowser/core/block/rules';
import { DEFAULT_FILTERS } from '@agbrowser/core/block/filters';
import { Store } from '@agbrowser/core/store/store';
import { SyncEngine } from '@agbrowser/core/sync/engine';

export class Runtime extends EventEmitter {
  /**
   * @param {object} opts
   * @param {string} [opts.dataDir] where the sqlite store lives
   * @param {boolean} [opts.headless]
   * @param {boolean} [opts.verticalTabs=true]
   * @param {boolean} [opts.adblock=true]
   * @param {string[]} [opts.extensionPaths]
   * @param {boolean} [opts.loadExtensions]
   */
  constructor({
    dataDir,
    headless = false,
    verticalTabs = true,
    adblock = true,
    extensionPaths = [],
    loadExtensions = false,
    store,
  } = {}) {
    super();
    this.setMaxListeners(0);
    this.headless = headless;
    this.verticalTabs = verticalTabs;
    this.extensionPaths = extensionPaths;
    this.loadExtensions = loadExtensions;

    this.store = store ?? new Store(dataDir ? join(dataDir, 'agbrowser.db') : ':memory:');
    this.sessions = new Map(); // accountId -> BrowserSession
    this.activeAccountId = null;
    this._activeTargetId = null;
    this._pages = new Map(); // targetId -> PageController

    this.ruleSet = new RuleSet().parse(DEFAULT_FILTERS);
    this.adblocker = new AdBlocker({ ruleSet: this.ruleSet, enabled: adblock });

    this.sync = new SyncEngine({
      store: this.store,
      getSession: (accountId) => this.sessions.get(accountId) ?? null,
    });
    this.sync.on('error', (err) => this.emit('syncError', err));
  }

  // -- accounts ------------------------------------------------------------

  /** Discover every installed channel and its profiles. */
  async listAccounts() {
    const channels = detectChannels();
    for (const channel of channels) {
      this.store.upsertChannel({
        id: channel.id,
        label: channel.label,
        binary: channel.binary,
        userDataDir: channel.userDataDir,
      });
      for (const account of channel.accounts) {
        this.store.upsertAccount({
          id: `${channel.id}/${account.id}`,
          channelId: channel.id,
          profileDir: account.id,
          name: account.name,
          email: account.email,
        });
      }
    }
    return {
      channels: channels.map((c) => ({
        id: c.id,
        label: c.label,
        binary: c.binary,
        userDataDir: c.userDataDir,
        accounts: c.accounts.map((a) => ({
          id: a.id,
          name: a.name,
          email: a.email,
          profileDir: a.profileDir,
        })),
      })),
      activeAccountId: this.activeAccountId,
    };
  }

  /**
   * Resolve an account id ("chrome/Default") to concrete paths.
   */
  resolveAccount(accountId) {
    const [channelId, profileId] = accountId.split('/');
    const channels = detectChannels();
    const channel = channels.find((c) => c.id === channelId);
    if (!channel) throw new Error(`Unknown channel: ${channelId}`);
    const account = channel.accounts.find((a) => a.id === profileId);
    if (!account) throw new Error(`Unknown account: ${accountId}`);
    return { channel, account };
  }

  /**
   * Switch the active account, launching the browser if needed.
   */
  async switchAccount(accountId) {
    if (this.activeAccountId === accountId && this.sessions.has(accountId)) {
      return this.sessions.get(accountId);
    }
    const { channel, account } = this.resolveAccount(accountId);

    // Prepare the native profile: enable the vertical tab strip in place.
    // The browser's own password manager, bookmarks and sync stay untouched.
    prepareProfile({
      profileDir: account.profileDir,
      verticalTabs: this.verticalTabs,
    });

    const session = await launchBrowser({
      binary: channel.binary,
      channelId: channel.id,
      accountId: account.id,
      userDataDir: channel.userDataDir,
      headless: this.headless,
      loadExtensions: this.loadExtensions,
      extensionPaths: this.extensionPaths,
      extraFlags: [
        '--disable-blink-features=AutomationControlled',
      ],
    });

    await this.adblocker.attach(session);
    await installHardening(session);

    this.sessions.set(accountId, session);
    this.activeAccountId = accountId;

    this._wireSession(accountId, session);

    return session;
  }

  /** Pick a sensible default account: the first Chrome profile, else Edge. */
  async ensureSession() {
    if (this.activeAccountId && this.sessions.has(this.activeAccountId)) {
      return this.sessions.get(this.activeAccountId);
    }
    const { channels } = await this.listAccounts();
    const preferred = channels.find((c) => c.id === 'chrome') ?? channels[0];
    if (!preferred) {
      throw new Error('No Chrome or Edge installation found.');
    }
    const accountId = `${preferred.id}/${preferred.accounts[0].id}`;
    return this.switchAccount(accountId);
  }

  _wireSession(accountId, session) {
    session.on('targetCreated', (info) => {
      this.emit('tabOpened', { accountId, targetId: info.targetId, url: info.url, title: info.title });
      // Mirror into the sync engine so other accounts follow along.
      this.sync.handleTabOpened(accountId, {
        id: info.targetId,
        url: info.url ?? 'about:blank',
        title: info.title ?? '',
        active: false,
      }).catch((err) => this.emit('syncError', err));
    });

    session.on('targetDestroyed', (targetId) => {
      this._pages.delete(targetId);
      this.emit('tabClosed', { accountId, targetId });
      this.sync.handleTabClosed(accountId, targetId).catch((err) => this.emit('syncError', err));
    });

    session.on('targetUpdated', (info) => {
      this.emit('tabUpdated', { accountId, targetId: info.targetId, url: info.url, title: info.title });
      this.sync.handleTabUpdated(accountId, {
        id: info.targetId,
        url: info.url,
        title: info.title,
        active: true,
        updatedAt: Date.now(),
        version: 1,
      }).catch((err) => this.emit('syncError', err));
    });
  }

  // -- pages ---------------------------------------------------------------

  async getSession() {
    return this.ensureSession();
  }

  /**
   * Get a page controller for the active tab, creating one if needed.
   */
  async getPage() {
    const session = await this.ensureSession();
    let targetId = this._activeTargetId;

    if (!targetId || !session.targets.has(targetId)) {
      const pages = await session.listPages();
      if (pages.length === 0) {
        targetId = await session.createPage('about:blank');
      } else {
        // Prefer a real web page over about:blank / chrome:// pages.
        const webPage = pages.find((p) => /^https?:/i.test(p.url ?? '')) ?? pages[0];
        targetId = webPage.targetId;
      }
      this._activeTargetId = targetId;
    }

    if (!this._pages.has(targetId)) {
      const page = new PageController({ session, targetId });
      this._pages.set(targetId, page);
    }
    return this._pages.get(targetId);
  }

  setActiveTarget(targetId) {
    this._activeTargetId = targetId;
  }

  activeTargetIndex() {
    const session = this.sessions.get(this.activeAccountId);
    if (!session) return -1;
    const ids = [...session.targets.keys()];
    return ids.indexOf(this._activeTargetId);
  }

  // -- sync ----------------------------------------------------------------

  async addSyncRule({ source, target, mode = 'mirror' }) {
    const id = `sync_${Date.now().toString(36)}_${Math.random().toString(36).slice(2, 8)}`;
    return this.store.createSyncRule({
      id,
      name: `${source} -> ${target}`,
      sourceAccount: source,
      targetAccount: target,
      mode,
    });
  }

  async listSyncRules() {
    return this.store.listSyncRules();
  }

  async runFullSync(source, target) {
    return this.sync.syncFull(source, target);
  }

  // -- diagnostics ---------------------------------------------------------

  async adblockStats() {
    return this.adblocker.report();
  }

  async status() {
    const session = this.sessions.get(this.activeAccountId);
    const pages = session ? await session.listPages() : [];
    return {
      activeAccount: this.activeAccountId,
      browser: session
        ? {
            channel: session.channelId,
            account: session.accountId,
            port: session.port,
            version: await this._browserVersion(session),
          }
        : null,
      tabs: pages.length,
      adblock: this.adblocker.report(),
      verticalTabs: this.verticalTabs,
      syncRules: this.store.listSyncRules().length,
    };
  }

  async _browserVersion(session) {
    try {
      const { product } = await session.connection.send('Browser.getVersion');
      return product;
    } catch {
      return 'unknown';
    }
  }

  /**
   * Network speed measurement from the host, plus page metrics when possible.
   */
  async speedTest(url = null) {
    const target = url ?? 'https://www.gstatic.com/generate_204';
    const samples = [];
    let latency = 0;

    for (let i = 0; i < 3; i++) {
      const t0 = performance.now();
      try {
        await fetch(`${target}${target.includes('?') ? '&' : '?'}agb=${Date.now()}-${i}`, {
          method: 'GET',
          cache: 'no-store',
          signal: AbortSignal.timeout(10_000),
        });
        samples.push(performance.now() - t0);
      } catch { /* skip */ }
    }
    if (samples.length) {
      const sorted = [...samples].sort((a, b) => a - b);
      latency = Math.round(sorted[Math.floor(sorted.length / 2)]);
    }

    // Download throughput using a sized payload.
    const bytes = 5_000_000;
    const dlTarget = 'https://speed.cloudflare.com/__down?bytes=' + bytes;
    let mbps = 0;
    try {
      const t0 = performance.now();
      const res = await fetch(dlTarget, { cache: 'no-store', signal: AbortSignal.timeout(20_000) });
      const reader = res.body.getReader();
      let got = 0;
      while (got < bytes) {
        const { done, value } = await reader.read();
        if (done) break;
        got += value.length;
      }
      const secs = (performance.now() - t0) / 1000;
      mbps = Number(((got * 8) / secs / 1_000_000).toFixed(2));
    } catch { /* throughput probe unavailable */ }

    let page = null;
    try {
      const p = await this.getPage();
      page = await p.evaluate(`(()=>{
        const nav = performance.getEntriesByType('navigation')[0];
        const paints = Object.fromEntries(performance.getEntriesByType('paint').map(p=>[p.name,p.startTime]));
        const res = performance.getEntriesByType('resource');
        let bytes=0; for(const r of res) bytes += r.transferSize||r.encodedBodySize||0;
        return {
          ttfb: nav?Math.round(nav.responseStart-nav.requestStart):0,
          fcp: Math.round(paints['first-contentful-paint']||0),
          load: nav?Math.round(nav.loadEventEnd-nav.startTime):0,
          resources: res.length,
          bytes,
          domNodes: document.getElementsByTagName('*').length
        };
      })()`);
    } catch { /* no page */ }

    return {
      timestamp: Date.now(),
      latencyMs: latency,
      jitterMs: samples.length > 1
        ? Math.round(Math.max(...samples) - Math.min(...samples))
        : 0,
      downloadMbps: mbps,
      page,
    };
  }

  async close() {
    for (const session of this.sessions.values()) {
      await session.close().catch(() => {});
    }
    this.sessions.clear();
    this.store.close();
  }
}
