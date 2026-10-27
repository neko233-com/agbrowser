/**
 * Network-level ad blocking.
 *
 * Two tiers, chosen so the common case stays free:
 *
 *  Tier 1 - native blocking via `Network.setBlockedURLs`.
 *    Chromium matches these inside the network stack, so a blocked request
 *    never reaches our JS. Zero per-request overhead.
 *
 *  Tier 2 - CDP `Fetch` interception.
 *    Only enabled when the rule set contains rules that cannot be expressed
 *    as a blocked-URL wildcard (regex rules, path-specific rules,
 *    resource-type filters, allow/exceptions).
 *
 * Cosmetic filtering is applied by injecting a stylesheet per document.
 */
import { RuleSet } from './rules.js';

export class AdBlocker {
  /**
   * @param {object} opts
   * @param {RuleSet} [opts.ruleSet]
   * @param {boolean} [opts.enabled]
   */
  constructor({ ruleSet, enabled = true } = {}) {
    this.ruleSet = ruleSet ?? new RuleSet();
    this.enabled = enabled;
    this.stats = {
      blocked: 0,
      allowed: 0,
      cosmeticApplied: 0,
      byRule: new Map(),
    };
    /** @type {Map<string, boolean>} native patterns -> enabled */
    this._nativePatterns = new Set();
    this._needsFetch = false;
    this._analyze();
  }

  _analyze() {
    for (const rule of this.ruleSet.networkRules) {
      if (rule.kind === 'allow' || rule.isRegex || rule.path || rule.query ||
          rule.resourceType || rule.thirdParty || rule.firstParty) {
        this._needsFetch = true;
        continue;
      }
      if (rule.isDomain && rule.domain) {
        // Never put a browser-internal host into the native blocked list:
        // that list has no exception mechanism.
        if (isBrowserInternal(`https://${rule.domain}/`)) continue;
        // ||example.com^  ->  *://*.example.com/*
        const pat = `*://*.${rule.domain}/*`;
        this._nativePatterns.add(pat);
      }
    }
  }

  /** True when the rule set needs CDP request interception. */
  get requiresFetch() {
    return this._needsFetch;
  }

  /**
   * Attach the blocker to a connected browser session.
   * @param {import('../browser/launcher.js').BrowserSession} session
   */
  async attach(session) {
    const { connection } = session;

    // Tier 1: native blocked URL list, applied to every target.
    if (this.enabled && this._nativePatterns.size > 0) {
      await connection.trySend('Network.setBlockedURLs', {
        urls: [...this._nativePatterns],
      });
    }

    if (this.enabled && this._needsFetch) {
      await connection.trySend('Fetch.enable', {
        patterns: [{ urlPattern: '*', requestStage: 'Request' }],
      });

      connection.on('Fetch.requestPaused', async (params) => {
        const { requestId, request } = params;
        try {
          // Browser-internal traffic is never blocked.
          if (isBrowserInternal(request.url)) {
            await connection.send('Fetch.continueRequest', { requestId });
            return;
          }
          const result = this.match(request.url, {
            resourceType: mapResourceType(params.resourceType),
            referrer: request.referrer || undefined,
          });
          if (result.blocked) {
            this.stats.blocked++;
            this.#countRule(result.rule);
            await connection.send('Fetch.failRequest', {
              requestId,
              errorReason: 'BlockedByClient',
            });
          } else {
            this.stats.allowed++;
            await connection.send('Fetch.continueRequest', { requestId });
          }
        } catch {
          // Never let a blocking error stall the page.
          await connection.trySend('Fetch.continueRequest', { requestId });
        }
      });
    }

    return this;
  }

