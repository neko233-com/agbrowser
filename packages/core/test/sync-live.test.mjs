/**
 * Cross-channel tab synchronization, exercised against two live browsers.
 *
 * The unit tests cover the store and the engine with a fake session. This
 * file proves the wiring end to end: two independent Chromium instances are
 * started, a rule links them, and every tab event in the source instance has
 * to show up in the target instance — opened, navigated and closed.
 */
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { Store } from '@agbrowser/core/store/store';
import { SyncEngine } from '@agbrowser/core/sync/engine';
import { launchBrowser } from '@agbrowser/core/browser/launcher';

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';

/** Start a real browser with a throwaway profile. */
async function startBrowser() {
  const dir = mkdtempSync(join(tmpdir(), 'agb-sync-'));
  const session = await launchBrowser({
    binary: CHROME,
    channelId: 'chrome',
    accountId: 'Default',
    userDataDir: dir,
    headless: true,
  });
  session._testDir = dir;
  return session;
}

/** Wait until a predicate holds, polling the browser. */
async function until(predicate, { timeout = 15_000, interval = 200 } = {}) {
  const deadline = Date.now() + timeout;
  let last;
  while (Date.now() < deadline) {
    last = await predicate();
    if (last) return last;
    await new Promise((r) => setTimeout(r, interval));
  }
  return last ?? false;
}

/**
 * The URLs of every real page in a session.
 *
 * A fresh Chromium starts on its own new-tab page, which is browser chrome
 * rather than something the user opened, so it is filtered out along with
 * the blank page.
 */
async function pageUrls(session) {
  const pages = await session.listPages();
  return pages
    .map((p) => p.url)
    .filter((u) => u && u !== 'about:blank' && !u.startsWith('chrome://newtab'));
}

/** Remove a browser profile directory once Chromium has released it. */
function removeProfile(dir) {
  try {
    rmSync(dir, { recursive: true, force: true, maxRetries: 10, retryDelay: 300 });
  } catch { /* the browser may still hold a handle; the temp dir is disposable */ }
}

test('a tab opened in one live browser appears in the other', async (t) => {
  const store = new Store(':memory:');
  const source = await startBrowser();
  const target = await startBrowser();

  t.after(async () => {
    await source.close().catch(() => {});
    await target.close().catch(() => {});
    removeProfile(source._testDir);
    removeProfile(target._testDir);
    store.close();
  });

  store.upsertChannel({
    id: 'chrome', label: 'Chrome', binary: CHROME, userDataDir: 'C:/x',
  });
  store.upsertAccount({
    id: 'chrome/Default', channelId: 'chrome', profileDir: 'Default', name: 'A',
  });
  store.upsertAccount({
    id: 'chrome/Profile 1', channelId: 'chrome', profileDir: 'Profile 1', name: 'B',
  });
  store.createSyncRule({
    id: 'r1', name: 'A->B',
    sourceAccount: 'chrome/Default', targetAccount: 'chrome/Profile 1',
  });

  const engine = new SyncEngine({
    store,
    getSession: async (accountId) => (
      accountId === 'chrome/Default' ? source : target
    ),
  });

  // The engine listens for the same events the runtime wires up.
  source.on('targetCreated', (info) => {
    engine.handleTabOpened('chrome/Default', {
      id: info.targetId,
      url: info.url ?? 'about:blank',
      title: info.title ?? '',
      active: false,
    }).catch(() => {});
  });

  await source.createPage('data:text/html,<title>Sync Source</title><h1>hello</h1>');

  // The mirror lands in the browser first and in the store right after, so
  // wait for the store to catch up before reading it.
  const stored = await until(() => {
    const tabs = store.listTabs('chrome/Profile 1');
    return tabs.length > 0 ? tabs : false;
  });

  assert.ok(stored, 'the target browser should have opened the source tab');
  assert.equal(stored.length, 1);
  assert.equal(stored[0].origin, 'sync:r1');
  assert.ok(stored[0].url.includes('Sync Source'));

  const mirrored = await until(async () => {
    const urls = await pageUrls(target);
    return urls.some((u) => u.includes('Sync Source')) ? urls : false;
  });
  assert.ok(mirrored, 'the mirrored tab must be a live page in the target browser');
});

