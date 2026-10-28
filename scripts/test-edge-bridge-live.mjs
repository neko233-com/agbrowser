import { spawn, spawnSync } from 'node:child_process';
import { cpSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { resolve, join } from 'node:path';
import { tmpdir } from 'node:os';
import { randomBytes } from 'node:crypto';
import { EventEmitter } from 'node:events';
import { launchBrowser } from '../packages/core/src/browser/launcher.js';
import { CHANNELS, resolveChannelPaths } from '../packages/core/src/browser/channels.js';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

const root = mkdtempSync(join(tmpdir(), 'agb-edge-live-'));
const data = join(root, 'desktop-data'), extension = join(root, 'extension'), edgeData = join(root, 'edge-data');
for (const path of [root, data, edgeData]) mkdirSync(path, { recursive: true });
cpSync(resolve('packages/extensions/agb-edge-favorites'), extension, { recursive: true });
const hostName = 'com.agbrowser.verify_' + randomBytes(8).toString('hex');
const registry = 'HKCU\\Software\\Microsoft\\Edge\\NativeMessagingHosts\\' + hostName;
const extensionId = 'fjkcppekpigobilckiggnjdfeiiabmee';
const worker = join(extension, 'service-worker.js');
const original = readFileSync(worker, 'utf8');
if (!original.includes('com.agbrowser.edge_favorites')) throw new Error('Expected production host name not found');
writeFileSync(worker, original.replace('com.agbrowser.edge_favorites', hostName));
const hostManifest = join(root, 'native-host.json');
const nativeHostExe = resolve(process.env.AGB_EDGE_TEST_HOST ?? 'build/edge-bridge-host/AgBrowser.Desktop.exe');
writeFileSync(hostManifest, JSON.stringify({ name: hostName, description: 'agbrowser isolated bridge check', path: nativeHostExe, type: 'stdio', allowed_origins: [`chrome-extension://${extensionId}/`] }));
writeFileSync(join(root, 'registry-key.txt'), registry);
let session, secondSession, child, registered = false, launchWasUnsafe = false;
const oldData = process.env.AGB_DESKTOP_DATA_DIR;
const messages = [], signal = new EventEmitter();
let buffer = '', stderr = '';
function waitMessage(predicate, start = 0, timeout = 20000) {
  return new Promise((resolveWait, reject) => {
    let timer;
    const inspect = () => { const found = messages.slice(start).find(predicate); if (found) { clearTimeout(timer); signal.off('message', inspect); resolveWait(found); } };
    timer = setTimeout(() => { signal.off('message', inspect); reject(new Error('Timed out waiting for bridge; messages=' + JSON.stringify(messages.map(x => ({ type: x.type, connected: x.connected, ok: x.ok, error: x.error }))) + '; stderr=' + stderr.slice(-500))); }, timeout);
    signal.on('message', inspect); inspect();
  });
}
async function waitUntil(fn, label, timeout = 15000) {
  const until = Date.now() + timeout;
  while (Date.now() < until) { const result = await fn(); if (result) return result; await new Promise(r => setTimeout(r, 150)); }
  throw new Error('Timed out: ' + label);
}
function send(op, fields = {}) { const id = randomBytes(6).toString('hex'); child.stdin.write(JSON.stringify({ id, op, ...fields }) + '\n'); return id; }
function find(nodes, name) { for (const n of nodes ?? []) { if (n.Name === name) return n; const nested = find(n.Children, name); if (nested) return nested; } }
async function waitForExit(process) {
  if (!process || process.exitCode !== null || process.signalCode !== null) return true;
  await new Promise(resolveWait => {
    const timer = setTimeout(resolveWait, 2500);
    process.once('exit', () => { clearTimeout(timer); resolveWait(); });
  });
  return process.exitCode !== null || process.signalCode !== null;
}
async function launchOwned(options) {
  try { return await launchBrowser(options); }
  catch (error) { if (error?.agbrowserProcessStillRunning) launchWasUnsafe = true; throw error; }
}
try {
  const query = spawnSync('reg.exe', ['query', registry], { encoding: 'utf8', windowsHide: true });
  if (query.status === 0) throw new Error('Generated registry key already exists');
  if (query.status !== 1) throw new Error('Could not check temporary registry key: ' + query.stderr);
  const add = spawnSync('reg.exe', ['add', registry, '/ve', '/d', hostManifest, '/f'], { encoding: 'utf8', windowsHide: true });
  if (add.status !== 0) throw new Error('Could not register isolated host: ' + add.stderr);
  registered = true;
  process.env.AGB_DESKTOP_DATA_DIR = data;
  child = spawn('dotnet', [resolve(process.env.AGB_EDGE_TEST_PROBE ?? 'packages/desktop.bridge.tests/bin/Release/net10.0-windows/BridgeProbe.dll')], { env: process.env, stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
  child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
  child.stdout.on('data', text => { buffer += text; for (;;) { const nl = buffer.indexOf('\n'); if (nl < 0) break; const line = buffer.slice(0, nl).trim(); buffer = buffer.slice(nl + 1); if (line) { messages.push(JSON.parse(line)); signal.emit('message'); } } });
  child.stderr.on('data', text => { stderr += text; });
  await waitMessage(x => x.type === 'ready');
  session = await launchOwned({ binary: resolveChannelPaths(CHANNELS.find(c => c.id === 'edge')).binary, channelId: 'edge', userDataDir: edgeData, headless: true, loadExtensions: true, extensionPaths: [extension], verticalTabs: false, adblock: false, timeout: 20000 });
  if (session.extensionLoadErrors.length) throw new Error('Extension load failed: ' + JSON.stringify(session.extensionLoadErrors));
  await waitMessage(x => x.type === 'connection' && x.connected);
  const firstSnapshot = await waitMessage(x => x.type === 'snapshot' && x.data?.Available);
  const firstGeneration = firstSnapshot.data.ConnectionGeneration;
  const firstProfileId = firstSnapshot.data.ProfileId;
  if (!firstProfileId) throw new Error('First real Edge profile did not send a persistent identity');
  const url = `chrome-extension://${extensionId}/test/test.html`;
  const target = await session.createPage(url), sid = await session.attach(target);
  const run = async expression => { const r = await session.connection.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sid); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text); return r.result.value; };
  await waitUntil(async () => { try { return (await run('location.href')) === url; } catch { return false; } }, 'extension page');
  const rootFolders = await run(`chrome.bookmarks.getTree().then(tree => tree[0].children.map(node => ({id:node.id, folderType:node.folderType, title:node.title})))`);
  const mobileRoot = rootFolders.find(node => node.folderType === 'mobile' || node.id === '3');
  if (mobileRoot) {
    let mobileStart = messages.length;
    const mobile = await run(`chrome.bookmarks.create({parentId:${JSON.stringify(mobileRoot.id)},title:'edge-mobile-fixture',url:'https://example.test/mobile'})`);
    await waitMessage(x => x.type === 'snapshot' && find(x.data?.AdditionalRoots, 'edge-mobile-fixture'), mobileStart, 5000);
    mobileStart = messages.length;
    await run(`chrome.bookmarks.remove(${JSON.stringify(mobile.id)})`);
    await waitMessage(x => x.type === 'snapshot' && !find(x.data?.AdditionalRoots, 'edge-mobile-fixture'), mobileStart, 5000);
    console.log('PASS Edge mobile favorites are included and kept in sync');
  } else console.log('SKIP live mobile root: this isolated Edge profile does not expose it');
  let start = messages.length;
  const created = await run(`chrome.bookmarks.create({parentId:'1',title:'edge-to-app-fixture',url:'https://example.test/edge'})`);
  await waitMessage(x => x.type === 'snapshot' && find(x.data?.Bar, 'edge-to-app-fixture'), start);
  console.log('PASS real isolated Edge bookmark creation reaches desktop bridge');
  start = messages.length;
  const createId = send('create', { parentId: '1', title: 'app-to-edge-fixture', url: 'https://example.test/app' });
  const createAck = await waitMessage(x => x.type === 'ack' && x.id === createId, start);
  if (!createAck.ok) throw new Error('Create failed: ' + createAck.error);
  const appBookmark = await waitUntil(async () => (await run(`chrome.bookmarks.search({title:'app-to-edge-fixture'})`))?.[0], 'desktop create in Edge');
  start = messages.length;
  const updateId = send('update', { bookmarkId: appBookmark.id, title: 'app-renamed-fixture', url: 'https://example.test/updated' });
  const updateAck = await waitMessage(x => x.type === 'ack' && x.id === updateId, start);
  if (!updateAck.ok) throw new Error('Update failed: ' + updateAck.error);
  await waitUntil(async () => (await run(`chrome.bookmarks.search({title:'app-renamed-fixture'})`))?.some(x => x.url === 'https://example.test/updated'), 'desktop update in Edge');
  start = messages.length;
  const removeId = send('remove', { bookmarkId: appBookmark.id });
  const removeAck = await waitMessage(x => x.type === 'ack' && x.id === removeId, start);
  if (!removeAck.ok) throw new Error('Remove failed: ' + removeAck.error);
  await waitUntil(async () => !(await run(`chrome.bookmarks.search({title:'app-renamed-fixture'})`)).length, 'desktop removal in Edge');
  console.log('PASS desktop bridge create, edit and removal reach real isolated Edge');
  start = messages.length;
  await run(`chrome.bookmarks.remove(${JSON.stringify(created.id)})`);
  await waitMessage(x => x.type === 'snapshot' && !find(x.data?.Bar, 'edge-to-app-fixture'), start);
  console.log('PASS real isolated Edge removal reaches desktop bridge');
  const secondData = join(root, 'edge-data-2'); mkdirSync(secondData);
  start = messages.length;
  secondSession = await launchOwned({ binary: resolveChannelPaths(CHANNELS.find(c => c.id === 'edge')).binary, channelId: 'edge', userDataDir: secondData, headless: true, loadExtensions: true, extensionPaths: [extension], verticalTabs: false, adblock: false, timeout: 20000 });
  if (secondSession.extensionLoadErrors.length) throw new Error('Second extension load failed: ' + JSON.stringify(secondSession.extensionLoadErrors));
  const secondTarget = await secondSession.createPage(url), secondSid = await secondSession.attach(secondTarget);
  const runSecond = async expression => { const r = await secondSession.connection.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, secondSid); if (r.exceptionDetails) throw new Error(r.exceptionDetails.exception?.description ?? r.exceptionDetails.text); return r.result.value; };
  await waitUntil(async () => { try { return (await runSecond('location.href')) === url; } catch { return false; } }, 'second extension page');
  await new Promise(resolveWait => setTimeout(resolveWait, 400));
  const profiles = await waitUntil(() => {
    const latest = messages.slice(start).findLast(x => x.type === 'profiles');
    return latest?.choices?.length === 2 ? latest : null;
  }, 'two connected profiles');
  const firstChoice = profiles.choices.find(x => x.ProfileId === firstProfileId);
  const secondChoice = profiles.choices.find(x => x.ProfileId !== firstProfileId);
  if (!firstChoice?.Selected || !secondChoice?.ProfileId || secondChoice.Selected)
    throw new Error('Simultaneous Edge profiles were not independently identified: ' + JSON.stringify({ firstGeneration, firstProfileId, choices: profiles.choices }));
  if (messages.slice(start).some(x => x.type === 'snapshot' && x.data?.ConnectionGeneration === secondChoice.Generation))
    throw new Error('The second profile silently replaced the first one');
  start = messages.length;
  const selectId = send('select', { generation: secondChoice.Generation });
  const selected = await waitMessage(x => x.type === 'ack' && x.id === selectId, start);
  if (!selected.ok || !selected.persisted) throw new Error('Second profile selection was not saved: ' + selected.error);
  const secondSnapshot = await waitMessage(x => x.type === 'snapshot' && x.data?.ConnectionGeneration === secondChoice.Generation, start);
  const secondGeneration = secondSnapshot.data.ConnectionGeneration;
  const secondProfileId = secondSnapshot.data.ProfileId;
  start = messages.length;
  const staleId = send('create', { generation: firstChoice.Generation, parentId: '1', title: 'stale-profile-fixture', url: 'https://example.test/stale' });
  const staleAck = await waitMessage(x => x.type === 'ack' && x.id === staleId, start);
  if (staleAck.ok || !staleAck.error?.includes('连接已变化')) throw new Error('Stale profile write was not rejected');
  if ((await runSecond(`chrome.bookmarks.search({title:'stale-profile-fixture'})`)).length) throw new Error('Stale write reached the second Edge profile');
  if ((await run(`chrome.bookmarks.search({title:'stale-profile-fixture'})`)).length) throw new Error('Stale write reached the inactive first Edge profile');
  start = messages.length;
  const currentId = send('create', { generation: secondGeneration, parentId: '1', title: 'current-profile-fixture', url: 'https://example.test/current' });
  const currentAck = await waitMessage(x => x.type === 'ack' && x.id === currentId, start);
  if (!currentAck.ok) throw new Error('Current profile write failed: ' + currentAck.error);
  await waitUntil(async () => (await runSecond(`chrome.bookmarks.search({title:'current-profile-fixture'})`)).length === 1, 'current profile write');
  if ((await run(`chrome.bookmarks.search({title:'current-profile-fixture'})`)).length) throw new Error('Selected profile write reached the first Edge profile');
  console.log('PASS two simultaneous isolated Edge profiles stay separate and stale writes are rejected after selection');

  start = messages.length;
  const secondProcess = secondSession.process;
  await secondSession.close();
  if (!await waitForExit(secondProcess)) throw new Error('Second isolated Edge process did not exit');
  secondSession = null;
  await waitMessage(x => x.type === 'connection' && !x.connected, start);
  await new Promise(resolveWait => setTimeout(resolveWait, 300));
  if (messages.slice(start).some(x => x.type === 'snapshot' && x.data?.ProfileId === firstProfileId))
    throw new Error('The first profile was silently selected when the chosen profile disconnected');
  // The exited fixture may leave its old debugging port behind. This path is
  // inside the unique disposable profile, never a user Edge profile.
  rmSync(join(secondData, 'DevToolsActivePort'), { force: true });
  start = messages.length;
  secondSession = await launchOwned({ binary: resolveChannelPaths(CHANNELS.find(c => c.id === 'edge')).binary, channelId: 'edge', userDataDir: secondData, headless: true, loadExtensions: true, extensionPaths: [extension], verticalTabs: false, adblock: false, timeout: 20000 });
  if (secondSession.extensionLoadErrors.length) throw new Error('Reconnected extension load failed: ' + JSON.stringify(secondSession.extensionLoadErrors));
  const reconnected = await waitMessage(x => x.type === 'snapshot' && x.data?.ProfileId === secondProfileId && x.data.ConnectionGeneration !== secondGeneration, start);
  if (!reconnected.data.ProfileId) throw new Error('Second Edge profile identity did not survive restart');
  console.log('PASS the selected Edge profile reconnects with its original identity without switching to the other profile');
} finally {
  let exited = !launchWasUnsafe;
  if (session) { const edgeProcess = session.process; await session.close(); exited = await waitForExit(edgeProcess) && exited; }
  if (secondSession) { const edgeProcess = secondSession.process; await secondSession.close(); exited = await waitForExit(edgeProcess) && exited; }
  if (child) { try { child.stdin.write('{"op":"exit"}\n'); child.stdin.end(); } catch {} }
  exited = await waitForExit(child) && exited;
  let registryClean = !registered;
  if (registered && exited) {
    const del = spawnSync('reg.exe', ['delete', registry, '/f'], { encoding: 'utf8', windowsHide: true });
    const remaining = spawnSync('reg.exe', ['query', registry], { encoding: 'utf8', windowsHide: true });
    registryClean = del.status === 0 && remaining.status === 1;
    if (!registryClean) { console.error('Temporary registry cleanup failed: ' + registry + ' ' + del.stderr); process.exitCode = 1; }
  }
  if (oldData === undefined) delete process.env.AGB_DESKTOP_DATA_DIR; else process.env.AGB_DESKTOP_DATA_DIR = oldData;
  let dataClean = false;
  if (!exited || !registryClean) { console.error('Fixture process or registry key remains; temporary data retained: ' + root); process.exitCode = 1; }
  else try { await cleanupTemporaryFixture(root, 'agb-edge-live-'); dataClean = true; }
  catch (error) { console.error('Temporary test data was not removed: ' + root + ' ' + error.message); process.exitCode = 1; }
  if (dataClean && process.env.AGB_EDGE_TEST_CLEANUP_STATUS) writeFileSync(process.env.AGB_EDGE_TEST_CLEANUP_STATUS, 'clean');
}
