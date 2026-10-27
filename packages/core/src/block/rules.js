/**
 * ABP-style ad blocking rule engine.
 *
 * Pure JS, zero dependencies. Two rule kinds:
 *  - network rules  -> matched against the request URL
 *  - cosmetic rules -> CSS selectors injected into matching pages
 *
 * Rules are parsed from EasyList-compatible syntax so existing filter lists
 * can be reused, but the engine only implements the subset that actually
 * matters for blocking (no scriptlets, no redirect resources).
 */

// ---------------------------------------------------------------------------
// Rule parsing
// ---------------------------------------------------------------------------

/**
 * @typedef {object} NetworkRule
 * @property {'block'|'allow'} kind
 * @property {boolean} isPattern  regex-based rule
 * @property {string} source      raw source text
 * @property {boolean} isRegex
 * @property {RegExp|null} regex
 * @property {boolean} isDomain   ||domain^
 * @property {string|null} domain
 * @property {boolean} includeSubdomains
 * @property {string|null} path
 * @property {string|null} query
 * @property {string|null} method
 */

/**
 * Parse a single ABP network rule.
 * @param {string} line
 * @returns {NetworkRule|null}
 */
export function parseNetworkRule(line) {
  let src = line.trim();
  if (!src || src.startsWith('!')) return null;
  // Comment / metadata lines
  if (src.startsWith('[')) return null;

  let kind = 'block';
  if (src.startsWith('@@')) {
    kind = 'allow';
    src = src.slice(2);
  }

  // Options come after the '$' separator, e.g. "||tracker.com^$script,third-party".
  // The separator must be located after the last '$' so that a ',' inside the
  // pattern itself is not mistaken for an option separator.
  let options = null;
  let pattern = src;
  const dollar = src.indexOf('$');
  if (dollar !== -1) {
    const maybeOptions = src.slice(dollar + 1);
    // Note: '-', '|' and ',' must all come first in the character class,
    // otherwise they are read as range operators or are rejected, and options
    // such as "script,third-party" fail to match.
    if (/^[-|,a-z0-9_$.~]+$/i.test(maybeOptions)) {
      options = maybeOptions.split(',').map((o) => o.toLowerCase());
      pattern = src.slice(0, dollar);
    }
  }

  const rule = {
    kind,
    isPattern: false,
    source: src,
    isRegex: false,
    regex: null,
    isDomain: false,
    domain: null,
    includeSubdomains: false,
    path: null,
    query: null,
    method: null,
  };

  // Regex rule: /pattern/flags
  if (pattern.length > 1 && pattern.startsWith('/') && pattern.lastIndexOf('/') > 0) {
    const end = pattern.lastIndexOf('/');
    const body = pattern.slice(1, end);
    const flags = pattern.slice(end + 1);
    try {
      rule.isRegex = true;
      rule.isPattern = true;
      rule.regex = new RegExp(body, flags.replace(/[^gimsuy]/g, ''));
    } catch {
      return null;
    }
    applyOptions(rule, options);
    return rule;
  }

  // Domain anchor: ||example.com^ or ||example.com/path
  if (pattern.startsWith('||')) {
    rule.isPattern = true;
    rule.isDomain = true;
    let rest = pattern.slice(2);
    const caret = rest.indexOf('^');
    if (caret !== -1) {
      rest = rest.slice(0, caret);
      rule.includeSubdomains = true;
    }
    const slash = rest.indexOf('/');
    if (slash !== -1) {
      rule.domain = rest.slice(0, slash).toLowerCase();
      rule.path = rest.slice(slash);
    } else {
      rule.domain = rest.toLowerCase();
    }
    applyOptions(rule, options);
    return rule;
  }

  // Plain substring rule
  if (pattern.length > 0) {
    rule.isPattern = true;
    rule.path = pattern;
    applyOptions(rule, options);
    return rule;
  }

  return null;
}

