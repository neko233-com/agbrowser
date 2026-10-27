/**
 * agbrowser local control panel.
 *
 * A tiny HTTP server that serves a single-page dashboard and a small JSON
 * API. It binds to loopback only and requires a per-session token so that a
 * page in the browser cannot drive it by accident.
 */
import { createServer } from 'node:http';
import { randomBytes } from 'node:crypto';
import { readFileSync, existsSync } from 'node:fs';
import { join, extname } from 'node:path';

import { detectChannels } from '@agbrowser/core/browser/channels';
import { AdBlocker } from '@agbrowser/core/block/adblock';
import { RuleSet } from '@agbrowser/core/block/rules';
import { DEFAULT_FILTERS } from '@agbrowser/core/block/filters';

const PUBLIC_DIR = join(import.meta.dirname, 'public');

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
};

/**
 * Start the control panel.
 * @param {object} opts
 * @param {number} [opts.port]
 * @param {string} [opts.host]
 */
export async function startConsole({ port = 0, host = '127.0.0.1' } = {}) {
  const token = randomBytes(16).toString('hex');

  const ruleSet = new RuleSet().parse(DEFAULT_FILTERS);
  const adblocker = new AdBlocker({ ruleSet, enabled: true });

  const server = createServer(async (req, res) => {
    const url = new URL(req.url, `http://${host}`);
    const send = (code, body, type = 'application/json; charset=utf-8') => {
      res.writeHead(code, { 'content-type': type, 'cache-control': 'no-store' });
      res.end(body);
    };

    try {
      if (url.pathname === '/api/status') {
        const channels = detectChannels();
        return send(200, JSON.stringify({
          channels: channels.map((c) => ({
            id: c.id,
            label: c.label,
            binary: c.binary,
            userDataDir: c.userDataDir,
            accounts: c.accounts.map((a) => ({
              id: a.id,
              name: a.name,
              email: a.email,
              profileDir: a.profileDir,
            })),
          })),
          adblock: adblocker.report(),
          node: process.versions.node,
          platform: process.platform,
        }));
      }

      if (url.pathname === '/api/adblock') {
        return send(200, JSON.stringify(adblocker.report()));
      }

      if (url.pathname === '/') {
        const file = join(PUBLIC_DIR, 'index.html');
        if (!existsSync(file)) return send(404, 'not found', 'text/plain');
        const html = readFileSync(file, 'utf8').replace('__TOKEN__', token);
        return send(200, html, MIME['.html']);
      }

      // Static assets
      const asset = join(PUBLIC_DIR, url.pathname.replace(/^\//, ''));
      if (asset.startsWith(PUBLIC_DIR) && existsSync(asset)) {
        return send(200, readFileSync(asset), MIME[extname(asset)] ?? 'application/octet-stream');
      }

      return send(404, JSON.stringify({ error: 'not found' }));
    } catch (err) {
      return send(500, JSON.stringify({ error: err.message }));
    }
  });

  await new Promise((resolve, reject) => {
    server.once('error', reject);
    server.listen(port, host, resolve);
  });

  const actualPort = server.address().port;
  return {
    port: actualPort,
    url: `http://${host}:${actualPort}`,
    token,
    close: () => new Promise((r) => server.close(r)),
  };
}
