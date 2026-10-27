/**
 * Game engine support for agent-driven web game development.
 *
 * A canvas-based game is invisible to the accessibility tree: there are no
 * buttons, no text, no roles. The only things an agent can work with are
 * pixels, the frame loop, and whatever state the game exposes on the
 * JavaScript objects it keeps.
 *
 * This module provides the primitives an agent needs to actually build and
 * debug a web game:
 *
 *   - detect what the page is running (DOM game, Canvas 2D, WebGL, WebGPU)
 *   - read the live state out of the game's own objects
 *   - take control of the frame loop: pause, step one frame at a time
 *   - measure the frame rate and the long frames that cause jank
 *   - drive the game with precise pointer and keyboard input
 *
 * Everything runs inside the page, so the game sees a completely normal
 * session.
 */

/**
 * Injected into the page. It installs a small harness the agent can then
 * talk to. Kept as one expression so it cannot fail to load.
 */
export const GAME_HARNESS = `
(() => {
  if (window.__agbGame) return;

  const state = {
    installedAt: Date.now(),
    frames: 0,
    paused: false,
    stepQueue: 0,
    frameTimes: [],
    longFrames: [],
    lastFrameAt: 0,
    observers: [],
    originalRaf: window.requestAnimationFrame.bind(window),
    originalCancel: window.cancelAnimationFrame.bind(window),
    hooked: false,
  };

  // Wrap requestAnimationFrame so every frame is counted and timed.
  //
  // Pausing has to keep the game's own rAF chain intact, otherwise the game
  // never registers its next frame and the loop dies. The wrapper therefore
  // always invokes the callback, but while paused it hands the game a frozen
  // timestamp and a zero delta, so the world does not advance even though the
  // loop keeps turning.
  //
  // Stepping releases a fixed number of frames with the real clock.
  function hookRaf() {
    if (state.hooked) return;
    state.hooked = true;

    const realNow = performance.now.bind(performance);
    state.realNow = realNow;
    state.frozenTime = null;

    window.requestAnimationFrame = (cb) => {
      return state.originalRaf((t) => {
        const now = realNow();
        if (state.lastFrameAt) {
          const dt = now - state.lastFrameAt;
          state.frameTimes.push(dt);
          if (state.frameTimes.length > 600) state.frameTimes.shift();
          if (dt > 32) {
            state.longFrames.push({ t: now, dt: Math.round(dt * 100) / 100 });
            if (state.longFrames.length > 100) state.longFrames.shift();
          }
        }
        state.lastFrameAt = now;

        const stepping = state.stepQueue > 0;
        const held = state.paused && !stepping;

        if (stepping) {
          state.stepQueue--;
          state.frames++;
          try { cb(t); } catch (e) { /* keep the loop alive */ }
          return;
        }

        if (held) {
          // Freeze the clock for the game so its own delta-based logic does
          // not advance, while still letting it register the next frame.
          state.frozenTime = state.frozenTime ?? t;
          try { cb(state.frozenTime); } catch (e) { /* keep the loop alive */ }
          return;
        }

        state.frozenTime = null;
        state.frames++;
        try { cb(t); } catch (e) { /* keep the loop alive */ }
      });
    };
  }

  // Find the canvases and the rendering context behind them.
  function describeCanvases() {
    const out = [];
    for (const canvas of document.querySelectorAll('canvas')) {
      const rect = canvas.getBoundingClientRect();
      const info = {
        width: canvas.width,
        height: canvas.height,
        cssWidth: Math.round(rect.width),
        cssHeight: Math.round(rect.height),
        left: Math.round(rect.left),
        top: Math.round(rect.top),
        context: 'unknown',
        webglVersion: null,
        renderer: null,
        vendor: null,
        visible: rect.width > 0 && rect.height > 0,
      };
      try {
        const gl = canvas.getContext('webgl2') || canvas.getContext('webgl');
        if (gl) {
          info.context = 'webgl';
          const dbg = gl.getExtension('WEBGL_debug_renderer_info');
          if (dbg) {
            info.renderer = gl.getParameter(dbg.UNMASKED_RENDERER_WEBGL);
            info.vendor = gl.getParameter(dbg.UNMASKED_VENDOR_WEBGL);
          }
          info.webglVersion = gl.getParameter(gl.VERSION);
        } else if (canvas.getContext('2d')) {
          info.context = '2d';
        }
      } catch (e) { /* context already taken */ }
      out.push(info);
    }
    return out;
  }

  // Find game-like objects on the window. Games conventionally keep their
  // world, player and entity collections on a global.
  function describeGlobals() {
    const interesting = ['game','Game','world','World','state','State','scene','Scene',
      'level','Level','player','Player','entity','entities','sprite','sprites',
      'engine','Engine','phaser','THREE','PIXI','kaboom','Crafty','Phaser'];
    const found = {};
    for (const key of interesting) {
      try {
        const v = window[key];
        if (v === undefined || v === null) continue;
        const type = Array.isArray(v) ? 'array' : typeof v;
        let size = null;
        if (Array.isArray(v)) size = v.length;
        else if (v && typeof v === 'object' && typeof v.constructor === 'function') {
          try { size = Object.keys(v).length; } catch (e) {}
        }
        found[key] = { type, size };
      } catch (e) { /* cross-origin getter */ }
    }
    return found;
  }

  // Look for the usual engine markers.
  function detectEngines() {
    const engines = [];
    if (window.Phaser) engines.push({ name: 'Phaser', version: window.Phaser.VERSION ?? null });
    if (window.THREE) engines.push({ name: 'Three.js', version: window.THREE.REVISION ?? null });
    if (window.PIXI) engines.push({ name: 'PixiJS', version: window.PIXI.VERSION ?? null });
    if (window.Crafty) engines.push({ name: 'Crafty', version: null });
    if (window.Kaboom) engines.push({ name: 'Kaboom', version: window.Kaboom.VERSION ?? null });
    if (window.p5) engines.push({ name: 'p5.js', version: window.p5.VERSION ?? null });
    if (window.Babylon) engines.push({ name: 'Babylon.js', version: null });
    return engines;
  }

  function fpsStats() {
    const times = state.frameTimes;
    if (!times.length) return { fps: 0, frames: 0, avg: 0, p95: 0, worst: 0, longFrames: state.longFrames.length };
    const sorted = [...times].sort((a, b) => a - b);
    const avg = times.reduce((a, b) => a + b, 0) / times.length;
    const p95 = sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * 0.95))];
    const worst = sorted[sorted.length - 1];
    return {
      fps: Math.round((1000 / avg) * 100) / 100,
      frames: state.frames,
      avg: Math.round(avg * 100) / 100,
      p95: Math.round(p95 * 100) / 100,
      worst: Math.round(worst * 100) / 100,
      longFrames: state.longFrames.length,
    };
  }

  window.__agbGame = {
    state,
    hookRaf,
    describeCanvases,
    describeGlobals,
    detectEngines,
    fpsStats,
    pause() { state.paused = true; },
    resume() { state.paused = false; },
    /** Queue n frames to run while paused. */
    step(n = 1) { state.stepQueue += n; return state.stepQueue; },
    isPaused() { return state.paused; },
    reset() {
      state.frameTimes.length = 0;
      state.longFrames.length = 0;
      state.frames = 0;
      state.lastFrameAt = 0;
    },
    /** Sample a rectangle of the canvas as raw pixels. */
    samplePixels(x, y, w, h) {
      const canvas = document.querySelector('canvas');
      if (!canvas) return null;
      const ctx = canvas.getContext('2d');
      if (!ctx) return null;
      const data = ctx.getImageData(x, y, w, h).data;
      let sum = 0;
      let nonBlack = 0;
      for (let i = 0; i < data.length; i += 4) {
        const v = (data[i] + data[i + 1] + data[i + 2]) / 3;
        sum += v;
        if (v > 8) nonBlack++;
      }
      return {
        x, y, w, h,
        averageLuma: Math.round((sum / (data.length / 4)) * 100) / 100,
        nonBlackRatio: Math.round((nonBlack / (data.length / 4)) * 10000) / 10000,
      };
    },
  };

  // Take over the frame loop right away. A game that starts its loop before
  // the harness is installed would otherwise never be counted, and the agent
  // could not pause it.
  hookRaf();
})();
`;

