/**
 * Automation-surface hardening.
 *
 * Many sites refuse to serve an automated client. Those refusals are almost
 * always based on *observable signals* rather than on the intent of the user:
 *
 *   1. `navigator.webdriver === true`      (set by --enable-automation / headless)
 *   2. A visible `cdc_` / CDP runtime hook  (left behind by Runtime.enable)
 *   3. Missing or generic `navigator.plugins` / `languages`
 *   4. `window.chrome` missing entirely      (non-Chromium lookalikes)
 *   5. Permission-API defaults that differ from a real browser
 *   6. Blanking of `Notification.permission`
 *
 * agbrowser ships a real Chromium and drives it over the DevTools protocol.
 * This module removes the *observable* differences between "a browser that a
 * human is using" and "a browser being driven by an agent", so that pages
 * behave identically in both cases.
 *
 * The goal is parity, not deception of the user: the person clicking the
 * button is the account owner. The site should not be able to tell the
 * difference between a person clicking and an agent clicking on that
 * person's behalf — otherwise agent-assisted browsing is impossible on sites
 * that gate ordinary functionality behind bot checks.
 */

/**
 * Injected before any page script runs.
 * Kept as a single self-contained expression: no external references, so it
 * cannot fail to load.
 */
export const HARDENING_SCRIPT = `
(() => {
  const patch = (obj, prop, value) => {
    try {
      Object.defineProperty(obj, prop, { get: () => value, configurable: true });
    } catch (e) {}
  };

  // 1. navigator.webdriver must be false/undefined, never true.
  //    Chromium defines it on Navigator.prototype as an accessor, so it has to
  //    be shadowed on the instance rather than deleted from the prototype.
  try {
    Object.defineProperty(navigator, 'webdriver', {
      get: () => false,
      configurable: true,
      enumerable: true,
    });
  } catch (e) {}
  try {
    const proto = Object.getPrototypeOf(navigator);
    Object.defineProperty(proto, 'webdriver', {
      get: () => false,
      configurable: true,
      enumerable: true,
    });
  } catch (e) {}

  // 2. Remove CDP runtime hooks. When Runtime.enable is active the page can
  //    see a 'cdc_' property and console events carry a DevTools marker.
  try {
    const keys = Object.getOwnPropertyNames(window);
    for (const k of keys) {
      if (typeof k === 'string' && (k.startsWith('cdc_') || k.startsWith('__webdriver') ||
          k.startsWith('_Selenium_IDE_') || k.startsWith('__driver_evaluate') ||
          k.startsWith('__webdriver_script_fn') || k.startsWith('__selenium_unwrapped') ||
          k.startsWith('__webdriver_script_func') || k.startsWith('__driver_') ||
          k.startsWith('__fxdriver_') || k.startsWith('_phantom') || k.startsWith('callPhantom') ||
          k.startsWith('callSelenium') || k.startsWith('__nightmare') || k.startsWith('domAutomation') ||
          k.startsWith('__playwright') || k.startsWith('__puppeteer_evaluation')) {
        try { delete window[k]; } catch (e) {}
      }
    }
  } catch (e) {}

  // 3. Real Chromium exposes window.chrome with a runtime object.
  try {
    if (!window.chrome) {
      window.chrome = {
        runtime: {},
        loadTimes: function() {},
        csi: function() {},
        app: { isInstalled: false },
      };
    }
    if (!window.chrome.runtime) window.chrome.runtime = {};
  } catch (e) {}

  // 4. Permissions default to "prompt" like a normal browser.
  try {
    const perms = ['geolocation', 'notifications', 'camera', 'microphone', 'midi', 'clipboard-read'];
    const originalQuery = navigator.permissions && navigator.permissions.query;
    if (originalQuery) {
      navigator.permissions.query = function(desc) {
        const p = originalQuery.call(this, desc);
        if (desc && perms.includes(desc.name)) {
          return p.then(status => ({ state: status.state, name: desc.name, onchange: null }));
        }
        return p;
      };
    }
  } catch (e) {}

  // 5. Notification.permission should be "default", not "denied" (headless).
  try {
    if (window.Notification && Notification.permission === 'denied') {
      patch(Notification, 'permission', 'default');
    }
  } catch (e) {}

  // 6. Keep the native plugin/mime lists intact instead of an empty array.
  try {
    if (!navigator.plugins || navigator.plugins.length === 0) {
      const mk = (name, filename, description) => ({ name, filename, description, length: 1 });
      const list = [
        mk('PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
        mk('Chrome PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
        mk('Chromium PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
        mk('Microsoft Edge PDF Viewer', 'internal-pdf-viewer', 'Portable Document Format'),
        mk('WebKit built-in PDF', 'internal-pdf-viewer', 'Portable Document Format'),
      ];
      patch(navigator, 'plugins', list);
    }
  } catch (e) {}

  // 7. A real browser exposes a non-empty language list.
  try {
    if (!navigator.languages || navigator.languages.length === 0) {
      patch(navigator, 'languages', [navigator.language || 'zh-CN']);
    }
  } catch (e) {}

  // 8. WebGL vendor/renderer strings: headless reports SwiftShader.
  try {
    const getParam = WebGLRenderingContext.prototype.getParameter;
    WebGLRenderingContext.prototype.getParameter = function(p) {
      if (p === 37445) return 'Intel Inc.';
      if (p === 37446) return 'Intel Iris OpenGL Engine';
      return getParam.apply(this, [p]);
    };
  } catch (e) {}

  // 9. Headless builds expose a vendor string that names the headless runtime.
  try {
    const nav = navigator;
    if (nav.userAgent && /Headless/i.test(nav.userAgent)) {
      Object.defineProperty(nav, 'userAgent', {
        get: () => nav.userAgent.replace(/HeadlessChrome/g, 'Chrome'),
        configurable: true,
      });
    }
  } catch (e) {}

  // 10. Suppress the DevTools console marker that appears when Runtime is
  //     enabled on a target. Sites can detect it via a thrown console error.
  try {
    const origError = console.error;
    console.error = function(...args) {
      if (typeof args[0] === 'string' && /DevTools/i.test(args[0])) return;
      return origError.apply(this, args);
    };
  } catch (e) {}
})();
`;

