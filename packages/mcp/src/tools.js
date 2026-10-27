/**
 * agbrowser MCP tool set.
 *
 * Tool naming follows the `browser_*` convention used by other agent
 * browsers so that existing skills and prompts keep working.
 *
 * Design notes:
 *  - `browser_open` returns a compact a11y snapshot with `@eN` refs instead
 *    of raw HTML. An agent then interacts via those refs, which keeps the
 *    token cost of a page read in the low hundreds rather than tens of
 *    thousands.
 *  - Actions are verified: after a click we report the new URL so the agent
 *    can confirm the outcome rather than assuming success.
 */
import { McpServer } from './protocol.js';

const INSTRUCTIONS = `agbrowser controls a real Chrome/Edge instance over the DevTools protocol.

Core loop:
  1. browser_open <url>       — navigate and get a compact snapshot
  2. browser_click @e3        — act on an element reference
  3. browser_snapshot         — re-read the page after any change

Element references (@e1, @e2, ...) are valid until the page changes.
Always re-snapshot after navigation or a DOM change.

The browser is a real Chromium with the user's own profile: cookies, logins,
bookmarks and the password manager work normally, and pages behave exactly as
they do for a human. Ads and trackers are blocked at the network layer.

Accounts & tabs:
  browser_accounts           — list channels (chrome/edge/...) and their profiles
  browser_switch_account     — switch the active account
  browser_sync_*             — mirror tabs between accounts
`;

