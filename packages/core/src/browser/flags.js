/**
 * Launch flags for a "slim, ad-free, agent-first" Chromium session.
 *
 * IMPORTANT: Chromium accepts `--enable-features` / `--disable-features` only
 * ONCE — the last occurrence wins. So every feature list in this module is
 * merged into a single flag pair by `buildLaunchFlags`.
 */

/** Features we explicitly want ON. */
const ENABLE_FEATURES = [
  'VerticalTabs',            // native left-side vertical tab strip
  'TabStripUnification',     // unified horizontal/vertical tab strip infra
  'NetworkService',
  'NetworkServiceInProcess',
];

/** Features we explicitly want OFF (bloat, telemetry, background chatter). */
const DISABLE_FEATURES = [
  'Translate',
  'BackForwardCache',
  'AcceptCHFrame',
  'MediaRouter',
  'OptimizationHints',
  'OptimizationGuideModelDownloading',
  'InterestFeedContentSuggestions',
  'CalculateNativeWinOcclusion',
  'GlobalMediaControls',
  'PrivacySandboxSettings4',
  'AutofillServerCommunication',
  'CertificateTransparencyComponentUpdater',
  'DialMediaRouteProvider',
  'ExtensionManifestV2Disabled',
  'EnhancedSafeBrowsing',
  'ImprovedCookieControls',
  'AiMode',
];

/**
 * Flags that keep a session lean and quiet.
 * Everything here removes background work that an agent-driven or
 * human-focused browsing session does not need.
 */
export const SLIM_FLAGS = [
  '--no-first-run',
  '--no-default-browser-check',
  '--no-service-autorun',
  '--disable-session-crashed-bubble',
  '--disable-infobars',
  '--disable-notifications',
  '--disable-search-engine-choice-screen',
  '--disable-sync',
  '--disable-background-networking',
  '--disable-background-timer-throttling',
  '--disable-backgrounding-occluded-windows',
  '--disable-renderer-backgrounding',
  '--disable-client-side-phishing-detection',
  '--disable-component-update',
  '--disable-component-extensions-with-background-pages',
  '--disable-default-apps',
  '--disable-hang-monitor',
  '--disable-popup-blocking',
  '--disable-prompt-on-repost',
  '--disable-domain-reliability',
  '--disable-breakpad',
  '--disable-print-preview',
  '--disable-translate',
  '--disable-features=OptimizationHints',
  '--metrics-recording-only',
  '--no-pings',
  '--mute-audio',
  '--password-store=basic',
  '--use-mock-keychain',
  '--force-color-profile=srgb',
  // Keep navigator.webdriver false without relying on the injected script.
  '--disable-blink-features=AutomationControlled',
];

/**
 * Hosts Chromium contacts for ads / analytics / suggestions.
 * Mapped to a black-hole address so the request fails locally and fast
 * instead of leaking a DNS + TLS round trip.
 */
export const NO_AD_HOSTS = [
  'googleads.g.doubleclick.net',
  'doubleclick.net',
  'google-analytics.com',
  'ssl.google-analytics.com',
  'googletagmanager.com',
  'www.googletagmanager.com',
  'googlesyndication.com',
  'pagead2.googlesyndication.com',
  'adservice.google.com',
  'adservice.googleusercontent.com',
  'googleadservices.com',
  'www.googleadservices.com',
  'amazon-adsystem.com',
  'c.amazon-adsystem.com',
  'adnxs.com',
  'adsrvr.org',
  'crashlyticsreports-pa.googleapis.com',
  'update.googleapis.com',
  'play.google-analytics.com',
  'app-measurement.com',
  'firebaseinstallations.googleapis.com',
  'safebrowsing.googleapis.com',
  'content-autofill.googleapis.com',
  'optimizationguide-pa.googleapis.com',
  'suggestqueries.google.com',
  'clientservices.googleapis.com',
  'gvt1.com',
  'gvt2.com',
  'gvt3.com',
];

/** Build the `--host-resolver-rules` value that black-holes ad hosts. */
export function hostResolverRules(hosts = NO_AD_HOSTS) {
  const rules = hosts.map((h) => `MAP ${h} ~NOTFOUND`);
  rules.push('EXCLUDE localhost', 'EXCLUDE 127.0.0.1');
  return rules.join(', ');
}

/**
 * Full flag set for a launch.
 *
 * @param {object} opts
 * @param {boolean} [opts.headless]
 * @param {number}  [opts.port] remote debugging port
 * @param {string}  [opts.userDataDir]
 * @param {string}  [opts.profileDir] profile inside userDataDir
 * @param {boolean} [opts.loadExtensions] load the built-in extension pack
 * @param {string[]} [opts.extensionPaths]
 * @param {boolean} [opts.verticalTabs] enable the left-side vertical tab strip
 * @param {string[]} [opts.extra]
 */
export function buildLaunchFlags({
  headless = false,
  port,
  userDataDir,
  profileDir,
  loadExtensions = false,
  extensionPaths = [],
  verticalTabs = true,
  extra = [],
} = {}) {
  const flags = [...SLIM_FLAGS];

  const enable = [...ENABLE_FEATURES];
  const disable = [...DISABLE_FEATURES];
  if (!verticalTabs) {
    // Drop both vertical-tab features.
    for (const f of ['VerticalTabs', 'TabStripUnification']) {
      const i = enable.indexOf(f);
      if (i !== -1) enable.splice(i, 1);
    }
  }

  flags.push(`--enable-features=${enable.join(',')}`);
  flags.push(`--disable-features=${disable.join(',')}`);

  if (port !== undefined && port !== null) {
    // port 0 is meaningful: it asks the OS for a free port and makes
    // Chromium write the chosen port to DevToolsActivePort.
    flags.push(`--remote-debugging-port=${port}`);
    flags.push('--remote-allow-origins=*');
  }
  if (userDataDir) flags.push(`--user-data-dir=${userDataDir}`);
  if (profileDir) flags.push(`--profile-directory=${profileDir}`);
  if (headless) flags.push('--headless=new');

  // Host-resolver black hole for ad endpoints.
  flags.push(`--host-resolver-rules=${hostResolverRules()}`);

  // Built-in extension pack.
  //
  // Chrome 137+ removed --load-extension, so the flags below are only a
  // fallback for older builds. Newer builds load the pack through the
  // Extensions.loadUnpacked CDP command instead (see launcher.js).
  if (loadExtensions && extensionPaths.length > 0) {
    const joined = extensionPaths.join(',');
    flags.push(`--load-extension=${joined}`);
    flags.push(`--disable-extensions-except=${joined}`);
  } else {
    flags.push('--disable-extensions');
  }

  flags.push(...extra);
  return flags;
}