function applyOptions(rule, options) {
  if (!options) return;
  for (const opt of options) {
    if (opt.startsWith('domain=')) {
      rule.domain = opt.slice(7);
      rule.includeSubdomains = true;
    } else if (opt === 'third-party' || opt === '3p') {
      rule.thirdParty = true;
    } else if (opt === 'first-party' || opt === '1p') {
      rule.firstParty = true;
    } else if (opt === 'script' || opt === 'image' || opt === 'stylesheet' ||
               opt === 'xmlhttprequest' || opt === 'subdocument' || opt === 'media' ||
               opt === 'font' || opt === 'websocket' || opt === 'other') {
      rule.resourceType = opt;
    } else if (opt.startsWith('~')) {
      rule.excluded = (rule.excluded ?? []).concat(opt.slice(1).split('|'));
    } else if (opt === 'popup' || opt === 'document') {
      rule.resourceType = opt;
    }
  }
}

/**
 * Parse a cosmetic rule: `example.com##.ad-banner` or `##.generic` (global).
 * @param {string} line
 */
export function parseCosmeticRule(line) {
  const src = line.trim();
  if (!src || src.startsWith('!') || src.startsWith('[')) return null;
  const idx = src.indexOf('##');
  if (idx === -1) return null;

  const domainPart = src.slice(0, idx);
  const selector = src.slice(idx + 2);
  if (!selector) return null;

  let includeSubdomains = false;
  let domains = [];
  if (domainPart) {
    includeSubdomains = domainPart.startsWith('~') ? false : true;
    domains = domainPart
      .split(',')
      .map((d) => d.trim().replace(/^~/, '').replace(/^\|/, '').toLowerCase())
      .filter(Boolean);
  }

  return { domains, includeSubdomains, selector, source: src };
}

// ---------------------------------------------------------------------------
// Rule set
// ---------------------------------------------------------------------------

export class RuleSet {
  constructor() {
    /** @type {NetworkRule[]} */
    this.networkRules = [];
    /** @type {Array<{domains:string[],includeSubdomains:boolean,selector:string}>} */
    this.cosmeticRules = [];
    /** @type {Map<string, NetworkRule[]>} */
    this._domainIndex = new Map();
    this._genericRules = [];
    this._cosmeticByDomain = new Map();
    this._cosmeticGeneric = [];
    this.stats = { lines: 0, network: 0, cosmetic: 0, skipped: 0 };
  }

  get size() {
    return this.networkRules.length + this.cosmeticRules.length;
  }

  /**
   * Parse a filter list file.
   * @param {string} content
   * @param {string} [origin] label used in stats
   */
  parse(content) {
    const lines = content.split(/\r?\n/);
    for (const raw of lines) {
      const line = raw.trim();
      if (!line) continue;
      this.stats.lines++;
      const cosmetic = parseCosmeticRule(line);
      if (cosmetic) {
        this.cosmeticRules.push(cosmetic);
        this.stats.cosmetic++;
        this._indexCosmetic(cosmetic);
        continue;
      }
      const net = parseNetworkRule(line);
      if (net) {
        this.networkRules.push(net);
        this.stats.network++;
        this._indexNetwork(net);
        continue;
      }
      this.stats.skipped++;
    }
    return this;
  }

  _indexNetwork(rule) {
    if (rule.isDomain && rule.domain && !rule.path && !rule.query) {
      const list = this._domainIndex.get(rule.domain) ?? [];
      list.push(rule);
      this._domainIndex.set(rule.domain, list);
    } else {
      this._genericRules.push(rule);
    }
  }

  _indexCosmetic(rule) {
    if (rule.domains.length === 0) {
      this._cosmeticGeneric.push(rule);
      return;
    }
    for (const d of rule.domains) {
      const list = this._cosmeticByDomain.get(d) ?? [];
      list.push(rule);
      this._cosmeticByDomain.set(d, list);
    }
  }

