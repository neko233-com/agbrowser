#!/usr/bin/env node
/**
 * agbrowser MCP server entry point.
 *
 * Starts a browser runtime and serves the MCP protocol over stdio.
 * Environment:
 *   AGB_HEADLESS=1        run the browser headless
 *   AGB_VERTICAL_TABS=0   disable the left-side vertical tab strip
 *   AGB_ADBLOCK=0         disable ad blocking
 *   AGB_DATA_DIR=<path>   where to keep the local store
 *   AGB_EXTENSIONS=1      load the built-in extension pack
 */
import { join, resolve } from 'node:path';
import { existsSync } from 'node:fs';

import { Runtime } from './runtime.js';
import { createServer } from './tools.js';

const here = import.meta.dirname;

/** Locate the built-in extension pack relative to this file. */
function findExtensionPack() {
  const candidates = [
    // Repo checkout: packages/mcp/src -> packages/extensions
    resolve(here, '..', '..', 'extensions'),
    // Installed layout: node_modules/@agbrowser/mcp/src -> sibling package
    resolve(here, '..', '..', '..', '@agbrowser', 'extensions'),
    resolve(here, '..', 'extensions'),
  ];
  for (const dir of candidates) {
    if (existsSync(dir)) {
      return [
        join(dir, 'agb-shield'),
        join(dir, 'agb-speed'),
        join(dir, 'agb-search'),
      ].filter((p) => existsSync(p));
    }
  }
  return [];
}

async function main() {
  const env = process.env;

  const extensionPaths = findExtensionPack();
  const loadExtensions = env.AGB_EXTENSIONS === '1' && extensionPaths.length > 0;

  const runtime = new Runtime({
    dataDir: env.AGB_DATA_DIR || undefined,
    headless: env.AGB_HEADLESS === '1',
    verticalTabs: env.AGB_VERTICAL_TABS !== '0',
    adblock: env.AGB_ADBLOCK !== '0',
    extensionPaths,
    loadExtensions,
  });

  const server = createServer(runtime);
  server.listen();

  // Keep the process alive; the stdio loop ends when the client disconnects.
  const shutdown = async () => {
    await runtime.close().catch(() => {});
    process.exit(0);
  };
  process.on('SIGINT', shutdown);
  process.on('SIGTERM', shutdown);
  process.on('exit', () => { void runtime.close(); });
}

main().catch((err) => {
  process.stderr.write(`agb-mcp fatal: ${err?.stack ?? err}\n`);
  process.exit(1);
});
