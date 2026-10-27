/**
 * Page-level control for agents.
 *
 * Design goals:
 *  - Token efficiency: the accessibility snapshot returns compact
 *    `@e1`-style refs instead of raw HTML, so an agent sees ~200-400 tokens
 *    for a typical page instead of tens of thousands.
 *  - Stability: refs stay valid for the lifetime of a snapshot; the caller
 *    re-snapshots after navigation or DOM change.
 *  - Speed: no per-call round trip that isn't needed; capabilities are
 *    enabled lazily and cached per session.
 */

/** Roles worth exposing as interactive refs. */
const INTERACTIVE_ROLES = new Set([
  'button', 'link', 'checkbox', 'radio', 'textbox', 'combobox', 'listbox',
  'menuitem', 'menuitemcheckbox', 'menuitemradio', 'option', 'searchbox',
  'slider', 'spinbutton', 'switch', 'tab', 'treeitem',
]);

const IGNORED_NODES = new Set(['none', 'presentation', 'generic', 'InlineTextBox']);

export class PageController {
  /**
   * @param {object} opts
   * @param {import('../browser/launcher.js').BrowserSession} opts.session
   * @param {string} opts.targetId
   */
  constructor({ session, targetId }) {
    this.session = session;
    this.targetId = targetId;
    this.sessionId = null;
    this._caps = new Set();
    /** @type {Map<string, object>} ref -> element descriptor */
    this.refs = new Map();
    this._refCounter = 0;
  }

  async attach() {
    if (this.sessionId) return this.sessionId;
    this.sessionId = await this.session.attach(this.targetId);
    await this._enable('Page');
    await this._enable('Runtime');
    await this._enable('DOM');
    await this._enable('Network');
    return this.sessionId;
  }

  /**
   * Enable a CDP domain once per session.
   *
   * `DOM.enable` matters for element resolution: without it Chromium can
   * refuse to resolve a backend node id into a remote object, which shows up
   * later as "Could not resolve node". So a failure here is surfaced instead
   * of being swallowed.
   */
  async _enable(domain) {
    if (this._caps.has(domain)) return;
    if (domain === 'DOM') {
      await this.session.connection.send('DOM.enable', {}, this.sessionId);
    } else {
      await this.session.connection.trySend(`${domain}.enable`, {}, this.sessionId);
    }
    this._caps.add(domain);
  }

  get connection() {
    return this.session.connection;
  }

  async _send(method, params = {}, timeout = 30_000) {
    await this.attach();
    return this.connection.send(method, params, this.sessionId, timeout);
  }

  // -- navigation ----------------------------------------------------------

  async navigate(url, { waitUntil = 'load', timeout = 30_000 } = {}) {
    await this.attach();
    const normalized = normalizeUrl(url);
    const loaded = this._waitForEvent('Page.loadEventFired', timeout);
    const result = await this._send('Page.navigate', { url: normalized });
    if (result.errorText) throw new Error(`Navigation failed: ${result.errorText}`);
    if (waitUntil === 'load') {
      await loaded.catch(() => {});
    }
    this.refs.clear();
    return { url: normalized, frameId: result.frameId };
  }

  async reload() {
    await this.attach();
    await this._send('Page.reload', { ignoreCache: false });
    this.refs.clear();
  }

  async goBack() {
    const hist = await this._send('Page.getNavigationHistory');
    const idx = hist.currentIndex;
    if (idx <= 0) return { navigated: false };
    const entry = hist.entries[idx - 1];
    await this._send('Page.navigateToHistoryEntry', { entryId: entry.id });
    this.refs.clear();
    return { navigated: true, url: entry.url };
  }

  async goForward() {
    const hist = await this._send('Page.getNavigationHistory');
    const idx = hist.currentIndex;
    if (idx >= hist.entries.length - 1) return { navigated: false };
    const entry = hist.entries[idx + 1];
    await this._send('Page.navigateToHistoryEntry', { entryId: entry.id });
    this.refs.clear();
    return { navigated: true, url: entry.url };
  }

  async getUrl() {
    const { result } = await this._send('Runtime.evaluate', {
      expression: 'location.href',
      returnByValue: true,
    });
    return result.value;
  }

  async getTitle() {
    const { result } = await this._send('Runtime.evaluate', {
      expression: 'document.title',
      returnByValue: true,
    });
    return result.value;
  }

  // -- evaluation ----------------------------------------------------------