  /**
   * Match a URL. Tier-2 rules are evaluated here; tier-1 patterns are
   * already handled natively by Chromium.
   */
  match(url, opts = {}) {
    if (!this.enabled) return { blocked: false, rule: null };

    // Never touch browser-internal resources: extensions, the Web Store,
    // devtools and other chrome:// surfaces are part of the browser itself.
    // Blocking them would break the extension pack and the DevTools UI.
    if (isBrowserInternal(url)) return { blocked: false, rule: null };

    // A native pattern hit means Chromium already blocked it; report it so
    // counters stay meaningful, without double-blocking.
    const nativeHit = this._nativePatterns.size > 0 && matchesNative(url, this._nativePatterns);
    if (nativeHit) {
      const result = this.ruleSet.match(url, opts);
      if (!result.blocked) return { blocked: true, rule: 'native:blocklist' };
    }

    const result = this.ruleSet.match(url, opts);
    if (result.blocked) return result;

    if (nativeHit) return { blocked: true, rule: 'native:blocklist' };
    return { blocked: false, rule: null };
  }

  #countRule(rule) {
    const key = rule ?? 'unknown';
    this.stats.byRule.set(key, (this.stats.byRule.get(key) ?? 0) + 1);
  }

  /**
   * CSS to hide cosmetic-rule elements on a given hostname.
   * @param {string} hostname
   */
  cosmeticCss(hostname) {
    const selectors = this.ruleSet.cosmeticFor(hostname);
    if (selectors.length === 0) return null;
    this.stats.cosmeticApplied++;
    const css = selectors.map((s) => `${s}{display:none!important}`).join('\n');
    return `${css}\n`;
  }

  /** Serializable snapshot for the console UI / stats reporting. */
  report() {
    const top = [...this.stats.byRule.entries()]
      .sort((a, b) => b[1] - a[1])
      .slice(0, 20)
      .map(([rule, count]) => ({ rule, count }));
    return {
      enabled: this.enabled,
      blocked: this.stats.blocked,
      allowed: this.stats.allowed,
      cosmeticApplied: this.stats.cosmeticApplied,
      ruleCount: this.ruleSet.size,
      nativePatterns: this._nativePatterns.size,
      requiresFetch: this._needsFetch,
      topRules: top,
    };
  }
}

function matchesNative(url, patterns) {
  for (const pattern of patterns) {
    if (wildcardMatch(url, pattern)) return true;
  }
  return false;
}

/**
 * Browser-internal schemes that must never be blocked.
 *
 * Extensions live on chrome-extension://, the Web Store on chromewebstore.google.com,
 * and the DevTools UI on devtools://. Blocking any of them breaks the
 * browser's own features.
 */
const INTERNAL_SCHEMES = [
  'chrome-extension:',
  'chrome-untrusted:',
  'chrome-search:',
  'chrome:',
  'devtools:',
  'chrome-error:',
  'chrome-native:',
  'chrome-web-store:',
  'about:',
  'blob:',
  'data:',
  'file:',
  'view-source:',
];

/** Internal hosts that must always stay reachable. */
const INTERNAL_HOSTS = [
  'chromewebstore.google.com',
  'clients2.google.com',
];

/**
 * @param {string} url
 * @returns {boolean} true when the URL belongs to the browser itself
 */
export function isBrowserInternal(url) {
  const lower = String(url ?? '').toLowerCase();
  for (const scheme of INTERNAL_SCHEMES) {
    if (lower.startsWith(scheme)) return true;
  }
  for (const host of INTERNAL_HOSTS) {
    if (lower.includes(`://${host}/`) || lower.includes(`://${host}?`)) return true;
  }
  return false;
}

/** Chrome's blocked-URL patterns support a single leading/trailing '*'. */
function wildcardMatch(url, pattern) {
  const idx = pattern.indexOf('*');
  if (idx === -1) return url === pattern;
  const prefix = pattern.slice(0, idx);
  const suffix = pattern.slice(idx + 1);
  return url.startsWith(prefix) && url.endsWith(suffix) && url.length >= prefix.length + suffix.length;
}

function mapResourceType(cdpType) {
  switch (cdpType) {
    case 'Document': return 'document';
    case 'Stylesheet': return 'stylesheet';
    case 'Image': return 'image';
    case 'Media': return 'media';
    case 'Font': return 'font';
    case 'Script': return 'script';
    case 'XHR': return 'xmlhttprequest';
    case 'Fetch': return 'xmlhttprequest';
    case 'WebSocket': return 'websocket';
    default: return 'other';
  }
}
