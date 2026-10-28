// Measures a fresh test-harness process using local data, not the installed MSI.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readdir, rm, writeFile } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { basename, join, relative, resolve, sep } from 'node:path';
import { performance } from 'node:perf_hooks';
import { deflateSync } from 'node:zlib';

const executable = resolve(process.env.AGB_BENCH_EXE ?? 'packages/desktop.tests/bin/Release/net10.0-windows/AgBrowser.Desktop.Tests.exe');
const packaging = process.env.AGB_BENCH_PACKAGING ?? 'framework-dependent';
if (!['framework-dependent', 'single-file', 'folder'].includes(packaging)) throw new Error('Unsupported benchmark packaging mode');
if (!existsSync(executable)) throw new Error('Build the Release desktop test project first');

function crc32(data) {
  let crc = 0xffffffff;
  for (const byte of data) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ (0xedb88320 & -(crc & 1));
  }
  return (crc ^ 0xffffffff) >>> 0;
}
function chunk(type, data) {
  const name = Buffer.from(type), length = Buffer.alloc(4), checksum = Buffer.alloc(4);
  length.writeUInt32BE(data.length); checksum.writeUInt32BE(crc32(Buffer.concat([name, data])));
  return Buffer.concat([length, name, data, checksum]);
}
function favicon() {
  const header = Buffer.alloc(13); header.writeUInt32BE(20, 0); header.writeUInt32BE(20, 4); header[8] = 8; header[9] = 6;
  const rows = Buffer.alloc((20 * 4 + 1) * 20);
  for (let y = 0; y < 20; y++) for (let x = 0; x < 20; x++) {
    const i = y * 81 + 1 + x * 4; rows[i] = 40; rows[i + 1] = 105; rows[i + 2] = 205; rows[i + 3] = 255;
  }
  return Buffer.concat([Buffer.from([137,80,78,71,13,10,26,10]), chunk('IHDR', header), chunk('IDAT', deflateSync(rows)), chunk('IEND', Buffer.alloc(0))]).toString('base64');
}
const cachedIcon = favicon();
const server = createServer((_request, response) => {
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
  response.end('<!doctype html><title>Startup fixture</title><h1>Startup fixture ready</h1>');
});
server.listen(0, '127.0.0.1'); await once(server, 'listening');
const port = server.address().port;
const runRoot = await mkdtemp(join(tmpdir(), 'agb-startup-'));
const samples = [];
try {
  const scenarios = [{ tabs: 1, selected: 0 }, { tabs: 100, selected: 0 }, { tabs: 100, selected: 99 }, { tabs: 100, selected: 0, external: true }];
  for (const { tabs, selected, external = false } of scenarios) {
    for (let sample = 0; sample < 3; sample++) {
      const dataDir = join(runRoot, `${tabs}-${selected}-${external}-${sample}`);
      await mkdir(dataDir, { recursive: true });
      const urls = Array.from({ length: tabs }, (_, i) => `http://127.0.0.1:${port}/?tab=${i}`);
      await writeFile(join(dataDir, 'session.json'), JSON.stringify({ version: 1, urls, activeIndex: selected, titles: urls.map(() => 'Startup fixture'), icons: urls.map(() => cachedIcon) }));
      const env = { ...process.env, AGB_STARTUP_BENCH_CHILD: '1', AGB_STARTUP_BENCH_TABS: String(tabs + (external ? 1 : 0)), AGB_STARTUP_BENCH_SELECTED: String(selected), AGB_DESKTOP_DATA_DIR: dataDir, AGB_EDGE_DATA_DIR: join(dataDir, 'no-edge-profile') };
      if (external) env.AGB_STARTUP_BENCH_EXTERNAL = `http://127.0.0.1:${port}/?tab=external`;
      else delete env.AGB_STARTUP_BENCH_EXTERNAL;
      delete env.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS;
      delete env.WEBVIEW2_USER_DATA_FOLDER;
      delete env.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER;
      delete env.DOTNET_BUNDLE_EXTRACT_BASE_DIR;
      if (packaging === 'single-file') {
        env.DOTNET_BUNDLE_EXTRACT_BASE_DIR = join(dataDir, 'bundle-extraction');
        await mkdir(env.DOTNET_BUNDLE_EXTRACT_BASE_DIR);
      }
      const start = performance.now();
      const child = spawn(executable, [], { env, stdio: ['ignore', 'pipe', 'pipe'], windowsHide: true });
      let output = '', errors = '', readyMs;
      child.stdout.on('data', chunk => { output += chunk; if (readyMs === undefined && output.includes('AGB_BENCH_READY')) readyMs = performance.now() - start; });
      child.stderr.on('data', chunk => { errors += chunk; });
      let timedOut = false;
      const timer = setTimeout(() => { timedOut = true; child.kill(); }, 30000);
      let code;
      try { [code] = await once(child, 'close'); } finally { clearTimeout(timer); }
      if (timedOut || code !== 0 || readyMs === undefined) throw new Error(`startup fixture failed (tabs=${tabs}, code=${code}, timeout=${timedOut}): ${errors}`);
      if (packaging === 'single-file') {
        const extracted = await readdir(env.DOTNET_BUNDLE_EXTRACT_BASE_DIR, { recursive: true, withFileTypes: true });
        if (!extracted.some(item => item.isFile())) throw new Error('Single-file sample did not extract native files into its fresh directory');
      }
      samples.push({ tabs, selected, external, milliseconds: Math.round(readyMs) });
      console.log(`tabs=${tabs} selected=${selected} external=${external} sample=${sample + 1}: ${Math.round(readyMs)} ms`);
    }
  }
  for (const { tabs, selected, external = false } of scenarios) {
    const values = samples.filter(x => x.tabs === tabs && x.selected === selected && x.external === external).map(x => x.milliseconds).sort((a, b) => a - b);
    console.log(`tabs=${tabs} selected=${selected} external=${external} median=${values[1]} ms range=${values[0]}–${values[2]} ms`);
  }
  console.log(`Scope: ${packaging} test-harness, fresh process + fresh local WebView2 profile to local DOM ready; ${packaging === 'single-file' ? 'native extraction verified in a fresh directory; ' : ''}OS caches may be warm. Not installed MSI startup.`);
} finally {
  server.close();
  const tempRoot = resolve(tmpdir()), target = resolve(runRoot), subpath = relative(tempRoot, target);
  if (subpath && !subpath.startsWith(`..${sep}`) && subpath !== '..' && basename(target).startsWith('agb-startup-')) {
    try { await rm(target, { recursive: true, force: true, maxRetries: 5, retryDelay: 200 }); }
    catch { console.error('Temporary benchmark profile could not be removed:', target); }
  }
}
