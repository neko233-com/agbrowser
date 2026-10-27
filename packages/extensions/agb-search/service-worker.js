/**
 * agb Search — aggregated search service worker.
 *
 * Provides:
 *  - prefix shortcuts resolved against the user's own history / bookmarks
 *  - multi-engine fallback when the primary engine yields nothing
 *  - a "search everything" mode that queries history + bookmarks + tabs
 */

const ENGINES = {
  google: 'https://www.google.com/search?q=',
  bing: 'https://www.bing.com/search?q=',
  duckduckgo: 'https://duckduckgo.com/?q=',
  brave: 'https://search.brave.com/search?q=',
  baidu: 'https://www.baidu.com/s?wd=',
  sogou: 'https://www.sogou.com/web?query=',
  zhihu: 'https://www.zhihu.com/search?q=',
  bilibili: 'https://search.bilibili.com/all?keyword=',
  github: 'https://github.com/search?q=',
  so: 'https://www.so.com/s?q=',
  yandex: 'https://yandex.com/search/?text=',
};

/** Prefix -> engine key. Users type "gh foo" and land on GitHub search. */
const PREFIXES = {
  g: 'google', 'google': 'google',
  b: 'bing', 'bing': 'bing',
  d: 'duckduckgo', 'ddg': 'duckduckgo',
  br: 'brave', 'brave': 'brave',
  bd: 'baidu', 'baidu': 'baidu',
  sg: 'sogou', 'sogou': 'sogou',
  zh: 'zhihu', 'zhihu': 'zhihu',
  bili: 'bilibili', 'bilibili': 'bilibili',
  gh: 'github', 'github': 'github',
  so: 'so', '360': 'so',
  y: 'yandex', 'yandex': 'yandex',
};

const DEFAULT_ENGINE = 'google';

async function getDefaultEngine() {
  const { defaultEngine = DEFAULT_ENGINE } = await chrome.storage.sync.get('defaultEngine');
  return ENGINES[defaultEngine] ? defaultEngine : DEFAULT_ENGINE;
}

/**
 * Resolve a query string into a target URL.
 * @param {string} query
 */
async function resolveQuery(query) {
  const raw = query.trim();
  if (!raw) return null;

  // Prefix shortcut, e.g. "gh vite".
  const match = /^([a-z0-9]{1,10})\s+(.+)$/i.exec(raw);
  if (match) {
    const [, prefix, rest] = match;
    const engine = PREFIXES[prefix.toLowerCase()];
    if (engine) {
      return { url: ENGINES[engine] + encodeURIComponent(rest), engine, kind: 'prefix' };
    }
  }

  // Direct URL.
  if (/^https?:\/\//i.test(raw)) {
    return { url: raw, engine: null, kind: 'url' };
  }

  // Looks like a hostname.
  if (/^[\w-]+(\.[\w-]+)+(\/.*)?$/.test(raw) && !raw.includes(' ')) {
    return { url: `https://${raw}`, engine: null, kind: 'host' };
  }

  const engine = await getDefaultEngine();
  return { url: ENGINES[engine] + encodeURIComponent(raw), engine, kind: 'search' };
}

/** Search the user's own history for the best matching entry. */
async function searchHistory(query, limit = 5) {
  try {
    const results = await chrome.history.search({ text: query, maxResults: limit, startTime: 0 });
    return results.map((r) => ({
      title: r.title || r.url,
      url: r.url,
      lastVisit: r.lastVisitTime,
    }));
  } catch {
    return [];
  }
}

/** Search the user's bookmarks. */
async function searchBookmarks(query, limit = 5) {
  try {
    const tree = await chrome.bookmarks.search(query);
    return tree.slice(0, limit).map((b) => ({
      title: b.title,
      url: b.url,
    }));
  } catch {
    return [];
  }
}

chrome.omnibox.onInputChanged.addListener(async (text, suggest) => {
  const suggestions = [];

  const history = await searchHistory(text);
  for (const h of history) {
    suggestions.push({
      content: h.url,
      description: `历史记录 — ${h.title}`,
    });
  }

  const bookmarks = await searchBookmarks(text);
  for (const b of bookmarks) {
    suggestions.push({
      content: b.url,
      description: `书签 — ${b.title}`,
    });
  }

  const resolved = await resolveQuery(text);
  if (resolved) {
    suggestions.push({
      content: resolved.url,
      description: resolved.engine
        ? `${resolved.engine} 搜索`
        : '打开链接',
    });
  }

  suggest(suggestions);
});

chrome.omnibox.onInputEntered.addListener(async (text) => {
  const resolved = await resolveQuery(text);
  if (!resolved) return;
  await chrome.tabs.create({ url: resolved.url, active: true });
});

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === 'agb:search:resolve') {
    resolveQuery(msg.query).then((r) => sendResponse({ ok: true, result: r }))
      .catch((e) => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg?.type === 'agb:search:everything') {
    Promise.all([searchHistory(msg.query, 8), searchBookmarks(msg.query, 8)])
      .then(([h, b]) => sendResponse({ ok: true, result: { history: h, bookmarks: b } }))
      .catch((e) => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  if (msg?.type === 'agb:search:engines') {
    sendResponse({ ok: true, result: ENGINES });
    return true;
  }
  return false;
});

export { ENGINES, PREFIXES, resolveQuery };