/**
 * Game-aware page controller.
 *
 * Extends the regular page controller with the game primitives. The harness
 * is installed into the page before any navigation so it is present from the
 * very first frame.
 */
export class GameController {
  /**
   * @param {object} opts
   * @param {import('../snapshot/page.js').PageController} opts.page
   */
  constructor({ page }) {
    this.page = page;
    this._installed = false;
  }

  /** Install the harness into the current document. */
  async install() {
    if (this._installed) return true;
    await this.page.attach();

    // Note: no `worldName` here. A script registered for a named world is
    // not injected into the page's own world after a navigation, so the
    // harness would silently disappear on the first navigation.
    await this.page._send('Page.addScriptToEvaluateOnNewDocument', {
      source: GAME_HARNESS,
      runImmediately: true,
    });

    // Also apply it to the document that is already loaded.
    await this.page._send('Runtime.evaluate', { expression: GAME_HARNESS })
      .catch(() => { /* about:blank and friends */ });

    this._installed = true;
    return true;
  }

  /** Call a method on the harness inside the page. */
  async _call(method, ...args) {
    await this.install();
    const expression = `(() => {
      const g = window.__agbGame;
      if (!g) return null;
      const fn = g[${JSON.stringify(method)}];
      if (typeof fn !== 'function') return null;
      return fn(...${JSON.stringify(args)});
    })()`;
    const value = await this.page.evaluate(expression);
    return value;
  }

