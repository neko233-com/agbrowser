// Force a startup failure against a local non-CDP endpoint and verify that
// the browser process created for this disposable profile has exited.
import { once } from 'node:events';
import { mkdtemp } from 'node:fs/promises';
import { createServer } from 'node:http';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { CHANNELS, resolveChannelPaths } from '../packages/core/src/browser/channels.js';
import { launchBrowser } from '../packages/core/src/browser/launcher.js';
import { cleanupTemporaryFixture } from './temporary-fixture.mjs';

const root = await mkdtemp(join(tmpdir(), 'agb-launch-failure-'));
const server = createServer((_request, response) => { response.writeHead(503); response.end(); });
let safeToClean = false, unexpectedSession;
try {
  server.listen(0, '127.0.0.1');
  await once(server, 'listening');
  let failure;
  try {
    unexpectedSession = await launchBrowser({
      binary: resolveChannelPaths(CHANNELS.find(channel => channel.id === 'edge')).binary,
      channelId: 'edge', userDataDir: root, headless: true,
      port: server.address().port, verticalTabs: false, adblock: false, timeout: 600,
    });
  } catch (error) { failure = error; }
  if (!failure) throw new Error('Expected the local non-CDP endpoint to cause startup failure');
  if (!failure.agbrowserProcessId || failure.agbrowserProcessStillRunning !== false)
    throw new Error('The spawned browser exit was not confirmed');
  try {
    process.kill(failure.agbrowserProcessId, 0);
    throw new Error('The failed browser process is still running');
  } catch (error) {
    if (error.code !== 'ESRCH') throw error;
  }
  safeToClean = true;
  console.log('PASS failed browser startup closes its spawned process');
} finally {
  server.close();
  if (unexpectedSession) await unexpectedSession.close();
  if (safeToClean) await cleanupTemporaryFixture(root, 'agb-launch-failure-');
  else console.error('Fixture process exit not confirmed; temporary data retained:', root);
}