  /**
   * Match a URL against the network rules.
   * @param {string} url
   * @param {object} [opts]
   * @param {string} [opts.resourceType]
   * @param {string} [opts.referrer] for first/third-party classification
   * @returns {{blocked:boolean, rule:string|null}}
   */
  match(url, { resourceType, referrer } = {}) {
    let parsed;
    try {
      parsed = new URL(url);
    } catch {
      return { blocked: false, rule: null };
    }

    const host = parsed.hostname.toLowerCase();
    const isThirdParty = computeThirdParty(parsed, referrer);

    // 1. Domain-indexed rules: exact host + parent-domain variants.
    const candidates = domainCandidates(host);
    let blockedRule = null;

    for (const candidate of candidates) {
      const rules = this._domainIndex.get(candidate);
      if (!rules) continue;
      for (const rule of rules) {
        if (rule.kind === 'allow') {
          // Allow rule wins over block rules for the same domain.
          return { blocked: false, rule: rule.source };
        }
        if (rule.excluded?.some((ex) => host === ex || host.endsWith(`.${ex}`))) continue;
        if (rule.thirdParty && !isThirdParty) continue;
        if (rule.firstParty && isThirdParty) continue;
        if (rule.resourceType && resourceType && rule.resourceType !== resourceType) continue;
        blockedRule = rule.source;
        break;
      }
      if (blockedRule) break;
    }

    // 2. Generic rules (substring / regex / anchored with path).
    if (!blockedRule) {
      for (const rule of this._genericRules) {
        if (rule.kind === 'allow') {
          if (rule.isRegex && rule.regex.test(url)) return { blocked: false, rule: rule.source };
          if (!rule.isRegex && url.includes(rule.path)) return { blocked: false, rule: rule.source };
          continue;
        }
        if (rule.excluded?.some((ex) => host === ex || host.endsWith(`.${ex}`))) continue;
        if (rule.thirdParty && !isThirdParty) continue;
        if (rule.firstParty && isThirdParty) continue;
        if (rule.resourceType && resourceType && rule.resourceType !== resourceType) continue;

        if (rule.isRegex) {
          if (rule.regex.test(url)) {
            blockedRule = rule.source;
            break;
          }
        } else if (rule.path) {
          const decoded = rule.path.replace(/\^/g, '.');
          if (url.includes(decoded)) {
            blockedRule = rule.source;
            break;
          }
        }
      }
    }

    return { blocked: Boolean(blockedRule), rule: blockedRule };
  }

  /**
   * Cosmetic selectors that apply to a hostname.
   * @param {string} hostname
   * @returns {string[]}
   */
  cosmeticFor(hostname) {
    const out = [];
    const host = hostname.toLowerCase();
    const seen = new Set();

    for (const candidate of domainCandidates(host)) {
      const rules = this._cosmeticByDomain.get(candidate);
      if (!rules) continue;
      for (const rule of rules) {
        if (seen.has(rule.selector)) continue;
        seen.add(rule.selector);
        out.push(rule.selector);
      }
    }
    for (const rule of this._cosmeticGeneric) {
      if (seen.has(rule.selector)) continue;
      seen.add(rule.selector);
      out.push(rule.selector);
    }
    return out;
  }
}

/** host, host parent, ... registrable-ish domain chain. */
function domainCandidates(host) {
  const out = [host];
  const parts = host.split('.');
  for (let i = 1; i < parts.length - 1; i++) {
    out.push(parts.slice(i).join('.'));
  }
  return out;
}

function computeThirdParty(parsed, referrer) {
  if (!referrer) return true;
  try {
    const ref = new URL(referrer);
    const a = ref.hostname.toLowerCase();
    const b = parsed.hostname.toLowerCase();
    if (a === b) return false;
    return !(b.endsWith(`.${a}`) || a.endsWith(`.${b}`));
  } catch {
    return true;
  }
}
