import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

import { Store } from '@agbrowser/core/store/store';
import { SyncEngine } from '@agbrowser/core/sync/engine';
import {
  enableVerticalTabs,
  readPreferences,
  VERTICAL_TABS_PREF,
} from '@agbrowser/core/browser/profile';

function freshStore() {
  return new Store(':memory:');
}

function seed(store) {
  store.upsertChannel({
    id: 'chrome', label: 'Chrome', binary: '/x/chrome', userDataDir: '/x/chrome-data',
  });
  store.upsertChannel({
    id: 'edge', label: 'Edge', binary: '/x/edge', userDataDir: '/x/edge-data',
  });
  store.upsertAccount({
    id: 'chrome/Default', channelId: 'chrome', profileDir: 'Default', name: 'Chrome Main',
  });
  store.upsertAccount({
    id: 'edge/Default', channelId: 'edge', profileDir: 'Default', name: 'Edge Main',
  });
}

test('channels and accounts round-trip through the store', () => {
  const store = freshStore();
  seed(store);
  assert.equal(store.listChannels().length, 2);
  assert.equal(store.listAccounts().length, 2);
  assert.equal(store.getAccount('edge/Default').name, 'Edge Main');
  store.close();
});

test('tabs are stored and listed per account', () => {
  const store = freshStore();
  seed(store);
  store.upsertTab({
    id: 't1', accountId: 'chrome/Default', url: 'https://a.test/', title: 'A',
  });
  store.upsertTab({
    id: 't2', accountId: 'edge/Default', url: 'https://b.test/', title: 'B',
  });
  assert.equal(store.listTabs('chrome/Default').length, 1);
  assert.equal(store.listTabs('edge/Default').length, 1);
  store.close();
});

test('upserting a tab bumps its version', () => {
  const store = freshStore();
  seed(store);
  store.upsertTab({ id: 't1', accountId: 'chrome/Default', url: 'https://a.test/', title: 'A' });
  const v1 = store.getTab('t1').version;
  store.upsertTab({ id: 't1', accountId: 'chrome/Default', url: 'https://a.test/', title: 'A2' });
  const v2 = store.getTab('t1').version;
  assert.ok(v2 > v1, `version should increase: ${v1} -> ${v2}`);
  assert.equal(store.getTab('t1').title, 'A2');
  store.close();
});

test('soft delete hides a tab but keeps the row', () => {
  const store = freshStore();
  seed(store);
  store.upsertTab({ id: 't1', accountId: 'chrome/Default', url: 'https://a.test/' });
  store.softDeleteTab('t1');
  assert.equal(store.listTabs('chrome/Default').length, 0);
  assert.equal(store.listTabs('chrome/Default', { includeDeleted: true }).length, 1);
  store.close();
});

test('sync rules are created and listed', () => {
  const store = freshStore();
  seed(store);
  store.createSyncRule({
    id: 'r1', name: 'c->e', sourceAccount: 'chrome/Default', targetAccount: 'edge/Default',
  });
  const rules = store.listSyncRules({ enabledOnly: true });
  assert.equal(rules.length, 1);
  assert.equal(rules[0].source_account, 'chrome/Default');
  store.close();
});

test('the sync engine mirrors an opened tab into the target account', async () => {
  const store = freshStore();
  seed(store);
  store.createSyncRule({
    id: 'r1', name: 'c->e', sourceAccount: 'chrome/Default', targetAccount: 'edge/Default',
  });

  const opened = [];
  const engine = new SyncEngine({
    store,
    getSession: async () => ({
      createPage: async (url) => { opened.push(url); return 'target-tab-1'; },
      closePage: async () => {},
      attach: async () => 'sid',
      connection: { trySend: async () => {} },
    }),
  });

  const result = await engine.handleTabOpened('chrome/Default', {
    id: 'src-tab-1', url: 'https://example.test/', title: 'Example', active: false,
  });

  assert.equal(result.length, 1);
  assert.equal(result[0].accountId, 'edge/Default');
  assert.deepEqual(opened, ['https://example.test/']);

  const mirrored = store.listTabs('edge/Default');
  assert.equal(mirrored.length, 1);
  assert.equal(mirrored[0].url, 'https://example.test/');
  store.close();
});

test('the sync engine closes the mirrored tab when the source tab closes', async () => {
  const store = freshStore();
  seed(store);
  store.createSyncRule({
    id: 'r1', name: 'c->e', sourceAccount: 'chrome/Default', targetAccount: 'edge/Default',
  });

  let closedId = null;
  const engine = new SyncEngine({
    store,
    getSession: async () => ({
      createPage: async () => 'target-tab-1',
      closePage: async (id) => { closedId = id; },
      attach: async () => 'sid',
      connection: { trySend: async () => {} },
    }),
  });

  await engine.handleTabOpened('chrome/Default', {
    id: 'src-tab-1', url: 'https://example.test/', title: 'Example',
  });
  await engine.handleTabClosed('chrome/Default', 'src-tab-1');

  assert.equal(closedId, 'target-tab-1');
  assert.equal(store.listTabs('edge/Default').length, 0);
  store.close();
});

test('active-only mode ignores background tabs', async () => {
  const store = freshStore();
  seed(store);
  store.createSyncRule({
    id: 'r1', name: 'c->e', sourceAccount: 'chrome/Default', targetAccount: 'edge/Default',
    mode: 'active-only',
  });

  let created = 0;
  const engine = new SyncEngine({
    store,
    getSession: async () => ({
      createPage: async () => { created++; return 't'; },
      closePage: async () => {},
      attach: async () => 'sid',
      connection: { trySend: async () => {} },
    }),
  });

  await engine.handleTabOpened('chrome/Default', {
    id: 'bg', url: 'https://bg.test/', title: 'bg', active: false,
  });
  assert.equal(created, 0);

  await engine.handleTabOpened('chrome/Default', {
    id: 'fg', url: 'https://fg.test/', title: 'fg', active: true,
  });
  assert.equal(created, 1);
  store.close();
});

test('the vertical tab preference is written into a profile', () => {
  const dir = mkdtempSync(join(tmpdir(), 'agb-prof-'));
  try {
    const result = enableVerticalTabs(dir);
    assert.equal(result.changed, true);
    const prefs = readPreferences(dir);
    assert.equal(prefs.vertical_tabs.enabled, true);
    assert.equal(prefs.vertical_tabs.uncollapsed_width, VERTICAL_TABS_PREF.uncollapsed_width);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test('enabling vertical tabs twice is idempotent', () => {
  const dir = mkdtempSync(join(tmpdir(), 'agb-prof-'));
  try {
    enableVerticalTabs(dir);
    const second = enableVerticalTabs(dir);
    assert.equal(second.changed, false);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
