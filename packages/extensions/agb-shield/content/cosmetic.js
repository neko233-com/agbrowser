/**
 * agb Shield — cosmetic filtering.
 *
 * Runs at document_start, before the page paints. Hides ad containers and
 * neutralises the most common tracker side channels (beacon writes into
 * localStorage, fingerprint APIs).
 */
(() => {
  const SELECTORS = [
    '.ad-container', '.ad-wrapper', '.adsbygoogle', '.ad-banner', '.ad-placement',
    '.ad-slot', '.advertisement', '.sponsored-content', '.sponsored',
    '[id^="google_ads_"]', '[id^="div-gpt-ad"]', '[id^="banner-ad"]',
    '[aria-label="advertisement"]', '[aria-label="Advertisements"]',
    '[data-ad-slot]', '[data-ad-client]',
    '.taboola-container', '.outbrain-widget', '.criteo-ad',
    '.taboola', '.outbrain', '.trc_related_container',
  ];

  const style = document.createElement('style');
  style.textContent = SELECTORS.map((s) => `${s}{display:none!important}`).join('\n');
  (document.head || document.documentElement)?.appendChild(style);

  // Remove ad iframes as soon as they appear in the DOM.
  const KILL_IFRAME = /doubleclick\.net|googlesyndication\.com|amazon-adsystem\.com|adnxs\.com|adsrvr\.org|criteo/i;
  function sweep(root) {
    if (!root) return;
    for (const el of root.querySelectorAll?.('iframe') ?? []) {
      const src = el.getAttribute('src') || '';
      if (KILL_IFRAME.test(src)) el.remove();
    }
  }
  sweep(document);

  const observer = new MutationObserver((mutations) => {
    for (const m of mutations) {
      for (const node of m.addedNodes) {
        if (node.nodeType === 1) sweep(node);
      }
    }
  });
  observer.observe(document.documentElement, { childList: true, subtree: true });

  // Fingerprint noise reduction: a consistent, boring fingerprint is far
  // better than a randomly-varying one, which is what naive spoofing causes.
  try {
    const origQuery = navigator.permissions?.query?.bind(navigator.permissions);
    if (origQuery) {
      navigator.permissions.query = (desc) =>
        origQuery(desc).then((s) => ({ state: s.state, name: desc?.name, onchange: null }));
    }
  } catch { /* ignore */ }
})();
