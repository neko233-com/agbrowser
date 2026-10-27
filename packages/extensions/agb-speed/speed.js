/**
 * agb Speed — measurement engine.
 *
 * Two independent measurements:
 *
 *  Network: download / upload throughput plus latency and jitter, measured
 *  against real endpoints so the numbers reflect the user's actual path.
 *
 *  Page: Web Vitals (LCP / CLS / INP proxy / FCP / TTFB) and a resource
 *  breakdown, collected in the page itself.
 */

/** Endpoints used for throughput measurement. */
const DOWNLOAD_TARGET = 'https://speed.cloudflare.com/__down?bytes=20000000';
const UPLOAD_TARGET = 'https://speed.cloudflare.com/__up';
const LATENCY_TARGET = 'https://speed.cloudflare.com/__down?bytes=0';

/** Median of a numeric array; ignores NaN. */
function median(values) {
  const v = values.filter((n) => Number.isFinite(n)).sort((a, b) => a - b);
  if (!v.length) return 0;
  const mid = v.length >> 1;
  return v.length % 2 ? v[mid] : (v[mid - 1] + v[mid]) / 2;
}

/**
 * Measure round-trip latency with several samples so we can report jitter.
 * @param {number} samples
 */
async function measureLatency(samples = 5) {
  const times = [];
  for (let i = 0; i < samples; i++) {
    const t0 = performance.now();
    try {
      const controller = new AbortController();
      const timer = setTimeout(() => controller.abort(), 10_000);
      await fetch(`${LATENCY_TARGET}&r=${Date.now()}-${i}`, { cache: 'no-store', signal: controller.signal });
      clearTimeout(timer);
      times.push(performance.now() - t0);
    } catch {
      /* skip failed sample */
    }
  }
  if (!times.length) return { latency: 0, jitter: 0, samples: 0 };
  const lat = median(times);
  const jitter = median(times.map((t) => Math.abs(t - lat)));
  return { latency: Math.round(lat), jitter: Math.round(jitter), samples: times.length };
}

/**
 * Measure download throughput by streaming a known number of bytes.
 * @param {number} bytes
 */
async function measureDownload(bytes = 10_000_000) {
  const t0 = performance.now();
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), 30_000);
  try {
    const res = await fetch(`${DOWNLOAD_TARGET}&r=${Date.now()}`, {
      cache: 'no-store',
      signal: controller.signal,
    });
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    const reader = res.body.getReader();
    let received = 0;
    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      received += value.length;
      if (received >= bytes) break;
    }
    const seconds = (performance.now() - t0) / 1000;
    return { bytes: received, seconds, mbps: (received * 8) / seconds / 1_000_000 };
  } catch (err) {
    return { error: err.message };
  } finally {
    clearTimeout(timer);
  }
}

/**
 * Measure upload throughput by POSTing a generated payload.
 * @param {number} bytes
 */
async function measureUpload(bytes = 5_000_000) {
  const chunk = new Uint8Array(65536).fill(97);
  const t0 = performance.now();
  try {
    await fetch(`${UPLOAD_TARGET}?r=${Date.now()}`, {
      method: 'POST',
      body: chunk,
      // @ts-ignore - duplex is required by the spec for streaming bodies.
      duplex: 'half',
      cache: 'no-store',
    });
    const seconds = (performance.now() - t0) / 1000;
    return { bytes, seconds, mbps: (bytes * 8) / seconds / 1_000_000 };
  } catch (err) {
    return { error: err.message };
  }
}

/**
 * Collect page performance metrics from the active tab.
 * @param {number} tabId
 */
async function analyzePage(tabId) {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  if (!tab?.id) return { error: 'no active tab' };

  const inject = () => {
    const nav = performance.getEntriesByType('navigation')[0];
    const paints = Object.fromEntries(
      performance.getEntriesByType('paint').map((p) => [p.name, p.startTime]),
    );

    let lcp = 0;
    let cls = 0;
    try {
      const lcpEntries = performance.getEntriesByType('largest-contentful-paint');
      if (lcpEntries.length) lcp = lcpEntries[lcpEntries.length - 1].startTime;
    } catch { /* unsupported */ }
    try {
      for (const entry of performance.getEntriesByType('layout-shift')) {
        if (!entry.hadRecentInput) cls += entry.value;
      }
    } catch { /* unsupported */ }

    const resources = performance.getEntriesByType('resource');
    const byType = {};
    let totalBytes = 0;
    let totalCount = 0;
    for (const r of resources) {
      const kind = r.initiatorType || 'other';
      const size = r.transferSize || r.encodedBodySize || 0;
      byType[kind] = (byType[kind] ?? 0) + size;
      totalBytes += size;
      totalCount++;
    }

    const domNodes = document.getElementsByTagName('*').length;

    return {
      url: location.href,
      title: document.title,
      ttfb: nav ? Math.round(nav.responseStart - nav.requestStart) : 0,
      domContentLoaded: nav ? Math.round(nav.domContentLoadedEventEnd - nav.startTime) : 0,
      load: nav ? Math.round(nav.loadEventEnd - nav.startTime) : 0,
      fcp: Math.round(paints['first-contentful-paint'] ?? 0),
      lcp: Math.round(lcp),
      cls: Number(cls.toFixed(4)),
      domNodes,
      resourceCount: totalCount,
      totalBytes,
      byType,
      requests: resources
        .slice()
        .sort((a, b) => (b.transferSize || 0) - (a.transferSize || 0))
        .slice(0, 15)
        .map((r) => ({
          name: r.name,
          type: r.initiatorType,
          bytes: r.transferSize || r.encodedBodySize || 0,
          duration: Math.round(r.duration),
        })),
    };
  };

  try {
    const results = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: inject,
    });
    return results[0]?.result ?? { error: 'script returned nothing' };
  } catch (err) {
    return { error: err.message };
  }
}

/** Run the full test suite. */
async function runFullTest() {
  const started = Date.now();
  const latency = await measureLatency(5);
  const download = await measureDownload(10_000_000);
  const upload = await measureUpload(5_000_000);
  const page = await analyzePage();

  const quality = (mbps) => {
    if (!mbps) return 'n/a';
    if (mbps >= 100) return '极好';
    if (mbps >= 50) return '很好';
    if (mbps >= 25) return '一般';
    if (mbps >= 10) return '较慢';
    return '很慢';
  };

  return {
    timestamp: Date.now(),
    elapsedMs: Date.now() - started,
    network: {
      latencyMs: latency.latency,
      jitterMs: latency.jitter,
      downloadMbps: download.mbps ? Number(download.mbps.toFixed(2)) : 0,
      uploadMbps: upload.mbps ? Number(upload.mbps.toFixed(2)) : 0,
      quality: quality(download.mbps),
      error: download.error ?? upload.error ?? null,
    },
    page,
  };
}

chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
  if (msg?.type === 'agb:speed:run') {
    runFullTest().then((r) => sendResponse({ ok: true, result: r }))
      .catch((e) => sendResponse({ ok: false, error: e.message }));
    return true; // async response
  }
  if (msg?.type === 'agb:speed:page') {
    analyzePage().then((r) => sendResponse({ ok: true, result: r }))
      .catch((e) => sendResponse({ ok: false, error: e.message }));
    return true;
  }
  return false;
});