export function createServer(runtime) {
  const server = new McpServer({
    name: 'agbrowser',
    version: '0.1.0',
    instructions: INSTRUCTIONS,
  });

  // -- navigation & reading ------------------------------------------------

  server.tool({
    name: 'browser_open',
    description: 'Open a URL in the browser and return a compact accessibility snapshot with clickable element references.',
    inputSchema: {
      type: 'object',
      properties: {
        url: { type: 'string', description: 'URL to open' },
        interactiveOnly: { type: 'boolean', description: 'Only list interactive elements (default true)' },
        depth: { type: 'number', description: 'Max tree depth; 0 = unlimited' },
      },
      required: ['url'],
    },
    handler: async ({ url, interactiveOnly = true, depth = 0 }) => {
      const page = await runtime.getPage();
      await page.navigate(url);
      const snap = await page.snapshot({ interactiveOnly, maxDepth: depth });
      const title = await page.getTitle();
      return {
        content: [
          { type: 'text', text: `Opened: ${await page.getUrl()}\nTitle: ${title}\n\n${snap.text}` },
        ],
      };
    },
  });

  server.tool({
    name: 'browser_snapshot',
    description: 'Take a fresh accessibility snapshot of the current page. Use after navigation or a DOM change to get fresh element references.',
    inputSchema: {
      type: 'object',
      properties: {
        interactiveOnly: { type: 'boolean', description: 'Only list interactive elements (default true)' },
        depth: { type: 'number', description: 'Max tree depth' },
        selector: { type: 'string', description: 'CSS selector to scope the snapshot' },
      },
    },
    handler: async ({ interactiveOnly = true, depth = 0, selector = null }) => {
      const page = await runtime.getPage();
      const snap = await page.snapshot({ interactiveOnly, maxDepth: depth, selector });
      return {
        content: [{ type: 'text', text: snap.text || '(no matching elements)' }],
      };
    },
  });

  server.tool({
    name: 'browser_read',
    description: 'Read the visible text of the current page, optionally scoped to a CSS selector.',
    inputSchema: {
      type: 'object',
      properties: {
        selector: { type: 'string', description: 'CSS selector to scope the reading' },
        maxLength: { type: 'number', description: 'Max characters (default 20000)' },
      },
    },
    handler: async ({ selector = null, maxLength = 20_000 }) => {
      const page = await runtime.getPage();
      const text = await page.read({ selector, maxLength });
      return { content: [{ type: 'text', text: text || '(empty)' }] };
    },
  });

  server.tool({
    name: 'browser_extract',
    description: 'Evaluate a JavaScript expression in the page and return the result. Use for structured data extraction.',
    inputSchema: {
      type: 'object',
      properties: {
        expression: { type: 'string', description: 'JS expression; the value is returned as JSON' },
      },
      required: ['expression'],
    },
    handler: async ({ expression }) => {
      const page = await runtime.getPage();
      const value = await page.evaluate(expression);
      return { content: [{ type: 'text', text: JSON.stringify(value, null, 2) }] };
    },
  });

  // -- interaction ----------------------------------------------------------

  server.tool({
    name: 'browser_click',
    description: 'Click an element by reference from the latest snapshot (e.g. "@e3").',
    inputSchema: {
      type: 'object',
      properties: {
        ref: { type: 'string', description: 'Element reference such as @e3' },
      },
      required: ['ref'],
    },
    handler: async ({ ref }) => {
      const page = await runtime.getPage();
      await page.click(ref);
      const url = await page.getUrl();
      return { content: [{ type: 'text', text: `Clicked ${ref}. Current URL: ${url}` }] };
    },
  });

  server.tool({
    name: 'browser_fill',
    description: 'Fill an input field by reference with the given text (clears existing value first).',
    inputSchema: {
      type: 'object',
      properties: {
        ref: { type: 'string', description: 'Element reference such as @e2' },
        text: { type: 'string', description: 'Text to type' },
      },
      required: ['ref', 'text'],
    },
    handler: async ({ ref, text }) => {
      const page = await runtime.getPage();
      await page.fill(ref, text);
      return { content: [{ type: 'text', text: `Filled ${ref} with ${text.length} characters.` }] };
    },
  });

  server.tool({
    name: 'browser_type',
    description: 'Type text into an element without clearing it first (for typing into a field that already has content).',
    inputSchema: {
      type: 'object',
      properties: {
        ref: { type: 'string', description: 'Element reference' },
        text: { type: 'string', description: 'Text to type' },
      },
      required: ['ref', 'text'],
    },
    handler: async ({ ref, text }) => {
      const page = await runtime.getPage();
      await page.type(ref, text);
      return { content: [{ type: 'text', text: `Typed into ${ref}.` }] };
    },
  });

  server.tool({
    name: 'browser_press',
    description: 'Press a key or key combination, e.g. "Enter", "Tab", "Control+a", "ArrowDown".',
    inputSchema: {
      type: 'object',
      properties: {
        key: { type: 'string', description: 'Key or combination' },
      },
      required: ['key'],
    },
    handler: async ({ key }) => {
      const page = await runtime.getPage();
      await page.press(key);
      return { content: [{ type: 'text', text: `Pressed ${key}` }] };
    },
  });

  server.tool({
    name: 'browser_scroll',
    description: 'Scroll the page.',
    inputSchema: {
      type: 'object',
      properties: {
        direction: { type: 'string', enum: ['up', 'down', 'top', 'bottom'], description: 'Scroll direction' },
        amount: { type: 'number', description: 'Pixels to scroll (default 300)' },
      },
    },
    handler: async ({ direction = 'down', amount = 300 }) => {
      const page = await runtime.getPage();
      await page.scroll({ direction, amount });
      return { content: [{ type: 'text', text: `Scrolled ${direction}` }] };
    },
  });

  server.tool({
    name: 'browser_hover',
    description: 'Hover over an element by reference.',
    inputSchema: {
      type: 'object',
      properties: {
        ref: { type: 'string', description: 'Element reference' },
      },
      required: ['ref'],
    },
    handler: async ({ ref }) => {
      const page = await runtime.getPage();
      await page.hover(ref);
      return { content: [{ type: 'text', text: `Hovered ${ref}` }] };
    },
  });

  server.tool({
    name: 'browser_wait',
    description: 'Wait for a condition: a selector to appear, a text to be present, or a fixed delay.',
    inputSchema: {
      type: 'object',
      properties: {
        selector: { type: 'string', description: 'CSS selector to wait for' },
        text: { type: 'string', description: 'Text to wait for' },
        ms: { type: 'number', description: 'Fixed delay in milliseconds' },
        timeout: { type: 'number', description: 'Timeout in milliseconds (default 10000)' },
      },
    },
    handler: async ({ selector, text, ms, timeout = 10_000 }) => {
      const page = await runtime.getPage();
      if (ms) {
        await new Promise((r) => setTimeout(r, ms));
        return { content: [{ type: 'text', text: `Waited ${ms}ms` }] };
      }
      const deadline = Date.now() + timeout;
      while (Date.now() < deadline) {
        const ok = await page.evaluate(`(()=>{
          try {
            ${selector ? `if(!document.querySelector(${JSON.stringify(selector)})) return false;` : ''}
            ${text ? `if(!(document.body && document.body.innerText||'').includes(${JSON.stringify(text)})) return false;` : ''}
            return true;
          } catch(e) { return false; }
        })()`);
        if (ok) return { content: [{ type: 'text', text: 'Condition met' }] };
        await new Promise((r) => setTimeout(r, 150));
      }
      throw new Error('Timed out waiting for condition');
    },
  });

  // -- visual --------------------------------------------------------------

  server.tool({
    name: 'browser_screenshot',
    description: 'Capture a screenshot of the current page and return it as an image.',
    inputSchema: {
      type: 'object',
      properties: {
        fullPage: { type: 'boolean', description: 'Capture the full scrollable page' },
        format: { type: 'string', enum: ['png', 'jpeg'], description: 'Image format' },
      },
    },
    handler: async ({ fullPage = false, format = 'png' }) => {
      const page = await runtime.getPage();
      const data = await page.screenshot({ format, fullPage });
      return {
        content: [
          { type: 'text', text: `Screenshot of ${await page.getUrl()}` },
          { type: 'image', data, mimeType: format === 'jpeg' ? 'image/jpeg' : 'image/png' },
        ],
      };
    },
  });

  // -- navigation control --------------------------------------------------

  server.tool({
    name: 'browser_back',
    description: 'Go back in history.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const page = await runtime.getPage();
      const r = await page.goBack();
      return { content: [{ type: 'text', text: r.navigated ? `Went back to ${r.url}` : 'Already at the oldest entry' }] };
    },
  });

  server.tool({
    name: 'browser_forward',
    description: 'Go forward in history.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const page = await runtime.getPage();
      const r = await page.goForward();
      return { content: [{ type: 'text', text: r.navigated ? `Went forward to ${r.url}` : 'Already at the newest entry' }] };
    },
  });

  server.tool({
    name: 'browser_reload',
    description: 'Reload the current page.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const page = await runtime.getPage();
      await page.reload();
      return { content: [{ type: 'text', text: 'Reloaded' }] };
    },
  });

  // -- tabs ----------------------------------------------------------------

  server.tool({
    name: 'browser_tabs',
    description: 'List open tabs, or open/close/switch between them.',
    inputSchema: {
      type: 'object',
      properties: {
        action: { type: 'string', enum: ['list', 'open', 'close', 'switch'], description: 'Operation' },
        url: { type: 'string', description: 'URL to open (action=open)' },
        targetId: { type: 'string', description: 'Tab to close or switch to' },
      },
    },
    handler: async ({ action = 'list', url, targetId }) => {
      const session = await runtime.getSession();
      if (action === 'open') {
        const id = await session.createPage(url ?? 'about:blank');
        return { content: [{ type: 'text', text: `Opened tab ${id}` }] };
      }
      if (action === 'close') {
        if (!targetId) throw new Error('targetId is required to close a tab');
        await session.closePage(targetId);
        return { content: [{ type: 'text', text: `Closed tab ${targetId}` }] };
      }
      if (action === 'switch') {
        if (!targetId) throw new Error('targetId is required to switch tabs');
        await session.connection.send('Target.activateTarget', { targetId });
        runtime.setActiveTarget(targetId);
        return { content: [{ type: 'text', text: `Switched to tab ${targetId}` }] };
      }
      const pages = await session.listPages();
      const lines = pages.map((p, i) => `${i === runtime.activeTargetIndex() ? '*' : ' '} ${p.targetId}  ${p.title}  ${p.url}`);
      return { content: [{ type: 'text', text: lines.join('\n') || '(no tabs)' }] };
    },
  });

  // -- accounts & sync ------------------------------------------------------

  server.tool({
    name: 'browser_accounts',
    description: 'List browser channels (chrome/edge/...) and the account profiles inside them, with the active one marked.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const info = await runtime.listAccounts();
      const lines = info.channels.map((c) => {
        const accounts = c.accounts.map((a) =>
          `    ${a.id === info.activeAccountId ? '*' : ' '} ${a.id}  ${a.name}${a.email ? `  <${a.email}>` : ''}`);
        return `  ${c.id}  (${c.label})\n${accounts.join('\n')}`;
      });
      return {
        content: [{
          type: 'text',
          text: `Active account: ${info.activeAccountId ?? '(none)'}\n\n${lines.join('\n\n')}`,
        }],
      };
    },
  });

  server.tool({
    name: 'browser_switch_account',
    description: 'Switch the active browser account (profile). The browser restarts on the new profile.',
    inputSchema: {
      type: 'object',
      properties: {
        accountId: { type: 'string', description: 'Account id, e.g. "chrome/Default" or "edge/Profile 1"' },
      },
      required: ['accountId'],
    },
    handler: async ({ accountId }) => {
      await runtime.switchAccount(accountId);
      return { content: [{ type: 'text', text: `Switched to account ${accountId}` }] };
    },
  });

  server.tool({
    name: 'browser_sync_add',
    description: 'Create a rule that mirrors tabs from one account into another.',
    inputSchema: {
      type: 'object',
      properties: {
        source: { type: 'string', description: 'Source account id' },
        target: { type: 'string', description: 'Target account id' },
        mode: { type: 'string', enum: ['mirror', 'active-only', 'metadata-only'], description: 'Sync mode' },
      },
      required: ['source', 'target'],
    },
    handler: async ({ source, target, mode = 'mirror' }) => {
      const rule = await runtime.addSyncRule({ source, target, mode });
      return { content: [{ type: 'text', text: `Created sync rule ${rule.id}: ${source} -> ${target} (${mode})` }] };
    },
  });

  server.tool({
    name: 'browser_sync_list',
    description: 'List active tab sync rules.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const rules = await runtime.listSyncRules();
      if (!rules.length) return { content: [{ type: 'text', text: '(no sync rules)' }] };
      const lines = rules.map((r) => `${r.id}  ${r.source_account} -> ${r.target_account}  [${r.mode}]${r.enabled ? '' : ' (disabled)'}`);
      return { content: [{ type: 'text', text: lines.join('\n') }] };
    },
  });

  server.tool({
    name: 'browser_sync_run',
    description: 'Immediately push all open tabs of the source account into the target account.',
    inputSchema: {
      type: 'object',
      properties: {
        source: { type: 'string', description: 'Source account id' },
        target: { type: 'string', description: 'Target account id' },
      },
      required: ['source', 'target'],
    },
    handler: async ({ source, target }) => {
      const created = await runtime.runFullSync(source, target);
      return { content: [{ type: 'text', text: `Synced ${created.length} tabs from ${source} to ${target}` }] };
    },
  });

  // -- diagnostics ----------------------------------------------------------

  server.tool({
    name: 'browser_status',
    description: 'Report runtime status: active account, browser version, ad-blocking stats, tab counts.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const status = await runtime.status();
      return { content: [{ type: 'text', text: JSON.stringify(status, null, 2) }] };
    },
  });

  server.tool({
    name: 'browser_adblock_stats',
    description: 'Report how many requests the ad blocker stopped and which rules fired.',
    inputSchema: { type: 'object', properties: {} },
    handler: async () => {
      const stats = await runtime.adblockStats();
      return { content: [{ type: 'text', text: JSON.stringify(stats, null, 2) }] };
    },
  });

  server.tool({
    name: 'browser_speed_test',
    description: 'Measure network latency, jitter and download throughput, plus current page performance metrics.',
    inputSchema: {
      type: 'object',
      properties: {
        url: { type: 'string', description: 'URL to measure against (default: a small latency probe)' },
      },
    },
    handler: async ({ url }) => {
      const result = await runtime.speedTest(url);
      return { content: [{ type: 'text', text: JSON.stringify(result, null, 2) }] };
    },
  });

  return server;
}
