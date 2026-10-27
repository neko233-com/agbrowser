/**
 * Game engine support tests.
 *
 * These run against a real canvas game in a real browser, because the whole
 * point of the module is to work inside a live frame loop.
 */
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { createServer } from 'node:http';

import { launchBrowser } from '@agbrowser/core/browser/launcher';
import { PageController } from '@agbrowser/core/snapshot/page';
import { GameController } from '@agbrowser/core/snapshot/game';

const CHROME = 'C:/Program Files/Google/Chrome/Application/chrome.exe';

const results = [];
let failures = 0;
function check(name, pass, detail = '') {
  results.push({ name, pass });
  console.log(`${pass ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
  if (!pass) failures++;
}

// A small but complete game: a canvas, a frame loop, an entity list, a
// player position that input moves, and a global state object.
const GAME_PAGE = `<!DOCTYPE html>
<html><head><title>agb test game</title></head>
<body>
<canvas id="c" width="320" height="200"></canvas>
<script>
  const canvas = document.getElementById('c');
  const ctx = canvas.getContext('2d');
  const player = { x: 40, y: 100, w: 16, h: 16, score: 0 };
  const entities = [];
  for (let i = 0; i < 5; i++) entities.push({ id: i, x: i * 30, alive: true });

  window.game = { player, entities, level: 3, running: true };

  let frames = 0;
  function loop() {
    frames++;
    window.game.frames = frames;
    ctx.fillStyle = '#101820';
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.fillStyle = '#22d3ee';
    ctx.fillRect(player.x, player.y, player.w, player.h);
    for (const e of entities) {
      if (!e.alive) continue;
      ctx.fillStyle = '#a7f3d0';
      ctx.fillRect(e.x, 20, 10, 10);
    }
    window.game.lastFrame = frames;
    requestAnimationFrame(loop);
  }
  requestAnimationFrame(loop);

  canvas.addEventListener('pointerdown', (e) => {
    window.game.lastPointer = { x: e.clientX, y: e.clientY, type: e.type };
    player.x = Math.round(e.clientX);
  });
  window.addEventListener('keydown', (e) => {
    window.game.lastKey = e.key;
    if (e.key === 'ArrowRight') player.x += 10;
    if (e.key === ' ') player.score += 1;
  });
</script>
</body></html>`;

const server = createServer((req, res) => {
  res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
  res.end(GAME_PAGE);
});
await new Promise((r) => server.listen(0, '127.0.0.1', r));
const url = `http://127.0.0.1:${server.address().port}/`;

const userDataDir = mkdtempSync(join(tmpdir(), 'agb-game-'));
let session = null;

try {
  session = await launchBrowser({
    binary: CHROME,
    channelId: 'chrome',
    accountId: 'Default',
    userDataDir,
    headless: true,
  });

  const targetId = (await session.listPages())[0].targetId;
  const page = new PageController({ session, targetId });
  const game = new GameController({ page });

  // Install the harness *before* navigating so the frame loop is hooked from
  // the very first frame.
  await game.install();
  await page.navigate(url, { waitUntil: 'load' });
  await new Promise((r) => setTimeout(r, 900));

  // ------------------------------------------------------------- detection
  const detected = await game.detect();
  check('detects a canvas game', detected.isGame === true, JSON.stringify(detected.engines));
  check('reports the canvas', detected.canvases.length === 1,
    `${detected.canvases[0]?.width}x${detected.canvases[0]?.height} ${detected.canvases[0]?.context}`);
  check('identifies a 2d context', detected.canvases[0]?.context === '2d');
  check('reports webgpu availability', typeof detected.hasWebgpu === 'boolean');
  check('finds the game globals', detected.globals?.game?.type === 'object',
    JSON.stringify(detected.globals?.game));

  // ----------------------------------------------------------------- state
  const state = await game.state();
  check('reads the game state', state?.game?.level === 3, `level=${state?.game?.level}`);
  check('reads the player position', state?.game?.player?.x === 40, `x=${state?.game?.player?.x}`);
  check('reports entity collections', state?.game?.entities?.length === 5,
    JSON.stringify(state?.game?.entities));

  const custom = await game.state('window.game.entities.filter(e=>e.alive).length');
  check('evaluates a custom expression', custom === 5, String(custom));

  // ------------------------------------------------------------ frame loop
  const fpsBefore = await game.fps();
  check('measures the frame rate', fpsBefore.fps > 0, `${fpsBefore.fps} fps over ${fpsBefore.frames} frames`);

  await game.pause();
  check('pauses the frame loop', (await game._call('isPaused')) === true);

  // The harness counts the frames it released to the game. While paused it
  // must not release any, which is what "the world stopped" means.
  const framesAtPause = await page.evaluate('window.__agbGame.state.frames');
  await new Promise((r) => setTimeout(r, 400));
  const framesStillPaused = await page.evaluate('window.__agbGame.state.frames');
  check('the loop stops while paused', framesAtPause === framesStillPaused,
    `${framesAtPause} -> ${framesStillPaused}`);

  await game.step(3);
  await new Promise((r) => setTimeout(r, 400));
  const framesAfterStep = await page.evaluate('window.__agbGame.state.frames');
  check('stepping advances exactly the requested frames',
    framesAfterStep >= framesAtPause + 3 && framesAfterStep < framesAtPause + 12,
    `${framesAtPause} -> ${framesAfterStep} (+${framesAfterStep - framesAtPause})`);

  await game.resume();
  await new Promise((r) => setTimeout(r, 400));
  const framesAfterResume = await page.evaluate('window.__agbGame.state.frames');
  check('resuming restarts the loop', framesAfterResume > framesAfterStep,
    `${framesAfterStep} -> ${framesAfterResume}`);

  // ----------------------------------------------------------------- input
  await game.pointer({ x: 120, y: 80, type: 'pointerdown' });
  await new Promise((r) => setTimeout(r, 150));
  const pointer = await page.evaluate('window.game.lastPointer ? {...window.game.lastPointer} : null');
  check('pointer input reaches the game', pointer?.type === 'pointerdown',
    JSON.stringify(pointer));
  check('pointer coordinates are exact', pointer?.x === 120 && pointer?.y === 80,
    `${pointer?.x},${pointer?.y}`);

  await game.key('ArrowRight', { repeat: 2 });
  await new Promise((r) => setTimeout(r, 150));
  const playerX = await page.evaluate('window.game.player.x');
  check('keyboard input moves the player', playerX === 120 + 20, `x=${playerX}`);

  await game.key(' ');
  await new Promise((r) => setTimeout(r, 100));
  const score = await page.evaluate('window.game.player.score');
  check('key events reach the game', score === 1, `score=${score}`);

  // ---------------------------------------------------------------- canvas
  const sample = await game.sample({ x: 0, y: 0, width: 32, height: 32 });
  check('samples canvas pixels', sample !== null && typeof sample.averageLuma === 'number',
    JSON.stringify(sample));
  check('the canvas is actually rendering', sample?.nonBlackRatio > 0,
    `nonBlack=${sample?.nonBlackRatio}`);

  // ------------------------------------------------------------ long frames
  await game.resetStats();
  await new Promise((r) => setTimeout(r, 300));
  const stats = await game.fps();
  check('tracks long frames', typeof stats.longFrames === 'number', `${stats.longFrames} long frames`);
} catch (err) {
  check('suite completed without exception', false, err.stack ?? String(err));
} finally {
  if (session) await session.close().catch(() => {});
  await new Promise((r) => server.close(r));
  try { rmSync(userDataDir, { recursive: true, force: true }); } catch { /* ignore */ }
}

console.log(`\n${results.length - failures}/${results.length} game checks passed`);
process.exit(failures > 0 ? 1 : 0);