  /**
   * Evaluate an expression in the page and return the value.
   * @param {string} expression
   * @param {{awaitPromise?:boolean, returnByValue?:boolean}} [opts]
   */
  async evaluate(expression, { awaitPromise = true, returnByValue = true } = {}) {
    const { result, exceptionDetails } = await this._send('Runtime.evaluate', {
      expression,
      awaitPromise,
      returnByValue,
      userGesture: true,
    });
    if (exceptionDetails) {
      throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text ?? 'Evaluation failed');
    }
    return result.value;
  }

  // -- snapshots -----------------------------------------------------------

  /**
   * Build a compact accessibility snapshot with stable element refs.
   *
   * @param {object} [opts]
   * @param {boolean} [opts.interactiveOnly] only include actionable elements
   * @param {string}  [opts.selector] CSS scope
   * @param {number}  [opts.maxDepth]
   * @returns {Promise<{text:string, refs:Record<string,object>, count:number}>}
   */
  async snapshot({ interactiveOnly = true, selector = null, maxDepth = 0 } = {}) {
    await this.attach();
    await this._enable('Accessibility');

    let scopeBackendNodeId = null;
    if (selector) {
      const { root } = await this._send('DOM.getDocument', { depth: -1, pierce: false });
      const { nodeId } = await this._send('DOM.querySelector', { nodeId: root.nodeId, selector });
      if (nodeId) {
        const described = await this._send('DOM.describeNode', { nodeId });
        scopeBackendNodeId = described.node.backendNodeId;
      }
    }

    const { nodes } = await this._send('Accessibility.getFullAXTree', {
      depth: -1,
      ...(scopeBackendNodeId ? { frameId: undefined } : {}),
    });

    this.refs.clear();
    this._refCounter = 0;

    const byId = new Map(nodes.map((n) => [n.nodeId, n]));
    const lines = [];
    let count = 0;

    const rootNode = nodes.find((n) => !n.parentId) ?? nodes[0];

    const walk = (node, depth) => {
      if (!node) return;
      if (maxDepth > 0 && depth > maxDepth) return;

      const role = node.role?.value ?? '';
      const name = node.name?.value ?? '';
      const ignored = node.ignored ?? false;

      if (!ignored && !IGNORED_NODES.has(role)) {
        const isInteractive = INTERACTIVE_ROLES.has(role);
        if (!interactiveOnly || isInteractive) {
          const ref = `@e${++this._refCounter}`;
          const descriptor = {
            ref,
            role,
            name,
            backendNodeId: node.backendDOMNodeId ?? null,
            nodeId: node.nodeId,
          };
          this.refs.set(ref, descriptor);
          count++;
          lines.push(`${ref} ${role}${name ? ` "${truncate(name, 80)}"` : ''}`);
        }
      }

      for (const childId of node.childIds ?? []) {
        const child = byId.get(childId);
        if (child) walk(child, depth + 1);
      }
    };

    if (rootNode) walk(rootNode, 0);

    return {
      text: lines.join('\n'),
      refs: Object.fromEntries(this.refs),
      count,
    };
  }

  /**
   * Resolve a ref to a DOM node description.
   * @param {string} ref
   */
  async resolveRef(ref) {
    const descriptor = this.refs.get(ref);
    if (!descriptor) {
      throw new Error(`Unknown ref "${ref}". Take a fresh snapshot first.`);
    }
    return descriptor;
  }

  // -- interaction ---------------------------------------------------------

  /**
   * Resolve a backend node id into a remote object id.
   *
   * Chromium resets its DOM agent on navigation, so a node id captured before
   * the navigation can stop resolving. When that happens we re-establish the
   * document context and retry, which restores resolution for nodes in the
   * current document.
   */
  async _resolveObject(backendNodeId) {
    await this.attach();

    let result = null;
    try {
      result = await this._send('DOM.resolveNode', { backendNodeId });
    } catch { /* retry below */ }

    if (!result?.object?.objectId) {
      // Re-establish the document, then try again.
      await this._send('DOM.getDocument', { depth: -1, pierce: false });
      result = await this._send('DOM.resolveNode', { backendNodeId });
    }

    return result?.object?.objectId ?? null;
  }

  /**
   * Click an element by ref.
   * Uses a real mouse event at the element's centre so that sites relying on
   * trusted events behave the same way they would for a human.
   */
  async click(ref, { button = 'left', clickCount = 1 } = {}) {
    const descriptor = await this.resolveRef(ref);
    if (!descriptor.backendNodeId) throw new Error(`Ref ${ref} has no backend node id`);

    const objectId = await this._resolveObject(descriptor.backendNodeId);
    if (!objectId) {
      throw new Error(
        `Could not resolve node for ${ref}. The page may have changed — take a fresh snapshot.`,
      );
    }

    // Scroll into view first, then compute the clickable point.
    await this._send('Runtime.callFunctionOn', {
      objectId,
      functionDeclaration: 'function(){ this.scrollIntoView({block:"center",inline:"center"}); return true; }',
    });

    const { result } = await this._send('Runtime.callFunctionOn', {
      objectId,
      functionDeclaration: `function(){
        const r = this.getBoundingClientRect();
        return { x: r.left + r.width/2, y: r.top + r.height/2, w: r.width, h: r.height };
      }`,
      returnByValue: true,
    });

    const box = result.value;
    if (!box || box.w === 0 || box.h === 0) {
      // Zero-size element: fall back to a DOM-level click.
      await this._send('Runtime.callFunctionOn', {
        objectId,
        functionDeclaration: 'function(){ this.click(); return true; }',
      });
      return { clicked: ref, method: 'dom' };
    }

    const x = box.x;
    const y = box.y;
    const buttons = button === 'right' ? 2 : 1;

    await this._send('Input.dispatchMouseEvent', {
      type: 'mouseMoved',
      x, y, button: 'none',
    });
    await this._send('Input.dispatchMouseEvent', {
      type: 'mousePressed',
      x, y, button,
      buttons,
      clickCount,
    });
    await this._send('Input.dispatchMouseEvent', {
      type: 'mouseReleased',
      x, y, button,
      buttons,
      clickCount,
    });

    return { clicked: ref, method: 'input', x, y };
  }

  /**
   * Fill an input with text: focus, clear, type.
   */
  async fill(ref, text) {
    const descriptor = await this.resolveRef(ref);
    if (!descriptor.backendNodeId) throw new Error(`Ref ${ref} has no backend node id`);

    const objectId = await this._resolveObject(descriptor.backendNodeId);
    if (!objectId) {
      throw new Error(
        `Could not resolve node for ${ref}. The page may have changed — take a fresh snapshot.`,
      );
    }

    await this._send('Runtime.callFunctionOn', {
      objectId,
      functionDeclaration: `function(){
        this.focus();
        const proto = this instanceof HTMLTextAreaElement
          ? HTMLTextAreaElement.prototype : HTMLInputElement.prototype;
        const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
        if (setter) setter.call(this, '');
        else this.value = '';
        this.dispatchEvent(new Event('input', { bubbles: true }));
        this.dispatchEvent(new Event('change', { bubbles: true }));
        return true;
      }`,
    });

    await this._send('Input.insertText', { text: String(text) });

    // Fire input/change so frameworks (React/Vue) pick up the value.
    await this._send('Runtime.callFunctionOn', {
      objectId,
      functionDeclaration: `function(){
        this.dispatchEvent(new Event('input', { bubbles: true }));
        this.dispatchEvent(new Event('change', { bubbles: true }));
        return this.value;
      }`,
      returnByValue: true,
    });

    return { filled: ref, text: String(text) };
  }

  /** Type text without clearing first. */
  async type(ref, text) {
    await this.resolveRef(ref);
    await this._send('Input.insertText', { text: String(text) });
    return { typed: ref, text: String(text) };
  }

  /** Press a key such as "Enter", "Tab", "Control+a". */
  async press(key) {
    const { modifiers, keyCode, code, text } = parseKey(key);
    await this._send('Input.dispatchKeyEvent', {
      type: text ? 'keyDown' : 'rawKeyDown',
      key,
      code,
      windowsVirtualKeyCode: keyCode,
      nativeVirtualKeyCode: keyCode,
      modifiers,
      text,
    });
    if (text) {
      await this._send('Input.dispatchKeyEvent', {
        type: 'char',
        key,
        text,
        modifiers,
      });
    }
    await this._send('Input.dispatchKeyEvent', {
      type: 'keyUp',
      key,
      code,
      windowsVirtualKeyCode: keyCode,
      nativeVirtualKeyCode: keyCode,
      modifiers,
    });
    return { pressed: key };
  }

  /** Scroll the page. */
  async scroll({ direction = 'down', amount = 300 } = {}) {
    const delta = direction === 'up' ? -amount
      : direction === 'down' ? amount
        : direction === 'top' ? -100000
          : direction === 'bottom' ? 100000 : amount;
    await this._send('Input.dispatchMouseEvent', {
      type: 'mouseWheel',
      x: 0, y: 0,
      deltaX: 0, deltaY: delta,
    });
    return { scrolled: direction, amount };
  }

  /** Hover over an element. */
  async hover(ref) {
    const descriptor = await this.resolveRef(ref);
    const objectId = await this._resolveObject(descriptor.backendNodeId);
    if (!objectId) throw new Error(`Could not resolve node for ${ref}`);
    const { result } = await this._send('Runtime.callFunctionOn', {
      objectId,
      functionDeclaration: 'function(){ const r=this.getBoundingClientRect(); return {x:r.left+r.width/2,y:r.top+r.height/2}; }',
      returnByValue: true,
    });
    const { x, y } = result.value;
    await this._send('Input.dispatchMouseEvent', { type: 'mouseMoved', x, y, button: 'none' });
    return { hovered: ref, x, y };
  }

  // -- extraction ----------------------------------------------------------

  /**
   * Read visible text from the page, optionally scoped to a selector.
   */
  async read({ selector = null, maxLength = 20_000 } = {}) {
    const expression = selector
      ? `(()=>{const el=document.querySelector(${JSON.stringify(selector)});return el?(el.innerText||el.textContent||''):''})()`
      : `(document.body && (document.body.innerText || document.body.textContent)) || ''`;
    const text = await this.evaluate(expression);
    const value = String(text ?? '');
    return value.length > maxLength ? `${value.slice(0, maxLength)}\n...[truncated]` : value;
  }

  /** Screenshot as base64 PNG. */
  async screenshot({ format = 'png', fullPage = false, quality } = {}) {
    const params = { format, captureBeyondViewport: fullPage, fromSurface: true };
    if (format === 'jpeg' && quality != null) params.quality = quality;
    const { data } = await this._send('Page.captureScreenshot', params, 60_000);
    return data;
  }

  /** Extract structured data via a JSON expression evaluated in the page. */
  async extract(expression) {
    const value = await this.evaluate(expression);
    return value;
  }

  // -- helpers -------------------------------------------------------------

  _waitForEvent(method, timeout) {
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.connection.off(method, handler);
        reject(new Error(`Timed out waiting for ${method}`));
      }, timeout);
      const handler = (params, sessionId) => {
        if (sessionId && sessionId !== this.sessionId) return;
        clearTimeout(timer);
        this.connection.off(method, handler);
        resolve(params);
      };
      this.connection.on(method, handler);
    });
  }
}

