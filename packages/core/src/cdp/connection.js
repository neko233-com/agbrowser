/**
 * Zero-dependency Chrome DevTools Protocol connection.
 *
 * Uses Node 24's built-in WebSocket (undici). Node 22's implementation caps
 * inbound messages at ~4MB and stalls on larger frames, which breaks
 * screenshot/a11y payloads, so Node >= 24 is a hard requirement.
 */
import { EventEmitter } from 'node:events';

export class CdpError extends Error {
  constructor(message, { code, data } = {}) {
    super(message);
    this.name = 'CdpError';
    this.code = code;
    this.data = data;
  }
}

/**
 * A single multiplexed CDP socket. Sessions (attached targets) are addressed
 * with the flat protocol: `Target.attachToTarget { flatten: true }` returns a
 * sessionId that we pass through on every subsequent command.
 */
export class CdpConnection extends EventEmitter {
  #ws = null;
  #nextId = 0;
  #pending = new Map();
  #closed = false;
  #url;
  /**
   * Bound listener reference so it can be detached on close.
   *
   * The WebSocket 'message' event delivers a MessageEvent; the payload lives
   * in `event.data`, so the unwrapping happens here.
   */
  #onMessageBound = (event) => this.#onMessage(event?.data);

  constructor(url) {
    super();
    this.setMaxListeners(0);
    this.#url = url;
  }

  get url() {
    return this.#url;
  }

  get closed() {
    return this.#closed;
  }

  async connect({ timeout = 15_000 } = {}) {
    if (this.#ws) return this;
    const ws = new WebSocket(this.#url);
    this.#ws = ws;

    await new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        reject(new CdpError(`CDP connect timed out after ${timeout}ms`));
        try {
          ws.close();
        } catch {
          /* ignore */
        }
      }, timeout);

      ws.addEventListener('open', () => {
        clearTimeout(timer);
        resolve();
      }, { once: true });

      ws.addEventListener('error', (event) => {
        clearTimeout(timer);
        reject(new CdpError(`CDP socket error: ${event?.message ?? 'unknown'}`));
      }, { once: true });
    });

    ws.addEventListener('message', this.#onMessageBound);
    ws.addEventListener('close', () => this.#onClose('socket closed'));
    ws.addEventListener('error', () => this.#onClose('socket error'));

    return this;
  }

  #onMessage(raw) {
    let msg;
    try {
      msg = JSON.parse(typeof raw === 'string' ? raw : raw.toString('utf8'));
    } catch (err) {
      this.emit('protocolError', err);
      return;
    }

    if (msg.id !== undefined) {
      const entry = this.#pending.get(msg.id);
      if (!entry) return;
      this.#pending.delete(msg.id);
      if (msg.error) {
        entry.reject(new CdpError(`${msg.error.message}${msg.error.data ? ` (${msg.error.data})` : ''}`, {
          code: msg.error.code,
          data: msg.error.data,
        }));
      } else {
        entry.resolve(msg.result ?? {});
      }
      return;
    }

    if (msg.method) {
      // Flat-session events carry the sessionId we attached with.
      this.emit(msg.method, msg.params ?? {}, msg.sessionId);
      this.emit('*', msg.method, msg.params ?? {}, msg.sessionId);
    }
  }

  #onClose(reason) {
    if (this.#closed) return;
    this.#closed = true;
    for (const [, entry] of this.#pending) {
      entry.reject(new CdpError(`CDP connection closed: ${reason}`));
    }
    this.#pending.clear();
    this.emit('close', reason);
  }

  /**
   * Send a CDP command.
   * @param {string} method e.g. "Page.navigate"
   * @param {object} [params]
   * @param {string} [sessionId] flat session id for target-scoped commands
   * @param {number} [timeout] ms; commands that never answer would otherwise hang forever
   */
  send(method, params = {}, sessionId = undefined, timeout = 30_000) {
    if (this.#closed) {
      return Promise.reject(new CdpError(`CDP connection is closed (${method})`));
    }
    const id = ++this.#nextId;
    const payload = { id, method, params };
    if (sessionId) payload.sessionId = sessionId;

    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        this.#pending.delete(id);
        reject(new CdpError(`CDP command timed out after ${timeout}ms: ${method}`));
      }, timeout);

      this.#pending.set(id, {
        resolve: (v) => {
          clearTimeout(timer);
          resolve(v);
        },
        reject: (e) => {
          clearTimeout(timer);
          reject(e);
        },
      });

      try {
        this.#ws.send(JSON.stringify(payload));
      } catch (err) {
        clearTimeout(timer);
        this.#pending.delete(id);
        reject(new CdpError(`Failed to send ${method}: ${err.message}`));
      }
    });
  }

  /** Convenience: send and swallow protocol errors, used for best-effort enables. */
  async trySend(method, params = {}, sessionId = undefined) {
    try {
      return await this.send(method, params, sessionId);
    } catch {
      return null;
    }
  }

  close() {
    if (this.#closed) return;
    this.#onClose('closed by client');
    try {
      // Detach the listeners before closing so that the socket's pending
      // close handshake does not outlive the event loop.
      this.#ws?.removeEventListener?.('message', this.#onMessageBound);
      this.#ws?.close();
    } catch {
      /* ignore */
    }
    this.#ws = null;
  }
}

/**
 * Discover the DevTools endpoint of a running browser.
 * @param {string} port
 * @param {string} [host]
 */
export async function discoverEndpoint(port, host = '127.0.0.1') {
  const res = await fetch(`http://${host}:${port}/json/version`);
  if (!res.ok) throw new CdpError(`DevTools endpoint returned HTTP ${res.status}`);
  return res.json();
}

/** Wait until a DevTools endpoint answers, polling `intervalMs`. */
export async function waitForEndpoint(port, { host = '127.0.0.1', timeout = 30_000, intervalMs = 120 } = {}) {
  const deadline = Date.now() + timeout;
  let lastError = null;
  while (Date.now() < deadline) {
    try {
      return await discoverEndpoint(port, host);
    } catch (err) {
      lastError = err;
      await new Promise((r) => setTimeout(r, intervalMs));
    }
  }
  throw new CdpError(`DevTools endpoint on port ${port} never came up: ${lastError?.message ?? 'timeout'}`);
}
