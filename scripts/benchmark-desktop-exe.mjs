// Measure the actual self-contained desktop EXE from spawn to a local page's
// DOM-ready marker. Fresh profiles are isolated; OS/browser files may be warm.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, readdir, writeFile } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { performance } from 'node:perf_hooks';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

if (process.platform !== 'win32') throw new Error('This measurement requires Windows and WebView2 Runtime');
const executable = resolve(process.env.AGB_DESKTOP_EXE ?? 'build/desktop/AgBrowser.Desktop.exe');
if (!existsSync(executable)) throw new Error(`Build the desktop EXE first: ${executable}`);
const root = await mkdtemp(join(tmpdir(), 'agb-exe-startup-'));
let childrenStopped = true;
const server = http.createServer((_request, response) => {
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
  response.end('<!doctype html><title>loading</title><h1 id="ready">Local startup fixture ready</h1><script>document.title="AGB startup fixture ready"</script>');
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const base = `http://127.0.0.1:${server.address().port}/`;
async function waitFor(probe, child, label) {
  const deadline = performance.now() + 25000;
  while (performance.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) throw new Error(`Browser exited before ${label}`);
    const result = await probe(); if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 40));
  }
  throw new Error(`Timed out: ${label}`);
}
async function sample(name, extractionDir) {
  const dataDir = join(root, name); await mkdir(dataDir);
  await mkdir(extractionDir, { recursive: true });
  await writeFile(join(dataDir, 'browser-network.json'), JSON.stringify({ AgentDebug: true }));
  const env = { ...process.env, AGB_DESKTOP_DATA_DIR: dataDir, AGB_EDGE_DATA_DIR: join(dataDir, 'empty-edge'), DOTNET_BUNDLE_EXTRACT_BASE_DIR: extractionDir };
  delete env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS;
  delete env.WEBVIEW2_USER_DATA_FOLDER;
  delete env.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER;
  const url = `${base}?sample=${name}`;
  const started = performance.now();
  const child = spawn(executable, [url], { env, windowsHide: true, stdio: 'ignore' });
  try {
    const portFile = join(dataDir, 'profile', 'EBWebView', 'DevToolsActivePort');
    const port = await waitFor(async () => { try { return Number((await readFile(portFile, 'utf8')).split(/\r?\n/, 1)[0]) || null; } catch { return null; } }, child, 'DevTools endpoint');
    await waitFor(async () => {
      try {
        const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
        return targets.some(target => target.url === url && target.title === 'AGB startup fixture ready');
      } catch { return false; }
    }, child, 'local DOM marker');
    const milliseconds = Math.round(performance.now() - started);
    const extracted = await readdir(extractionDir, { recursive: true, withFileTypes: true });
    if (!extracted.some(item => item.isFile())) throw new Error('Expected native files in the specified bundle extraction directory');
    return milliseconds;
  } finally {
    child.kill();
    if (child.exitCode === null && child.signalCode === null) await Promise.race([once(child, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (child.exitCode === null && child.signalCode === null) { childrenStopped = false; throw new Error(`Fixture browser did not exit: ${child.pid}`); }
    await new Promise(resolve => setTimeout(resolve, 350));
  }
}
function summary(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return { samplesMs: values, medianMs: sorted[Math.floor(sorted.length / 2)], rangeMs: [sorted[0], sorted.at(-1)] };
}
try {
  const fresh = [], reused = [];
  for (let i = 0; i < 3; i++) fresh.push(await sample(`fresh-${i}`, join(root, `extract-fresh-${i}`)));
  const cache = join(root, 'extract-reused');
  await sample('warmup', cache);
  for (let i = 0; i < 3; i++) reused.push(await sample(`reused-${i}`, cache));
  console.log(JSON.stringify({ freshBundleExtraction: summary(fresh), reusedBundleExtraction: summary(reused),
    scope: 'actual EXE, isolated fresh WebView2 profile, Agent debug enabled, local DOM marker; OS caches may be warm, not a cold-machine or installed-MSI measurement' }, null, 2));
} finally {
  server.close();
  if (childrenStopped) await cleanupTemporaryFixture(root, 'agb-exe-startup-');
  else console.error(`Fixture browser is still running; temporary data retained: ${root}`);
}
