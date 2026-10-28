// The Edge profile that owns this extension is the authority for its bookmarks.
// No page, network or browsing-data permissions are requested.
const HOST = 'com.agbrowser.edge_favorites';
let port = null;
let timer = null;
let chain = Promise.resolve();
let snapshotSequence = 0;

// Native messaging identifies the extension, not the Edge profile that owns
// it. Keep an anonymous, profile-local ID without requesting account access or
// an additional extension permission. If storage is unavailable the bridge
// can still offer a session-only connection, but must not persist a choice.
function loadProfileIdentity() {
  if (!globalThis.indexedDB || !globalThis.crypto?.randomUUID) return Promise.resolve(null);
  return new Promise(resolve => {
    let settled = false, db;
    const finish = value => { if (settled) return; settled = true; clearTimeout(timeout); db?.close(); resolve(value); };
    const timeout = setTimeout(() => finish(null), 3000);
    try {
      const request = indexedDB.open('agbrowser-edge-profile', 1);
      request.onupgradeneeded = () => request.result.createObjectStore('identity');
      request.onerror = () => finish(null);
      request.onblocked = () => finish(null);
      request.onsuccess = () => {
        db = request.result;
        if (settled) { db.close(); return; }
        try {
          const tx = db.transaction('identity', 'readwrite');
          const store = tx.objectStore('identity');
          const read = store.get('profileId');
          let id = null;
          read.onsuccess = () => {
            id = typeof read.result === 'string' && /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(read.result) ? read.result : crypto.randomUUID();
            if (id !== read.result) store.put(id, 'profileId');
          };
          tx.oncomplete = () => finish(id);
          tx.onerror = tx.onabort = () => finish(null);
        } catch { finish(null); }
      };
    } catch { finish(null); }
  });
}
const profileIdentity = loadProfileIdentity();

// Alarms can be cleared when the browser restarts. Re-establish the retry
// alarm whenever the worker starts, not just when the extension is installed.
async function ensureReconnectAlarm() {
  try {
    if (!await chrome.alarms.get('reconnect'))
      await chrome.alarms.create('reconnect', { periodInMinutes: 0.5 });
  } catch { /* A later startup can retry; connecting now still works. */ }
}

async function sendSnapshot() {
  if (!port) return;
  const current = port;
  const sequence = ++snapshotSequence;
  try {
    const [tree, profileId] = await Promise.all([chrome.bookmarks.getTree(), profileIdentity]);
    if (port === current) current.postMessage({ type: 'snapshot', sequence, tree, profileId });
  }
  catch { /* A disconnect is retried by the alarm. */ }
}
function changed() {
  clearTimeout(timer);
  timer = setTimeout(() => { void sendSnapshot(); }, 150);
}
function connect() {
  if (port) return;
  try {
    const current = chrome.runtime.connectNative(HOST);
    port = current;
    current.onMessage.addListener(message => {
      chain = chain.then(async () => {
        // Never execute queued commands from a connection that has ended.
        // A command already handed to Edge may still complete after disconnect.
        if (port !== current) return;
        try {
          let result;
          if (message.op === 'create') { const details = { parentId: message.parentId, title: message.title }; if (typeof message.url === 'string') details.url = message.url; result = await chrome.bookmarks.create(details); }
          else if (message.op === 'update') { const changes = { title: message.title }; if (typeof message.url === 'string') changes.url = message.url; result = await chrome.bookmarks.update(message.bookmarkId, changes); }
          else if (message.op === 'remove') { await chrome.bookmarks.remove(message.bookmarkId); result = true; }
          else if (message.op === 'removeTree') { await chrome.bookmarks.removeTree(message.bookmarkId); result = true; }
          else throw new Error('Unsupported bookmark operation');
          current.postMessage({ type: 'result', id: message.id, ok: true, result });
          await sendSnapshot();
        } catch (e) { try { current.postMessage({ type: 'result', id: message.id, ok: false, error: e.message }); } catch {} }
      });
    });
    current.onDisconnect.addListener(() => { if (port === current) port = null; void chrome.runtime.lastError; });
    void sendSnapshot();
  } catch { port = null; }
}
chrome.bookmarks.onCreated.addListener(changed);
chrome.bookmarks.onRemoved.addListener(changed);
chrome.bookmarks.onChanged.addListener(changed);
chrome.bookmarks.onMoved.addListener(changed);
chrome.bookmarks.onChildrenReordered.addListener(changed);
chrome.bookmarks.onImportEnded.addListener(changed);
chrome.runtime.onStartup.addListener(() => { void ensureReconnectAlarm(); connect(); });
chrome.runtime.onInstalled.addListener(() => { void ensureReconnectAlarm(); connect(); });
chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === 'reconnect') connect(); });
void ensureReconnectAlarm();
connect();
