// Check session restoration in the actual packaged EXE using an isolated
// profile and a loopback-only page. No user browser data is read or changed.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, stat, writeFile } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

if (process.platform !== 'win32') throw new Error('This check requires Windows and WebView2 Runtime');
const executable = resolve(process.env.AGB_DESKTOP_EXE ?? 'build/desktop/AgBrowser.Desktop.exe');
if (!existsSync(executable)) throw new Error(`Build the desktop EXE first: ${executable}`);
const directory = await mkdtemp(join(tmpdir(), 'agb-restore-live-'));
const server = http.createServer((_request, response) => {
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
  response.end('<!doctype html><title>agbrowser restore fixture</title><p>Local restore fixture</p>');
});
server.listen(0, '127.0.0.1');
await once(server, 'listening');
const count = 400, selected = 199;
const urls = Array.from({ length: count }, (_, index) => `http://127.0.0.1:${server.address().port}/?tab=${index}`);
const inactiveIcon = 'iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO8u7Z8AAAAASUVORK5CYII=';
const sessionFile = join(directory, 'session.json');
await writeFile(sessionFile, JSON.stringify({ version: 1, urls, activeIndex: selected, titles: urls.map((_, index) => `Fixture ${index}`), icons: urls.map((_, index) => index === selected - 1 ? inactiveIcon : null) }));
await writeFile(join(directory, 'browser-network.json'), JSON.stringify({ AgentDebug: true }));
const initialTimestamp = (await stat(sessionFile)).mtimeMs;
const environment = { ...process.env, AGB_DESKTOP_DATA_DIR: directory, AGB_EDGE_DATA_DIR: join(directory, 'empty-edge'), WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS: '--no-proxy-server' };
delete environment.WEBVIEW2_USER_DATA_FOLDER;
delete environment.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER;
const child = spawn(executable, [], { env: environment, windowsHide: true, stdio: 'ignore' });

async function waitFor(probe, label) {
  const deadline = Date.now() + 25000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) throw new Error(`Browser exited before ${label}`);
    const value = await probe();
    if (value) return value;
    await new Promise(resolve => setTimeout(resolve, 80));
  }
  throw new Error(`Timed out: ${label}`);
}

try {
  const portFile = join(directory, 'profile', 'EBWebView', 'DevToolsActivePort');
  const port = await waitFor(async () => {
    try { return Number((await readFile(portFile, 'utf8')).split(/\r?\n/, 1)[0]) || null; } catch { return null; }
  }, 'DevTools endpoint');
  const pages = await waitFor(async () => {
    try {
      const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
      return targets.some(target => target.url === urls[selected] && target.title === 'agbrowser restore fixture') ? targets : null;
    } catch { return null; }
  }, 'selected local page');
  if (pages.filter(target => target.type === 'page' && urls.includes(target.url)).length !== 1)
    throw new Error('Unexpected additional restored page targets');
  await waitFor(async () => {
    try { return (await stat(sessionFile)).mtimeMs > initialTimestamp; } catch { return false; }
  }, 'restored session checkpoint');
  const session = JSON.parse(await readFile(sessionFile, 'utf8'));
  if (session.activeIndex !== selected || session.urls.length !== count || session.urls.some((url, index) => url !== urls[index]) ||
      session.icons?.[selected - 1] !== inactiveIcon)
    throw new Error('Saved session did not preserve the selected tab and tab order');
  console.log(`PASS actual hidden EXE: ${count} restored tabs keep index ${selected}, load only the selected local page, and preserve order and an inactive tab icon`);
} finally {
  child.kill();
  if (child.exitCode === null && child.signalCode === null) await Promise.race([once(child, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
  server.close();
  if (child.exitCode === null && child.signalCode === null) {
    console.error(`Fixture browser did not exit; temporary data retained: ${directory}`);
    process.exitCode = 1;
  } else await cleanupTemporaryFixture(directory, 'agb-restore-live-');
}
