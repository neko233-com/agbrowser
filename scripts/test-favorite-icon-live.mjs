// Exercise the packaged EXE against loopback-only data in a disposable profile.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createHash } from 'node:crypto';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { deflateSync } from 'node:zlib';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

if (process.platform !== 'win32') throw new Error('This fixture requires Windows and WebView2 Runtime');
const executable = resolve(process.env.AGB_DESKTOP_EXE ?? 'build/desktop/AgBrowser.Desktop.exe');
if (!existsSync(executable)) throw new Error('Build the desktop EXE first');
const directory = await mkdtemp(join(tmpdir(), 'agb-favicon-live-'));
let child, iconRequests = 0;
function crc32(data) {
  let crc = 0xffffffff;
  for (const byte of data) { crc ^= byte; for (let i = 0; i < 8; i++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1)); }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const name = Buffer.from(type), length = Buffer.alloc(4), checksum = Buffer.alloc(4);
  length.writeUInt32BE(data.length); checksum.writeUInt32BE(crc32(Buffer.concat([name, data])));
  return Buffer.concat([length, name, data, checksum]);
}
function favicon() {
  const header = Buffer.alloc(13); header.writeUInt32BE(16, 0); header.writeUInt32BE(16, 4); header[8] = 8; header[9] = 6;
  const rows = Buffer.alloc((16 * 4 + 1) * 16);
  for (let y = 0; y < 16; y++) for (let x = 0; x < 16; x++) {
    const i = y * 65 + 1 + x * 4; rows[i] = 240; rows[i + 1] = 20; rows[i + 2] = 20; rows[i + 3] = 255;
  }
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header), chunk('IDAT', deflateSync(rows)), chunk('IEND', Buffer.alloc(0))]);
}
const png = favicon();
const server = createServer((request, response) => {
  if (request.url === '/red.png') { iconRequests++; response.writeHead(200, { 'content-type': 'image/png' }); response.end(png); }
  else if (request.url === '/page') { response.writeHead(200, { 'content-type': 'text/html; charset=utf-8' }); response.end('<!doctype html><title>Icon fixture</title><link rel="icon" href="/red.png"><p>Local fixture</p>'); }
  else response.writeHead(404).end();
});
server.on('connect', (_request, socket) => socket.destroy());
async function waitFor(probe, label) {
  const deadline = Date.now() + 25000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) throw new Error('Browser exited before ' + label);
    const found = await probe(); if (found) return found;
    await new Promise(resolveWait => setTimeout(resolveWait, 100));
  }
  throw new Error('Timed out: ' + label);
}
try {
  server.listen(0, '127.0.0.1'); await once(server, 'listening');
  const port = server.address().port, url = `http://127.0.0.1:${port}/page`;
  const edge = join(directory, 'edge'), profile = join(edge, 'Default'); await mkdir(profile, { recursive: true });
  await writeFile(join(edge, 'Local State'), JSON.stringify({ profile: { last_used: 'Default' } }));
  await writeFile(join(profile, 'Bookmarks'), JSON.stringify({ roots: {
    bookmark_bar: { id: '1', type: 'folder', children: [{ id: '42', name: 'Icon fixture', type: 'url', url }] },
    other: { id: '2', type: 'folder', children: [] },
  } }));
  await writeFile(join(directory, 'browser-network.json'), JSON.stringify({ AgentDebug: true }));
  await writeFile(join(directory, 'update-network.json'), JSON.stringify({ ProxyUrl: `http://127.0.0.1:${port}` }));
  const environment = { ...process.env, AGB_DESKTOP_DATA_DIR: directory, AGB_EDGE_DATA_DIR: edge, WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--no-proxy-server' };
  delete environment.WEBVIEW2_USER_DATA_FOLDER; delete environment.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER;
  child = spawn(executable, [url], { env: environment, windowsHide: true, stdio: 'ignore' });
  const session = await waitFor(async () => {
    try {
      const value = JSON.parse(await readFile(join(directory, 'session.json'), 'utf8'));
      const index = value.urls?.indexOf(url) ?? -1;
      return index >= 0 && value.icons?.[index] ? value : null;
    } catch { return null; }
  }, 'page favicon and session checkpoint');
  const index = session.urls.indexOf(url), saved = Buffer.from(session.icons[index], 'base64');
  if (!saved.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10])) || iconRequests < 1)
    throw new Error('The local page favicon was not saved');
  console.log('PASS hidden packaged EXE receives a local page favicon and saves its tab icon');
  const cached = await waitFor(async () => {
    try {
      const text = await readFile(join(directory, 'favorite-page-icons.json'), 'utf8');
      if (text.includes(url)) throw new Error('Favorite icon cache stores the page URL in plain text');
      const value = JSON.parse(text), hash = createHash('sha256').update(url, 'utf8').digest('hex');
      return value.version === 1 ? value.entries?.find(item => item.hash === hash && item.icon) : null;
    } catch (error) {
      if (error.code === 'ENOENT' || error instanceof SyntaxError) return false;
      throw error;
    }
  }, 'saved favorite favicon');
  const icon = Buffer.from(cached.icon, 'base64');
  if (!icon.subarray(0, 8).equals(Buffer.from([137,80,78,71,13,10,26,10]))) throw new Error('Saved favorite icon is not PNG');
  console.log('PASS the visited favorite favicon is saved in the bounded local cache');
} finally {
  if (child) {
    child.kill();
    if (child.exitCode === null && child.signalCode === null) await Promise.race([once(child, 'exit'), new Promise(resolveWait => setTimeout(resolveWait, 5000))]);
  }
  server.close();
  if (child && child.exitCode === null && child.signalCode === null) {
    console.error('Fixture browser did not exit; temporary data retained:', directory); process.exitCode = 1;
  } else await cleanupTemporaryFixture(directory, 'agb-favicon-live-');
}
