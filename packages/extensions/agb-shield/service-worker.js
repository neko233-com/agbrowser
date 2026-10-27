/**
 * agb Shield — background service worker.
 *
 * Responsibilities:
 *  1. Declarative network rules for the hosts the kernel cannot block
 *     (they are needed by first-party pages).
 *  2. Maintain the per-site isolation state (what was blocked on this origin).
 *  3. Report stats to the popup and to the agbrowser core via storage.
 */

const STATS_KEY = 'shieldStats';
const DISABLED_KEY = 'shieldDisabled';

// Cosmetic selectors applied to every page. Kept intentionally small and
// high-confidence: a wrong selector breaks real content.
const COSMETIC_SELECTORS = [
  '.ad-container', '.ad-wrapper', '.adsbygoogle', '.ad-banner', '.ad-placement',
  '.ad-slot', '.advertisement', '.sponsored-content', '.sponsored',
  '[id^="google_ads_"]', '[id^="div-gpt-ad"]', '[id^="banner-ad"]',
  '[aria-label="advertisement"]', '[aria-label="Advertisements"]',
  '[data-ad-slot]', '[data-ad-client]', '[data-ad-slot-content]',
  '.taboola-container', '.outbrain-widget', '.criteo-ad',
  '.taboola', '.outbrain', '.trc_related_container',
  'iframe[src*="doubleclick.net"]', 'iframe[src*="googlesyndication.com"]',
  'iframe[src*="amazon-adsystem.com"]', 'iframe[src*="adnxs.com"]',
  'img[src*="doubleclick.net"]', 'img[src*="googlesyndication.com"]',
  '[class*="popup-overlay"]', '[id*="newsletter-modal"]',
];

// Tracker hosts blocked at the network layer by this extension.
const TRACKER_HOSTS = [
  'doubleclick.net', 'googlesyndication.com', 'googleadservices.com',
  'googletagservices.com', 'adservice.google.com', 'adservice.googleusercontent.com',
  'amazon-adsystem.com', 'adnxs.com', 'adsrvr.org', 'criteo.com', 'criteo.net',
  'pubmatic.com', 'rubiconproject.com', 'openx.net', 'taboola.com',
  'outbrain.com', 'sharethrough.com', 'zedo.com', 'adroll.com',
  'google-analytics.com', 'googletagmanager.com', 'scorecardresearch.com',
  'quantserve.com', 'hotjar.com', 'fullstory.com', 'mouseflow.com',
  'mixpanel.com', 'segment.io', 'segment.com', 'amplitude.com',
  'clarity.ms', 'branch.io', 'crazyegg.com', 'optimizely.com',
];

async function isDisabled() {
  const v = await chrome.storage.local.get(DISABLED_KEY);
  return Boolean(v[DISABLED_KEY]);
}

async function bumpStat(host) {
  const { [STATS_KEY]: stats = {} } = await chrome.storage.local.get(STATS_KEY);
  stats.total = (stats.total ?? 0) + 1;
  stats.byHost = stats.byHost ?? {};
  stats.byHost[host] = (stats.byHost[host] ?? 0) + 1;
  await chrome.storage.local.set({ [STATS_KEY]: stats });
}

async function installRules() {
  if (await isDisabled()) {
    await chrome.declarativeNetRequest.updateEnabledRulesets?.({ enableRulesetIds: [], disableRulesetIds: ['agb_shield'] });
    return;
  }
  const rules = TRACKER_HOSTS.map((host, i) => ({
    id: i + 1,
    priority: 1,
    action: { type: 'block' },
    condition: {
      urlFilter: `||${host}^`,
      resourceTypes: [
        'script', 'image', 'xmlhttprequest', 'sub_frame', 'media',
        'ping', 'websocket', 'other',
      ],
    },
  }));

  await chrome.declarativeNetRequest.updateDynamicRules({
    removeRuleIds: rules.map((r) => r.id),
    addRules: rules,
  });
}

chrome.runtime.onInstalled.addListener(async () => {
  await installRules();
});

chrome.runtime.onStartup.addListener(async () => {
  await installRules();
});

chrome.storage.onChanged.addListener(async (changes, area) => {
  if (area === 'local' && changes[DISABLED_KEY]) {
    await installRules();
  }
});

// Count the requests we blocked so the popup can show real numbers.
chrome.declarativeNetRequest.onRuleMatchedDebug?.addListener?.((info) => {
  bumpStat(info.request.url).catch(() => {});
});

chrome.webRequest?.onBeforeRequest?.addListener?.(
  (details) => {
    if (details.tabId < 0) return;
    const host = new URL(details.url).hostname;
    if (TRACKER_HOSTS.some((h) => host === h || host.endsWith(`.${h}`))) {
      bumpStat(host).catch(() => {});
    }
  },
  { urls: ['<all_urls>'] },
  ['blocking'],
);