  /**
   * Describe what kind of game is running.
   * @returns {Promise<object>}
   */
  async detect() {
    await this.install();
    const info = await this.page.evaluate(`(() => {
      const g = window.__agbGame;
      if (!g) return null;
      const canvases = g.describeCanvases();
      const engines = g.detectEngines();
      const globals = g.describeGlobals();
      const hasWebgpu = !!navigator.gpu;
      const audio = !!window.AudioContext || !!window.webkitAudioContext;
      const storage = {
        localStorage: (() => { try { return localStorage.length; } catch (e) { return -1; } })(),
        indexedDB: !!window.indexedDB,
        webSocket: !!window.WebSocket,
      };
      return {
        canvases,
        engines,
        globals,
        hasWebgpu,
        audio,
        storage,
        isGame: canvases.length > 0 || engines.length > 0,
      };
    })()`);
    return info ?? { isGame: false, canvases: [], engines: [], globals: {} };
  }

  /**
   * Read the live state of the game.
   *
   * The expression is evaluated in the page, so it can read whatever the game
   * keeps on its globals.
   *
   * @param {string} [expression] a JS expression returning the state
   */
  async state(expression = null) {
    await this.install();
    if (expression) {
      return this.page.evaluate(expression);
    }
    return this.page.evaluate(`(() => {
      const g = window.__agbGame;
      if (!g) return null;
      const out = { fps: g.fpsStats(), globals: g.describeGlobals(), canvases: g.describeCanvases() };

      // A snapshot helper that keeps primitives and describes containers, so
      // an agent can read the game state without knowing its shape.
      const snapshot = (value, depth) => {
        if (value === null || value === undefined) return value ?? null;
        const t = typeof value;
        if (t === 'number' || t === 'string' || t === 'boolean') return value;
        if (t === 'function') return '[function]';
        if (Array.isArray(value)) {
          return { type: 'array', length: value.length,
                   sample: depth > 0 ? value.slice(0, 5).map(v => snapshot(v, depth - 1)) : undefined };
        }
        if (t === 'object') {
          if (depth <= 0) return { type: 'object' };
          const out2 = {};
          for (const k of Object.keys(value).slice(0, 40)) {
            try { out2[k] = snapshot(value[k], depth - 1); } catch (e) { out2[k] = '[error]'; }
          }
          return out2;
        }
        return null;
      };

      // Surface the common game objects when they look like state bags.
      for (const key of ['game','Game','state','State','world','World','scene','Scene','level','Level']) {
        try {
          const v = window[key];
          if (v && typeof v === 'object') {
            out[key] = snapshot(v, 3);
          } else if (typeof v === 'number' || typeof v === 'string' || typeof v === 'boolean') {
            out[key] = v;
          }
        } catch (e) { /* cross-origin */ }
      }
      return out;
    })()`);
  }

