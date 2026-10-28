// Integration fixture: run the production service worker against an in-memory
// bookmarks API and the real agbrowser native host. No Edge profile or registry.
import { spawn } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { createInterface } from 'node:readline';
import { runInNewContext } from 'node:vm';

const [hostPath, firstUrl] = process.argv.slice(2);
if (!hostPath || !firstUrl) throw new Error('missing fixture arguments');
function event() {
  const handlers = [];
  return { addListener: fn => handlers.push(fn), fire: (...args) => handlers.forEach(fn => fn(...args)) };
}
const root = { id: '0', children: [
  { id: '1', title: 'Favorites bar', children: [{ id: 'edge-1', title: 'Connected Edge favorite', url: firstUrl }] },
  { id: '2', title: 'Other favorites', children: [] },
] };
let nextId = 1;
const events = { onCreated: event(), onRemoved: event(), onChanged: event(), onMoved: event(), onChildrenReordered: event(), onImportEnded: event() };
function find(id, node = root) {
  if (node.id === id) return node;
  for (const child of node.children ?? []) { const found = find(id, child); if (found) return found; }
  return null;
}
function parentOf(id, node = root) {
  if ((node.children ?? []).some(child => child.id === id)) return node;
  for (const child of node.children ?? []) { const found = parentOf(id, child); if (found) return found; }
  return null;
}
const bookmarks = {
  ...events,
  getTree: async () => [structuredClone(root)],
  create: async details => {
    const parent = find(details.parentId);
    if (!parent?.children) throw new Error('invalid parent');
    const node = { id: `created-${nextId++}`, title: details.title, ...(typeof details.url === 'string' ? { url: details.url } : { children: [] }) };
    parent.children.push(node); events.onCreated.fire(node.id, node); return structuredClone(node);
  },
  update: async (id, changes) => {
    const node = find(id); if (!node) throw new Error('missing bookmark');
    Object.assign(node, changes); events.onChanged.fire(id, changes); return structuredClone(node);
  },
  remove: async id => {
    const parent = parentOf(id); if (!parent) throw new Error('missing bookmark');
    parent.children.splice(parent.children.findIndex(child => child.id === id), 1); events.onRemoved.fire(id, {});
  },
  removeTree: async id => {
    const parent = parentOf(id); if (!parent) throw new Error('missing folder');
    parent.children.splice(parent.children.findIndex(child => child.id === id), 1); events.onRemoved.fire(id, {});
  },
};

const host = spawn(hostPath, ['chrome-extension://fjkcppekpigobilckiggnjdfeiiabmee/'], { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
host.stderr.on('data', data => process.stderr.write(data));
let buffered = Buffer.alloc(0);
const port = {
  onMessage: event(), onDisconnect: event(),
  postMessage(message) {
    const data = Buffer.from(JSON.stringify(message)); const header = Buffer.alloc(4); header.writeUInt32LE(data.length);
    host.stdin.write(Buffer.concat([header, data]));
  },
};
host.stdout.on('data', chunk => {
  buffered = Buffer.concat([buffered, chunk]);
  while (buffered.length >= 4 && buffered.length >= 4 + buffered.readUInt32LE(0)) {
    const length = buffered.readUInt32LE(0);
    const message = JSON.parse(buffered.subarray(4, 4 + length).toString('utf8'));
    buffered = buffered.subarray(4 + length); port.onMessage.fire(message);
  }
});
host.on('exit', () => port.onDisconnect.fire());
const chrome = {
  bookmarks,
  runtime: { connectNative: () => port, onStartup: event(), onInstalled: event() },
  alarms: { get: async () => ({ name: 'reconnect' }), create: async () => {}, onAlarm: event() },
};
runInNewContext(readFileSync(new URL('../service-worker.js', import.meta.url), 'utf8'), { chrome, setTimeout, clearTimeout, Promise });

const input = createInterface({ input: process.stdin });
input.on('line', async line => {
  try {
    const command = JSON.parse(line);
    if (command.op === 'edge-create') {
      root.children[0].children.push({ id: 'edge-2', title: command.title, url: command.url });
      events.onCreated.fire('edge-2', {});
    } else if (command.op === 'edge-update') await bookmarks.update('edge-2', { title: command.title });
    else if (command.op === 'edge-remove') await bookmarks.remove('edge-2');
    else if (command.op !== 'state') throw new Error('unknown fixture command');
    process.stdout.write(JSON.stringify({ ok: true, tree: root }) + '\n');
  } catch (error) { process.stdout.write(JSON.stringify({ ok: false, error: error.message }) + '\n'); }
});
input.on('close', () => host.stdin.end());