/** Normalize a user-supplied URL into something navigable. */
export function normalizeUrl(input) {
  const url = String(input ?? '').trim();
  if (!url) throw new Error('URL is empty');
  if (/^(https?|file|data|about|chrome|devtools|view-source|chrome-extension):/i.test(url)) return url;
  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(url)) return url;
  // Looks like a bare domain
  if (/^[\w-]+(\.[\w-]+)+(\/.*)?$/.test(url)) return `https://${url}`;
  return `https://www.google.com/search?q=${encodeURIComponent(url)}`;
}

const KEY_CODES = {
  Enter: 13, Tab: 9, Escape: 27, Backspace: 8, Delete: 46,
  ArrowUp: 38, ArrowDown: 40, ArrowLeft: 37, ArrowRight: 39,
  Home: 36, End: 35, PageUp: 33, PageDown: 34, Space: 32,
};

const MODIFIERS = { Alt: 1, Control: 2, Ctrl: 2, Meta: 4, Command: 4, Cmd: 4, Shift: 8 };

function parseKey(spec) {
  const parts = String(spec).split('+').map((p) => p.trim());
  const key = parts.pop();
  let modifiers = 0;
  for (const mod of parts) modifiers |= MODIFIERS[mod] ?? 0;

  const named = KEY_CODES[key] ?? key.toUpperCase().charCodeAt(0);
  const code = key.length === 1 ? `Key${key.toUpperCase()}` : key;
  const text = key.length === 1 ? key : undefined;

  return { modifiers, keyCode: named, code, text };
}

function truncate(str, max) {
  const s = String(str).replace(/\s+/g, ' ').trim();
  return s.length > max ? `${s.slice(0, max)}…` : s;
}