/**
 * Install the hardening script into a session so it runs before page scripts.
 *
 * The script is registered for *new* documents, so it must be installed
 * before the first navigation. It is also injected into the currently loaded
 * document so that a session hardened mid-flight is covered immediately.
 *
 * @param {import('./launcher.js').BrowserSession} session
 */
export async function installHardening(session) {
  const { connection } = session;

  // Applies to every document created from now on, in every frame.
  await connection.trySend('Page.addScriptToEvaluateOnNewDocument', {
    source: HARDENING_SCRIPT,
    runImmediately: true,
    worldName: 'MAIN',
  });

  // Also inject into targets that get attached later.
  connection.on('Target.attachedToTarget', async (params) => {
    const { sessionId } = params;
    if (!sessionId) return;
    await connection.trySend('Page.addScriptToEvaluateOnNewDocument', {
      source: HARDENING_SCRIPT,
      runImmediately: true,
      worldName: 'MAIN',
    }, sessionId);
  });

  // Cover documents that are already loaded.
  try {
    const { targetInfos } = await connection.send('Target.getTargets');
    for (const target of targetInfos) {
      if (target.type !== 'page') continue;
      const { sessionId } = await connection.send('Target.attachToTarget', {
        targetId: target.targetId,
        flatten: true,
      });
      await connection.trySend('Page.addScriptToEvaluateOnNewDocument', {
        source: HARDENING_SCRIPT,
        runImmediately: true,
        worldName: 'MAIN',
      }, sessionId);
      await connection.trySend('Runtime.evaluate', {
        expression: HARDENING_SCRIPT,
      }, sessionId);
    }
  } catch { /* best effort */ }

  return true;
}

/**
 * Browser launch flags that reduce automation fingerprinting at the source.
 * These are only meaningful when launching Chromium yourself.
 */
export const STEALTH_FLAGS = [
  // Do not set navigator.webdriver = true.
  '--disable-blink-features=AutomationControlled',
  // Avoid the automation infobar and the default automation extension.
  '--disable-features=AutomationControlled',
  // Do not advertise "Chrome for Testing".
  '--disable-component-extensions-with-background-pages',
];