  /** Pause the frame loop. */
  async pause() {
    return this._call('pause');
  }

  /** Resume the frame loop. */
  async resume() {
    return this._call('resume');
  }

  /**
   * Advance the game by n frames while paused.
   * @param {number} [frames]
   */
  async step(frames = 1) {
    return this._call('step', frames);
  }

  /** Frame rate statistics. */
  async fps() {
    return this._call('fpsStats');
  }

  /** Reset the collected frame statistics. */
  async resetStats() {
    return this._call('reset');
  }

  /**
   * Sample a region of the canvas and report its brightness, which is a
   * cheap way to tell whether the game is actually rendering.
   *
   * @param {object} [opts]
   * @param {number} [opts.x]
   * @param {number} [opts.y]
   * @param {number} [opts.width]
   * @param {number} [opts.height]
   */
  async sample({ x = 0, y = 0, width = 64, height = 64 } = {}) {
    return this._call('samplePixels', x, y, width, height);
  }

  /**
   * Send a pointer event at viewport coordinates.
   *
   * Games listen for pointer events rather than the mouse events the regular
   * click helper dispatches, so the game path uses the richer event set.
   *
   * @param {object} opts
   * @param {number} opts.x
   * @param {number} opts.y
   * @param {string} [opts.type] pointerdown | pointermove | pointerup | click
   * @param {string|number} [opts.button] 0 = left, 1 = middle, 2 = right
   * @param {number} [opts.buttons]
   * @param {number} [opts.clickCount]
   */
  async pointer({ x, y, type = 'pointerdown', button = 0, buttons = 1, clickCount = 1 }) {
    await this.page.attach();

    // The DevTools protocol names the buttons; "none" is used while moving.
    const buttonName = button === 'left' ? 'left'
      : button === 'right' ? 'right'
        : button === 'middle' ? 'middle'
          : button === 2 ? 'right'
            : button === 1 ? 'middle' : 'left';

    const common = {
      x, y,
      button: type === 'pointermove' ? 'none' : buttonName,
      buttons,
      clickCount,
      pointerType: 'mouse',
      isPrimary: true,
      force: 1,
      tangentialPressure: 0,
      tiltX: 0,
      tiltY: 0,
      twist: 0,
      deltaX: 0,
      deltaY: 0,
    };

    await this.page._send('Input.dispatchMouseEvent', {
      ...common,
      type: type === 'pointerdown' ? 'mousePressed'
        : type === 'pointerup' || type === 'click' ? 'mouseReleased'
          : 'mouseMoved',
    });

    // Deliver the matching pointer event so PointerEvent listeners fire.
    await this.page._send('Runtime.evaluate', {
      expression: `(() => {
        const el = document.elementFromPoint(${x}, ${y}) || document.body;
        const ev = new PointerEvent(${JSON.stringify(type)}, {
          clientX: ${x}, clientY: ${y},
          button: ${buttonName === 'left' ? 0 : buttonName === 'middle' ? 1 : 2},
          buttons: ${buttons},
          pointerId: 1, pointerType: 'mouse', isPrimary: true,
          bubbles: true, cancelable: true, view: window,
        });
        el.dispatchEvent(ev);
        return true;
      })()`,
    }).catch(() => { /* the mouse event already carried the load */ });

    return { dispatched: type, x, y };
  }

  /**
   * Press and release a key with a realistic repeat pattern.
   * @param {string} key
   * @param {object} [opts]
   * @param {number} [opts.repeat]
   */
  async key(key, { repeat = 1 } = {}) {
    for (let i = 0; i < repeat; i++) {
      await this.page.press(key);
    }
    return { key, repeat };
  }
}

export { GAME_HARNESS as gameHarness };
