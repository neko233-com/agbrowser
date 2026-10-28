// Exercise the actual desktop EXE with disposable profiles and loopback-only
// servers. Never send fixture requests through the user's system proxy.
import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { existsSync } from 'node:fs';
import { mkdir, mkdtemp, readFile, writeFile } from 'node:fs/promises';
import http from 'node:http';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

if (process.platform !== 'win32') throw new Error('This check requires Windows and WebView2 Runtime');
const executable = resolve(process.env.AGB_DESKTOP_EXE ?? 'packages/desktop/bin/Release/net10.0-windows/AgBrowser.Desktop.exe');
if (!existsSync(executable)) throw new Error(`Build the desktop EXE first: ${executable}`);
const root = await mkdtemp(join(tmpdir(), 'agb-network-live-'));
let childrenStopped = true;
const requests = [];
const origin = http.createServer((request, response) => {
  requests.push({ url: request.url, viaProxy: request.headers['x-agbrowser-fixture-proxy'] === 'yes' });
  response.writeHead(200, { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' });
  response.end('<!doctype html><title>agbrowser network fixture</title><main id="ready">local fixture ready</main>');
});
origin.listen(0, '127.0.0.1'); await once(origin, 'listening');
const originPort = origin.address().port;
const proxy = http.createServer((request, response) => {
  let target;
  try { target = new URL(request.url); } catch { response.writeHead(400).end(); return; }
  if (target.protocol !== 'http:' || target.hostname !== '127.0.0.1' || Number(target.port) !== originPort) {
    response.writeHead(502).end(); return;
  }
  const forwarded = http.request({ hostname: '127.0.0.1', port: originPort, path: target.pathname + target.search,
    method: request.method, headers: { ...request.headers, 'x-agbrowser-fixture-proxy': 'yes' } }, upstream => {
    response.writeHead(upstream.statusCode, upstream.headers); upstream.pipe(response);
  });
  forwarded.on('error', () => { if (!response.headersSent) response.writeHead(502); response.end(); });
  request.pipe(forwarded);
});
proxy.on('connect', (_request, socket) => socket.destroy());
proxy.listen(0, '127.0.0.1'); await once(proxy, 'listening');
const proxyPort = proxy.address().port;

async function waitFor(probe, description, child) {
  const deadline = Date.now() + 20000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null || child.signalCode !== null) throw new Error(`${description}: browser exited early`);
    const result = await probe();
    if (result) return result;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Timed out: ${description}`);
}

async function evaluate(url, expression) {
  const socket = new WebSocket(url);
  try {
    return await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('CDP evaluation timed out')), 10000);
      socket.addEventListener('error', () => { clearTimeout(timer); reject(new Error('CDP WebSocket failed')); }, { once: true });
      socket.addEventListener('open', () => socket.send(JSON.stringify({ id: 1, method: 'Runtime.evaluate', params: { expression, returnByValue: true } })), { once: true });
      socket.addEventListener('message', event => {
        const reply = JSON.parse(String(event.data));
        if (reply.id !== 1) return;
        clearTimeout(timer);
        if (reply.error || reply.result?.exceptionDetails) reject(new Error('CDP evaluation returned an error'));
        else resolve(reply.result.result.value);
      });
    });
  } finally { socket.close(); }
}

async function runScenario(name, captureLoopback, agentDebug, profileName = name, blank = false) {
  const dataDir = join(root, profileName); await mkdir(dataDir, { recursive: true });
  await writeFile(join(dataDir, 'browser-network.json'), JSON.stringify({ ProxyUrl: `http://127.0.0.1:${proxyPort}`, CaptureLoopback: captureLoopback, AgentDebug: agentDebug }));
  const environment = { ...process.env, AGB_DESKTOP_DATA_DIR: dataDir, AGB_EDGE_DATA_DIR: join(dataDir, 'empty-edge') };
  delete environment.WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS;
  delete environment.WEBVIEW2_USER_DATA_FOLDER;
  delete environment.WEBVIEW2_BROWSER_EXECUTABLE_FOLDER;
  const url = blank ? 'about:blank' : `http://127.0.0.1:${originPort}/${name}`;
  const child = spawn(executable, [url], { env: environment, windowsHide: true, stdio: 'ignore' });
  try {
    if (!blank) {
      const observed = await waitFor(() => requests.find(item => item.url === `/${name}`), `${name} fixture navigation`, child);
      if (observed.viaProxy !== captureLoopback) throw new Error(`${name}: loopback routing did not match its setting`);
      console.log(`PASS loopback navigation ${captureLoopback ? 'passes through' : 'bypasses'} the configured proxy`);
    }
    const portFile = join(dataDir, 'profile', 'EBWebView', 'DevToolsActivePort');
    if (agentDebug) {
      const port = await waitFor(async () => {
        try { return Number((await readFile(portFile, 'utf8')).split(/\r?\n/, 1)[0]) || null; } catch { return null; }
      }, 'debug endpoint discovery', child);
      const page = await waitFor(async () => {
        try { return (await (await fetch(`http://127.0.0.1:${port}/json/list`)).json()).find(item => item.url === url && item.webSocketDebuggerUrl); }
        catch { return null; }
      }, 'debug target', child);
      const expression = blank ? 'location.href' : "document.querySelector('#ready')?.textContent";
      const expected = blank ? 'about:blank' : 'local fixture ready';
      if (await evaluate(page.webSocketDebuggerUrl, expression) !== expected) throw new Error('CDP did not read the expected page');
      console.log(`PASS actual desktop DevTools endpoint reads the ${blank ? 'new tab' : 'loaded page'} through CDP`);
    } else {
      await new Promise(resolve => setTimeout(resolve, 500));
      if (existsSync(portFile)) throw new Error(`${profileName}: debug-disabled profile retains DevToolsActivePort`);
      console.log(`PASS debug-disabled ${profileName} profile has no stale DevToolsActivePort`);
    }
  } finally {
    child.kill();
    if (child.exitCode === null && child.signalCode === null) await Promise.race([once(child, 'exit'), new Promise(resolve => setTimeout(resolve, 5000))]);
    if (child.exitCode === null && child.signalCode === null) { childrenStopped = false; throw new Error(`Fixture browser did not exit: ${child.pid}`); }
  }
}

try {
  await runScenario('capture', true, true, 'reused-profile');
  await new Promise(resolve => setTimeout(resolve, 1000));
  await runScenario('capture-restart', true, true, 'reused-profile');
  await new Promise(resolve => setTimeout(resolve, 1000));
  await runScenario('disabled-after-enabled', false, false, 'reused-profile');
  await runScenario('direct', false, false);
  await runScenario('blank-debug', false, true, 'blank-debug', true);
} finally {
  origin.close(); proxy.close();
  if (childrenStopped) await cleanupTemporaryFixture(root, 'agb-network-live-');
  else console.error(`Fixture browser is still running; temporary data retained: ${root}`);
}
