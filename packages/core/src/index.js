/**
 * @agbrowser/core — public API.
 *
 * agbrowser is an agent-first browser runtime built on the Chromium engine
 * that is already installed on the machine. It adds:
 *
 *   - a zero-dependency DevTools Protocol client (Node >= 24)
 *   - network-level ad blocking (native blocked-URL list + CDP interception)
 *   - multi-channel / multi-account management (Chrome, Edge, profiles)
 *   - cross-account tab synchronization with last-writer-wins merging
 *   - automation-surface hardening so pages behave identically for humans
 *     and for agents
 *   - native vertical tab strip, enabled by default
 */
export { CdpConnection, CdpError, discoverEndpoint, waitForEndpoint } from './cdp/connection.js';
export { launchBrowser, BrowserSession } from './browser/launcher.js';
export { detectChannels, listProfiles, findAccount, CHANNELS, resolveChannelPaths } from './browser/channels.js';
export { buildLaunchFlags, SLIM_FLAGS, NO_AD_HOSTS, hostResolverRules } from './browser/flags.js';
export {
  prepareProfile,
  enableVerticalTabs,
  disableVerticalTabs,
  readPreferences,
  writePreferences,
  describeNativeStores,
  VERTICAL_TABS_PREF,
} from './browser/profile.js';
export { installHardening, HARDENING_SCRIPT, STEALTH_FLAGS } from './browser/hardening.js';
export { PageController, normalizeUrl } from './snapshot/page.js';
export { AdBlocker } from './block/adblock.js';
export { RuleSet, parseNetworkRule, parseCosmeticRule } from './block/rules.js';
export { DEFAULT_FILTERS } from './block/filters.js';
export { Store, SCHEMA } from './store/store.js';
export { SyncEngine, SYNC_MODES } from './sync/engine.js';