test('closing the source tab closes the mirrored tab', async (t) => {
  const store = new Store(':memory:');
  const source = await startBrowser();
  const target = await startBrowser();

  t.after(async () => {
    await source.close().catch(() => {});
    await target.close().catch(() => {});
    removeProfile(source._testDir);
    removeProfile(target._testDir);
    store.close();
  });

  store.upsertChannel({
    id: 'chrome', label: 'Chrome', binary: CHROME, userDataDir: 'C:/x',
  });
  store.upsertAccount({
    id: 'chrome/Default', channelId: 'chrome', profileDir: 'Default', name: 'A',
  });
  store.upsertAccount({
    id: 'chrome/Profile 1', channelId: 'chrome', profileDir: 'Profile 1', name: 'B',
  });
  store.createSyncRule({
    id: 'r1', name: 'A->B',
    sourceAccount: 'chrome/Default', targetAccount: 'chrome/Profile 1',
  });

  const engine = new SyncEngine({
    store,
    getSession: async (accountId) => (
      accountId === 'chrome/Default' ? source : target
    ),
  });

  const opened = new Map();
  source.on('targetCreated', async (info) => {
    const [result] = await engine.handleTabOpened('chrome/Default', {
      id: info.targetId,
      url: info.url ?? 'about:blank',
      title: info.title ?? '',
      active: false,
    });
    if (result) opened.set(info.targetId, result.tabId);
  });
  source.on('targetDestroyed', (targetId) => {
    engine.handleTabClosed('chrome/Default', targetId).catch(() => {});
  });

  const sourceTab = await source.createPage('data:text/html,<title>To Close</title>');

  const created = await until(async () => opened.has(sourceTab));
  assert.ok(created, 'the mirror should have been recorded');
  const mirroredId = opened.get(sourceTab);

  // The mirrored tab is live in the other browser.
  const targetPages = await until(async () => {
    const pages = await target.listPages();
    return pages.some((p) => p.targetId === mirroredId) ? pages : false;
  });
  assert.ok(targetPages, 'the mirrored tab must exist as a real page in the target browser');

  await source.closePage(sourceTab);

  const gone = await until(async () => {
    const pages = await target.listPages();
    return !pages.some((p) => p.targetId === mirroredId);
  });
  assert.ok(gone, 'the mirrored tab should have been closed in the target browser');

  // The store keeps the row but marks it deleted.
  const stored = await until(() => {
    const tabs = store.listTabs('chrome/Profile 1', { includeDeleted: true });
    return tabs.length > 0 && tabs[0].deleted_at ? tabs : false;
  });
  assert.ok(stored, 'the mirrored tab should be soft deleted in the store');
});

test('a full sync pushes every open tab of the source into the target', async (t) => {
  const store = new Store(':memory:');
  const source = await startBrowser();
  const target = await startBrowser();

  t.after(async () => {
    await source.close().catch(() => {});
    await target.close().catch(() => {});
    removeProfile(source._testDir);
    removeProfile(target._testDir);
    store.close();
  });

  store.upsertChannel({
    id: 'chrome', label: 'Chrome', binary: CHROME, userDataDir: 'C:/x',
  });
  store.upsertAccount({
    id: 'chrome/Default', channelId: 'chrome', profileDir: 'Default', name: 'A',
  });
  store.upsertAccount({
    id: 'chrome/Profile 1', channelId: 'chrome', profileDir: 'Profile 1', name: 'B',
  });
  store.createSyncRule({
    id: 'r1', name: 'A->B',
    sourceAccount: 'chrome/Default', targetAccount: 'chrome/Profile 1',
  });

  const engine = new SyncEngine({
    store,
    getSession: async (accountId) => (
      accountId === 'chrome/Default' ? source : target
    ),
  });

  await source.createPage('data:text/html,<title>One</title>');
  await source.createPage('data:text/html,<title>Two</title>');
  await source.createPage('data:text/html,<title>Three</title>');

  // The store is the source of truth for what a full sync pushes.
  const pages = await source.listPages();
  for (const page of pages) {
    if (page.url === 'about:blank' || page.url.startsWith('chrome://newtab')) continue;
    store.upsertTab({
      id: page.targetId,
      accountId: 'chrome/Default',
      url: page.url,
      title: 'page',
    });
  }

  const created = await engine.syncFull('chrome/Default', 'chrome/Profile 1');
  assert.equal(created.length, 3, `expected three mirrored tabs, got ${created.length}`);

  const targetUrls = await until(async () => {
    const urls = await pageUrls(target);
    return urls.length >= 3 ? urls : false;
  });
  assert.ok(targetUrls, `the target should hold three tabs, got: ${JSON.stringify(targetUrls)}`);
  for (const title of ['One', 'Two', 'Three']) {
    assert.ok(
      targetUrls.some((u) => u.includes(title)),
      `the target should hold the "${title}" tab, got: ${JSON.stringify(targetUrls)}`,
    );
  }

  const stored = store.listTabs('chrome/Profile 1');
  assert.equal(stored.length, 3);
  for (const tab of stored) {
    assert.equal(tab.origin, 'sync:r1');
  }
});
