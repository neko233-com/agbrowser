// Measure an actual desktop EXE with an isolated profile and local page.
// Private bytes are summed across this host's WebView2 process tree; this is
// not system-wide RAM and says nothing about real sites or cold disk caches.
import { spawn, execFileSync } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdtemp, readFile, writeFile } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

if (process.platform !== 'win32') throw new Error('This measurement requires Windows and WebView2 Runtime');
const executable = resolve(process.env.AGB_DESKTOP_EXE ?? 'build/desktop/AgBrowser.Desktop.exe');
if (!existsSync(executable)) throw new Error(`Build the desktop EXE first: ${executable}`);
const root = await mkdtemp(join(tmpdir(), 'agb-memory-'));
await writeFile(join(root, 'browser-network.json'), JSON.stringify({ AgentDebug: true }));
const env = { ...process.env, AGB_DESKTOP_DATA_DIR: root, AGB_EDGE_DATA_DIR: join(root, 'empty-edge') };
delete env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS;
delete env.WEBVIEW2_USER_DATA_FOLDER;
delete env.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER;
const server = http.createServer((_request, response) => {
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
  response.end('<!doctype html><title>agbrowser memory fixture</title><h1>Local memory fixture</h1>');
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const url = `http://127.0.0.1:${server.address().port}/fixture`;
const host = spawn(executable, [url], { env, windowsHide: true, stdio: 'ignore' });
async function waitFor(probe, label) {
  const deadline = Date.now() + 25000;
  while (Date.now() < deadline) {
    if (host.exitCode !== null || host.signalCode !== null) throw new Error(`Browser exited before ${label}`);
    const result = await probe(); if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out: ${label}`);
}
const powershell = `
$rows = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId, Name, PrivatePageCount, WorkingSetSize)
$ids = [System.Collections.Generic.HashSet[uint32]]::new()
[void]$ids.Add([uint32]$env:AGB_MEASURE_ROOT_PID)
do {
  $changed = $false
  foreach ($row in $rows) {
    if ($ids.Contains([uint32]$row.ParentProcessId) -and $ids.Add([uint32]$row.ProcessId)) { $changed = $true }
  }
} while ($changed)
@($rows | Where-Object { $ids.Contains([uint32]$_.ProcessId) -and ($_.Name -eq 'AgBrowser.Desktop.exe' -or $_.Name -eq 'msedgewebview2.exe') }) | ConvertTo-Json -Depth 3 -Compress
`;
function memory() {
  const result = execFileSync('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', powershell], {
    env: { ...process.env, AGB_MEASURE_ROOT_PID: String(host.pid) }, encoding: 'utf8', timeout: 20000,
  });
  let rows = JSON.parse(result); if (!Array.isArray(rows)) rows = [rows];
  if (!rows.some(row => row.Name === 'msedgewebview2.exe')) throw new Error('No WebView2 processes found under the isolated host');
  return {
    processCount: rows.length,
    privateMiB: Math.round(rows.reduce((sum, row) => sum + Number(row.PrivatePageCount || 0), 0) / 1048576),
    workingSetMiB: Math.round(rows.reduce((sum, row) => sum + Number(row.WorkingSetSize || 0), 0) / 1048576),
  };
}
try {
  const portPath = join(root, 'profile', 'EBWebView', 'DevToolsActivePort');
  const port = await waitFor(async () => { try { return Number((await readFile(portPath, 'utf8')).split(/\r?\n/, 1)[0]) || null; } catch { return null; } }, 'DevTools port');
  const targets = async () => { try { return await (await fetch(`http://127.0.0.1:${port}/json/list`)).json(); } catch { return []; } };
  await waitFor(async () => (await targets()).some(target => target.url === url && target.title === 'agbrowser memory fixture'), 'local fixture');
  await new Promise(resolve => setTimeout(resolve, 1500));
  const one = memory();
  const forward = spawn(executable, Array(10).fill('about:blank'), { env, windowsHide: true, stdio: 'ignore' });
  const [code] = await once(forward, 'exit'); if (code !== 0) throw new Error('Forwarding blank tabs failed');
  await waitFor(async () => {
    try { const session = JSON.parse(await readFile(join(root, 'session.json'), 'utf8')); return session.urls?.filter(item => item === 'about:blank').length >= 10; }
    catch { return false; }
  }, 'ten blank tabs recorded in the session');
  await new Promise(resolve => setTimeout(resolve, 1500));
  const eleven = memory();
  const initializedBlankTargets = (await targets()).filter(target => target.url === 'about:blank').length;
  console.log(JSON.stringify({ onePage: one, onePageAndTenBlankTabs: eleven,
    addedPrivateMiB: eleven.privateMiB - one.privateMiB, initializedBlankTargets,
    scope: 'isolated profile, local page, actual EXE, debugging enabled; summed process private bytes, OS cache may be warm' }, null, 2));
} finally {
  host.kill();
  if (host.exitCode === null && host.signalCode === null) await Promise.race([once(host, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
  server.close();
  if (host.exitCode === null && host.signalCode === null) {
    console.error(`Fixture browser did not exit; temporary data retained: ${root}`);
    process.exitCode = 1;
  } else await cleanupTemporaryFixture(root, 'agb-memory-');
}
