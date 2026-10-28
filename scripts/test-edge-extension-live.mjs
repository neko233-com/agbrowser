import { launchBrowser } from '../packages/core/src/browser/launcher.js';
import { resolve } from 'node:path';
import { CHANNELS, resolveChannelPaths } from '../packages/core/src/browser/channels.js';
const session = await launchBrowser({
  binary: resolveChannelPaths(CHANNELS.find(c => c.id === 'edge')).binary, channelId: 'edge',
  ephemeral: true, headless: true, loadExtensions: true,
  extensionPaths: [resolve('packages/extensions/agb-edge-favorites')], timeout: 20000,
});
try {
  if (session.extensionLoadErrors.length) throw new Error('Edge extension failed to load');
  const url = 'chrome-extension://fjkcppekpigobilckiggnjdfeiiabmee/test/test.html';
  const target = await session.createPage(url);
  const sid = await session.attach(target);
  const run = async expression => {
    const result = await session.connection.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true }, sid);
    if (result.exceptionDetails) throw new Error(result.exceptionDetails.exception?.description ?? result.exceptionDetails.text);
    return result.result.value;
  };
  let ready = false;
  for (let i = 0; i < 100; i++) {
    try { ready = (await run(`location.href`)) === url; } catch {}
    if (ready) break;
    await new Promise(r => setTimeout(r, 100));
  }
  if (!ready) throw new Error('Extension test page failed to load');
  let alarm;
  for (let i = 0; i < 100; i++) {
    alarm = await run(`chrome.alarms.get('reconnect')`);
    if (alarm) break;
    await new Promise(r => setTimeout(r, 100));
  }
  if (!alarm || alarm.periodInMinutes !== 0.5) throw new Error('Extension worker did not create the reconnect alarm');
  console.log('PASS isolated real Edge: extension worker created reconnect alarm');
  const created = await run(`chrome.bookmarks.create({parentId:'1',title:'agbrowser isolated test',url:'https://example.test/'})`);
  const found = await run(`chrome.bookmarks.search({title:'agbrowser isolated test'})`);
  if (!created?.id || !found.some(x => x.id === created.id)) throw new Error('Created bookmark missing');
  await run(`chrome.bookmarks.remove(${JSON.stringify(created.id)})`);
  if ((await run(`chrome.bookmarks.search({title:'agbrowser isolated test'})`)).length) throw new Error('Deleted bookmark still exists');
  console.log('PASS isolated real Edge: extension page and bookmarks API create/search/delete');
} finally { await session.close(); }
