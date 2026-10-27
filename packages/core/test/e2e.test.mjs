/**
 * End-to-end smoke test against a real Chrome instance.
 *
 * Verifies the whole stack: launch -> CDP attach -> navigate -> a11y
 * snapshot -> click -> read -> screenshot -> ad blocking -> hardening.
 */
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createServer } from 'node:http';

import { launchBrowser } from '@agbrowser/core/browser/launcher';
import { PageController } from '@agbrowser/core/snapshot/page';
import { AdBlocker } from '@agbrowser/core/block/adblock';
import { RuleSet } from '@agbrowser/core/block/rules';
import { installHardening } from '@agbrowser/core/browser/hardening';

const CHROME = 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe';
const results = [];
let failures = 0;

function check(name, pass, detail = '') {
  results.push({ name, pass, detail });
  if (!pass) failures++;
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
}

// A local origin that serves an ad-looking request we can watch for.
const AD_HOST = 'ads.test.local';
const hits = [];

const site = createServer((req, res) => {
  hits.push(req.url);
  if (req.url.startsWith('/tracker')) {
    res.writeHead(200, { 'content-type': 'text/plain' });
    res.end('tracked');
    return;
  }
  res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  res.end(`<!DOCTYPE html><html><head><title>agb test</title></head><body>
    <h1>Hello agbrowser</h1>
    <input id="q" type="text" placeholder="search">
    <button id="go" onclick="document.getElementById('out').textContent='clicked'">Press Me</button>
    <div id="out">idle</div>
    <script>document.getElementById('go').addEventListener('click',()=>{});</script>
    <img src="http://${AD_HOST}/tracker.gif" alt="ad">
  </body></html>`);
});

await new Promise((r) => site.listen(0, '127.0.0.1', r));
const sitePort = site.address().port;
const siteUrl = `http://127.0.0.1:${sitePort}/`;

const userDataDir = mkdtempSync(join(tmpdir(), 'agb-e2e-'));
let session = null;

try {
  // ---------------------------------------------------------------- launch
  const t0 = Date.now();
  session = await launchBrowser({
    binary: CHROME,
    channelId: 'chrome',
    accountId: 'Default',
    userDataDir,
    headless: true,
    ephemeral: false,
  });
  const launchMs = Date.now() - t0;
  check('launch browser + CDP attach', true, `${launchMs}ms, port ${session.port}`);

  // -------------------------------------------------------------- adblock
  const ruleSet = new RuleSet().parse(`
||${AD_HOST}^
##.ad-slot
`);
  const blocker = new AdBlocker({ ruleSet, enabled: true });
  await blocker.attach(session);

  await installHardening(session);

  // -------------------------------------------------------------- navigate
  const page = new PageController({ session, targetId: (await session.listPages())[0].targetId });
  await page.navigate(siteUrl, { waitUntil: 'load', timeout: 15_000 });
  const url = await page.getUrl();
  check('navigate to page', url.startsWith(siteUrl), url);

  const title = await page.getTitle();
  check('read title', title === 'agb test', title);

  // -------------------------------------------------------------- snapshot
  const snap = await page.snapshot({ interactiveOnly: true });
  check('a11y snapshot returns refs', snap.count >= 2, `${snap.count} interactive elements`);
  check('snapshot exposes button', /button/i.test(snap.text), snap.text.split('\n')[0] ?? '');
  check('snapshot exposes textbox', /textbox/i.test(snap.text));

  // ----------------------------------------------------------------- click
  const btnRef = Object.values(snap.refs).find((r) => r.role === 'button')?.ref;
  check('locate button ref', Boolean(btnRef), btnRef ?? 'none');

  if (btnRef) {
    await page.click(btnRef);
    const out = await page.evaluate(`document.getElementById('out').textContent`);
    check('click triggers handler', out === 'clicked', `out="${out}"`);
  }

  // ------------------------------------------------------------------ fill
  const inputRef = Object.values(snap.refs).find((r) => r.role === 'textbox')?.ref;
  if (inputRef) {
    await page.fill(inputRef, 'agbrowser test');
    const val = await page.evaluate(`document.getElementById('q').value`);
    check('fill sets input value', val === 'agbrowser test', `value="${val}"`);
  } else {
    check('locate textbox ref', false, 'none');
  }

  // ------------------------------------------------------------------ read
  const text = await page.read();
  check('read page text', text.includes('Hello agbrowser'), text.slice(0, 60).replace(/\n/g, ' '));

  // ------------------------------------------------------------ screenshot
  const shot = await page.screenshot({ format: 'png' });
  check('screenshot returns base64 png', typeof shot === 'string' && shot.length > 500, `${shot.length} chars`);

  // --------------------------------------------------------------- adblock
  // Give the blocked request time to be attempted.
  await new Promise((r) => setTimeout(r, 1200));
  const trackerHit = hits.some((h) => h.startsWith('/tracker'));
  check('ad blocker stopped the tracker request', !trackerHit,
    trackerHit ? 'request reached the server' : 'no tracker request observed');

  // -------------------------------------------------------------- hardening
  const wd = await page.evaluate('navigator.webdriver');
  check('navigator.webdriver is not true', wd !== true, String(wd));

  const cdc = await page.evaluate(`Object.getOwnPropertyNames(window).filter(k=>k.startsWith('cdc_')).length`);
  check('no cdc_ runtime hooks exposed', cdc === 0, `${cdc} hooks`);

  const hasChrome = await page.evaluate(`typeof window.chrome === 'object' && window.chrome !== null`);
  check('window.chrome present', hasChrome === true);

  const perms = await page.evaluate(`navigator.permissions.query({name:'notifications'}).then(s=>s.state)`);
  check('permissions API returns a sane state', typeof perms === 'string', perms);

  // -------------------------------------------------------------- switching
  const pages = await session.listPages();
  check('list pages', pages.length >= 1, `${pages.length} pages`);

  const created = await session.createPage('about:blank');
  check('create new tab', Boolean(created), created);
  await session.closePage(created);
  check('close tab', true);

} catch (err) {
  check('suite completed without exception', false, err.stack ?? String(err));
} finally {
  if (session) await session.close().catch(() => {});
  await new Promise((r) => site.close(r));
  try { rmSync(userDataDir, { recursive: true, force: true }); } catch { /* ignore */ }
}

console.log(`\n${results.length - failures}/${results.length} checks passed`);
process.exit(failures > 0 ? 1 : 0);
