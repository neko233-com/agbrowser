import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const base = new URL('../', import.meta.url);
function event() { const handlers = []; return { addListener: fn => handlers.push(fn), fire: (...args) => handlers.forEach(fn => fn(...args)) }; }
async function flush() { await new Promise(resolve => setTimeout(resolve, 20)); }

test('extension is limited to bookmarks and native messaging and performs requested operations', async () => {
  const manifest = JSON.parse(readFileSync(new URL('manifest.json', base), 'utf8'));
  assert.deepEqual(manifest.permissions.slice().sort(), ['alarms', 'bookmarks', 'nativeMessaging']);
  const digest = createHash('sha256').update(Buffer.from(manifest.key, 'base64')).digest().subarray(0, 16);
  const id = [...digest].flatMap(b => [b >> 4, b & 15]).map(n => String.fromCharCode(97 + n)).join('');
  assert.equal(id, 'fjkcppekpigobilckiggnjdfeiiabmee');
  const messages = [], calls = [];
  const port = { onMessage: event(), onDisconnect: event(), postMessage: msg => messages.push(msg) };
  const bookmarks = {
    getTree: async () => [{ id: '0', children: [{ id: '1', children: [] }] }],
    create: async data => { calls.push(['create', data]); return { id: '42', ...data }; },
    update: async (id, data) => { calls.push(['update', id, data]); return { id, ...data }; },
    remove: async id => { calls.push(['remove', id]); },
    removeTree: async id => { calls.push(['removeTree', id]); },
    onCreated: event(), onRemoved: event(), onChanged: event(), onMoved: event(), onChildrenReordered: event(), onImportEnded: event(),
  };
  const chrome = { bookmarks, runtime: { connectNative: name => { assert.equal(name, 'com.agbrowser.edge_favorites'); return port; }, onStartup: event(), onInstalled: event() }, alarms: { get: async () => ({ name: 'reconnect' }), create() {}, onAlarm: event() } };
  runInNewContext(readFileSync(fileURLToPath(new URL('service-worker.js', base)), 'utf8'), { chrome, setTimeout, clearTimeout, Promise });
  await flush();
  assert.ok(messages.some(x => x.type === 'snapshot' && x.tree[0].children[0].id === '1'));
  port.onMessage.fire({ op: 'create', id: 'a', parentId: '1', title: 'Example', url: 'https://example.com/' });
  port.onMessage.fire({ op: 'update', id: 'b', bookmarkId: '42', title: 'Updated', url: 'https://example.org/' });
  port.onMessage.fire({ op: 'remove', id: 'c', bookmarkId: '42' });
  port.onMessage.fire({ op: 'create', id: 'd', parentId: '1', title: 'Folder', url: null });
  port.onMessage.fire({ op: 'removeTree', id: 'e', bookmarkId: '99' });
  await flush();
  assert.deepEqual(calls.map(x => x[0]), ['create', 'update', 'remove', 'create', 'removeTree']);
  assert.equal(Object.hasOwn(calls[3][1], 'url'), false);
  assert.ok(messages.some(x => x.type === 'result' && x.id === 'a' && x.ok));
  assert.ok(messages.some(x => x.type === 'result' && x.id === 'b' && x.ok));
  assert.ok(messages.some(x => x.type === 'result' && x.id === 'c' && x.ok));
  assert.ok(messages.some(x => x.type === 'result' && x.id === 'd' && x.ok));
  assert.ok(messages.some(x => x.type === 'result' && x.id === 'e' && x.ok));
});

test('recreates a missing reconnect alarm after worker startup and reconnects after disconnect', async () => {
  let alarm = null;
  const created = [], ports = [];
  const bookmarks = {
    getTree: async () => [{ id: '0', children: [{ id: '1', children: [] }] }],
    onCreated: event(), onRemoved: event(), onChanged: event(), onMoved: event(), onChildrenReordered: event(), onImportEnded: event(),
  };
  const chrome = {
    bookmarks,
    runtime: {
      connectNative: () => {
        const port = { onMessage: event(), onDisconnect: event(), postMessage() {} };
        ports.push(port); return port;
      },
      onStartup: event(), onInstalled: event(),
    },
    alarms: {
      get: async () => alarm,
      create: async (name, options) => { created.push([name, options.periodInMinutes]); alarm = { name }; },
      onAlarm: event(),
    },
  };
  runInNewContext(readFileSync(fileURLToPath(new URL('service-worker.js', base)), 'utf8'), { chrome, setTimeout, clearTimeout, Promise });
  await flush();
  assert.deepEqual(created, [['reconnect', 0.5]]);
  assert.equal(ports.length, 1);
  ports[0].onDisconnect.fire();
  chrome.alarms.onAlarm.fire({ name: 'other' });
  assert.equal(ports.length, 1);
  chrome.alarms.onAlarm.fire({ name: 'reconnect' });
  assert.equal(ports.length, 2);
  alarm = null; // Simulate a browser restart clearing the scheduled alarm.
  chrome.runtime.onStartup.fire();
  await flush();
  assert.deepEqual(created, [['reconnect', 0.5], ['reconnect', 0.5]]);
  assert.equal(ports.length, 2); // Do not duplicate an existing connection.
});

test('disconnect drops queued old commands and does not send an old snapshot on a new port', async () => {
  const ports = [], calls = [];
  let finishFirst, finishOldTree;
  let treeCalls = 0;
  const bookmarks = {
    getTree: () => {
      if (++treeCalls === 1) return new Promise(resolve => { finishOldTree = resolve; });
      return Promise.resolve([{ id: '0', children: [{ id: '1', children: [] }] }]);
    },
    create: data => {
      calls.push(data.title);
      if (data.title === 'in flight') return new Promise(resolve => { finishFirst = resolve; });
      return Promise.resolve({ id: 'new', ...data });
    },
    onCreated: event(), onRemoved: event(), onChanged: event(), onMoved: event(), onChildrenReordered: event(), onImportEnded: event(),
  };
  const chrome = {
    bookmarks,
    runtime: { connectNative: () => { const port = { onMessage: event(), onDisconnect: event(), messages: [], postMessage(msg) { this.messages.push(msg); } }; ports.push(port); return port; }, onStartup: event(), onInstalled: event() },
    alarms: { get: async () => ({ name: 'reconnect' }), create: async () => {}, onAlarm: event() },
  };
  runInNewContext(readFileSync(fileURLToPath(new URL('service-worker.js', base)), 'utf8'), { chrome, setTimeout, clearTimeout, Promise });
  ports[0].onMessage.fire({ op: 'create', id: 'a', parentId: '1', title: 'in flight' });
  ports[0].onMessage.fire({ op: 'create', id: 'b', parentId: '1', title: 'queued old' });
  await flush();
  assert.deepEqual(calls, ['in flight']);
  ports[0].onDisconnect.fire();
  chrome.alarms.onAlarm.fire({ name: 'reconnect' });
  await flush();
  finishOldTree([{ id: 'stale' }]);
  finishFirst({ id: 'old' });
  await flush();
  assert.deepEqual(calls, ['in flight']);
  assert.ok(!ports[1].messages.some(x => x.type === 'snapshot' && x.tree[0].id === 'stale'));
  ports[1].onMessage.fire({ op: 'create', id: 'c', parentId: '1', title: 'new connection' });
  await flush();
  assert.deepEqual(calls, ['in flight', 'new connection']);
  assert.ok(ports[1].messages.some(x => x.type === 'result' && x.id === 'c' && x.ok));
});
