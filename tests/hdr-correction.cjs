const assert = require('node:assert/strict');
const path = require('node:path');
const {execFileSync} = require('node:child_process');
const {createRequire} = require('node:module');
const requireRuntime = process.env.SCREENTASK_NODE_MODULES ? createRequire(process.env.SCREENTASK_NODE_MODULES + '/package.json') : require;
const {chromium} = requireRuntime('playwright');
const origin = process.env.SCREENTASK_TEST_ORIGIN || 'http://localhost:17871';

function toneMapRamp() {
  const output = path.join(__dirname, 'ConnectionHarness/bin/Release/net48');
  const filter = execFileSync(path.join(output, 'ConnectionHarness.exe'), ['--tone-map-filter'], {encoding: 'utf8'}).trim();
  const width = 16, height = 2, ramp = [0, .01, .03, .1, .2, .3, .5, .75, 1, 2, 3, 4, 6, 8, 10, 12.5];
  const input = Buffer.alloc(width * height * 3 * 4);
  for (let channel = 0; channel < 3; channel++) for (let y = 0; y < height; y++) for (let x = 0; x < width; x++)
    input.writeFloatLE(ramp[x], ((channel * height + y) * width + x) * 4);
  const result = execFileSync(path.join(output, 'ffmpeg.exe'), ['-hide_banner', '-loglevel', 'error', '-f', 'rawvideo', '-pixel_format', 'gbrpf32le', '-video_size', width + 'x' + height, '-i', 'pipe:0', '-vf', filter + ',format=yuv444p', '-frames:v', '1', '-f', 'rawvideo', 'pipe:1'], {input});
  assert.equal(result.length, width * height * 3);
  const luma = [...result.subarray(0, width)];
  assert(luma[0] <= 17, 'Black level was lifted');
  for (let x = 1; x < width; x++) assert(luma[x] > luma[x - 1], 'HDR highlight steps collapsed or became non-monotonic');
  assert(luma.at(-1) <= 235 && luma.at(-1) >= 232, 'SDR reference white was out of range');
  for (const value of result.subarray(width * height)) assert(Math.abs(value - 128) <= 1, 'Neutral colors acquired a tint');
  console.log('PASS: floating-point HDR ramp preserves black, neutral colors and highlight detail in SDR', luma);
}

async function main() {
  toneMapRamp();
  const chrome = await chromium.launch({channel: 'chrome', headless: true});
  const edge = await chromium.launch({channel: 'msedge', headless: true});
  try {
    const info = await (await fetch(origin + '/connection-info')).json(); assert.equal(info.hdrCorrection, true);
    const response = await fetch(origin + '/stream.mp4'); assert.equal(response.status, 200);
    assert.equal(response.headers.get('X-Stream-HDR-Corrected'), 'true'); await response.body.cancel();
    console.log('PASS: real floating-point desktop capture produces an HDR-corrected H.264 stream');
    const errors = [], host = await chrome.newPage(), viewer = await edge.newPage();
    for (const page of [host, viewer]) page.on('pageerror', e => errors.push(e.message));
    await host.addInitScript(() => { navigator.mediaDevices.getDisplayMedia = async () => { throw new Error('Native HDR video must not use browser screen capture'); }; });
    await host.goto(origin + '/host');
    await host.waitForFunction(() => document.getElementById('hdrNative').checked);
    assert.equal(await host.locator('#hdrAudio').isChecked(), false);
    await host.click('#start');
    await host.waitForFunction(() => document.getElementById('status').textContent.includes('Waiting for your viewer'), null, {timeout: 30000});
    const room = await host.locator('#room').textContent(); await viewer.goto(origin + '/direct?room=' + room); await viewer.click('#connect');
    await viewer.waitForFunction(() => { const v = document.getElementById('video'); return v.videoWidth >= 160 && v.readyState >= 2 && document.getElementById('status').textContent.includes('Direct connected'); }, null, {timeout: 30000});
    assert((await host.locator('body > video').evaluate(v => v.videoWidth)) >= 854, 'Native HDR source resolution dropped');
    console.log('PASS: Chrome sends the native HDR-corrected monitor directly to Edge for decoded playback');
    await viewer.click('#disconnect');
    await host.waitForFunction(() => document.getElementById('status').textContent.includes('Ready for another viewer'));
    await viewer.click('#connect');
    // WebRTC can adapt the remote resolution independently of the full-size HDR source.
    await viewer.waitForFunction(() => { const v = document.getElementById('video'); return v.videoWidth >= 160 && v.readyState >= 2 && document.getElementById('status').textContent.includes('Direct connected'); }, null, {timeout: 20000});
    const before = await viewer.locator('#video').evaluate(v => v.currentTime);
    await viewer.waitForFunction(time => document.getElementById('video').currentTime > time + .2, before, {timeout: 10000});
    console.log('PASS: HDR direct viewer reconnects with advancing playback while native correction stays active', await viewer.locator('#video').evaluate(v => [v.videoWidth, v.videoHeight]));
    const jpeg = await edge.newPage(); jpeg.on('pageerror', e => errors.push(e.message));
    await jpeg.addInitScript(() => { window.MediaSource = undefined; }); await jpeg.goto(origin + '/', {waitUntil: 'domcontentloaded'});
    await jpeg.waitForFunction(() => document.getElementById('stream').naturalWidth >= 854, null, {timeout: 20000});
    console.log('PASS: HDR-corrected JPEG compatibility playback works concurrently');
    await host.click('#stop');
    await host.waitForFunction(() => !document.getElementById('hdrNative').disabled);
    assert.equal(await host.locator('body > video').count(), 0, 'Hidden HDR decoder leaked after stop');
    await viewer.waitForFunction(() => document.getElementById('status').textContent.includes('Host stopped'));
    console.log('PASS: stopping direct HDR sharing closes decoded tracks and removes its local decoder');
    assert.deepEqual(errors, []);
  } finally { await chrome.close(); await edge.close(); }
}
main().catch(e => { console.error(e); process.exitCode = 1; });
