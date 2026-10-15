const assert = require('node:assert/strict');
const {createRequire} = require('node:module');
const requireRuntime = process.env.SCREENTASK_NODE_MODULES ? createRequire(process.env.SCREENTASK_NODE_MODULES + '/package.json') : require;
const {chromium} = requireRuntime('playwright');
const origin = process.env.SCREENTASK_TEST_ORIGIN || 'http://localhost:17871';

async function generatedCapture() {
  navigator.mediaDevices.getDisplayMedia = async () => {
    const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 360;
    const ctx = canvas.getContext('2d'); let n = 0;
    setInterval(() => { ctx.fillStyle = `hsl(${n++ % 360},70%,40%)`; ctx.fillRect(0, 0, 640, 360); ctx.fillStyle = 'white'; ctx.fillText('Connection regression ' + n, 50, 50); }, 33);
    return canvas.captureStream(30);
  };
}
async function connected(page) {
  await page.waitForFunction(() => document.getElementById('status').textContent.includes('Direct connected'), null, {timeout: 20000});
  await page.waitForFunction(() => { const v = document.getElementById('video'); return v.videoWidth > 0 && v.readyState >= 2; }, null, {timeout: 15000});
}
async function viewerLayout(page) {
  const hidden = await page.locator('#stream').evaluate(element => ({display: getComputedStyle(element).display, width: element.getBoundingClientRect().width}));
  assert.deepEqual(hidden, {display: 'none', width: 0}, 'Inactive JPEG fallback occupies space beside the video');
  await page.click('#filesToggle');
  assert.equal(await page.locator('#filePanel').isVisible(), true);
  assert.equal(await page.locator('#chatPanel').isVisible(), false);
  assert.equal(await page.locator('#streamVideo').isVisible(), true);
  await page.click('#chatTab');
  await page.selectOption('#viewMode', 'fill'); await page.click('#fullscreenBtn');
  await page.waitForFunction(() => document.fullscreenElement === document.getElementById('player'));
  const bounds = await page.evaluate(() => {
    const stage = document.getElementById('stage').getBoundingClientRect(), video = document.getElementById('streamVideo').getBoundingClientRect();
    return {left: stage.left, top: stage.top, width: stage.width, height: stage.height, vw: innerWidth, vh: innerHeight, fit: getComputedStyle(document.getElementById('streamVideo')).objectFit, mediaWidth: video.width, mediaHeight: video.height};
  });
  assert(Math.abs(bounds.left) < 1 && Math.abs(bounds.top) < 1);
  assert(Math.abs(bounds.width - bounds.vw) < 1 && Math.abs(bounds.height - bounds.vh) < 1, JSON.stringify(bounds));
  assert(Math.abs(bounds.mediaWidth - bounds.width) < 1 && Math.abs(bounds.mediaHeight - bounds.height) < 1);
  assert.equal(bounds.fit, 'cover');
  assert.equal(await page.locator('#viewMode').isVisible(), true, 'View control disappeared in fullscreen');
  await page.selectOption('#viewMode', '150');
  assert.equal(await page.locator('#player').evaluate(element => element.style.getPropertyValue('--media-zoom')), '1.5');
  await page.evaluate(() => document.exitFullscreen());
  await page.waitForFunction(() => !document.fullscreenElement);
  assert.equal(await page.locator('#viewMode').inputValue(), '150');
  await page.selectOption('#viewMode', 'fit');
  assert.equal(await page.locator('#streamVideo').evaluate(element => getComputedStyle(element).objectFit), 'contain');
  console.log('PASS: no ghost fallback; shared Files panel; fullscreen fills viewport and preserves Fit/Fill/zoom');
}
async function main() {
  const browsers = [];
  try {
    const chrome = await chromium.launch({channel: 'chrome', headless: true}); browsers.push(chrome);
    const edge = await chromium.launch({channel: 'msedge', headless: true}); browsers.push(edge);
    const errors = [];
    const host = await chrome.newPage(); const viewer = await edge.newPage();
    for (const p of [host, viewer]) p.on('pageerror', e => errors.push(e.message));
    await host.addInitScript(generatedCapture);
    await host.goto(origin + '/host');
    const room = await host.locator('#room').textContent();
    await viewer.goto(origin + '/direct?room=' + room);
    await viewer.click('#connect');
    await viewer.waitForFunction(() => document.getElementById('status').textContent.includes('Waiting for the host'));
    await host.click('#start');
    await connected(viewer);
    assert.deepEqual(await host.locator('#preview').evaluate(v => [v.videoWidth, v.videoHeight]), [640, 360]);
    // WebRTC can adapt the encoded resolution to CPU/network conditions. Verify
    // decoded content and aspect ratio rather than requiring the first RTP frame
    // to have the capture source's full resolution.
    const dimensions = await viewer.locator('#video').evaluate(v => [v.videoWidth, v.videoHeight]);
    assert(dimensions[0] >= 160 && dimensions[0] <= 640 && dimensions[0] * 9 === dimensions[1] * 16);
    const firstTime = await viewer.locator('#video').evaluate(v => v.currentTime);
    await viewer.waitForFunction(time => document.getElementById('video').currentTime > time + .25, firstTime);
    console.log('PASS: viewer arrives before host; Chrome → Edge direct media decoded', dimensions);

    const occupied = await edge.newPage(); await occupied.goto(origin + '/direct?room=' + room); await occupied.click('#connect');
    await occupied.waitForFunction(() => document.getElementById('status').textContent.includes('Another viewer'));
    console.log('PASS: duplicate viewer is rejected without breaking the connected viewer');
    await occupied.close();
    await viewer.click('#disconnect');
    await host.waitForFunction(() => document.getElementById('status').textContent.includes('Ready for another viewer'));
    await viewer.click('#connect'); await connected(viewer);
    console.log('PASS: same invitation reconnects without restarting host capture');

    await host.fill('#viewerAddress', origin.replace('localhost', '127.0.0.1'));
    const link = await host.locator('#invite').getAttribute('href');
    assert.equal(new URL(link).port, new URL(origin).port); assert.equal(new URL(link).searchParams.get('room'), room);
    await viewer.click('#disconnect');
    await host.waitForFunction(() => document.getElementById('status').textContent.includes('Ready for another viewer'));
    await viewer.fill('#room', link); await viewer.click('#connect');
    await viewer.waitForURL(link); assert.equal(await viewer.locator('#room').inputValue(), room);
    await viewer.click('#connect'); await connected(viewer);
    console.log('PASS: full invitation changes server address while retaining port and pairing code');

    await host.click('#stop');
    await viewer.waitForFunction(() => document.getElementById('status').textContent.includes('Host stopped'));
    assert.notEqual(await host.locator('#room').textContent(), room);
    assert.equal(await host.locator('#copyLink').isDisabled(), true);
    await host.click('#start');
    await host.waitForFunction(() => document.getElementById('status').textContent.includes('Waiting for your viewer'));
    const newRoom = await host.locator('#room').textContent();
    await viewer.fill('#room', newRoom); await viewer.click('#connect'); await connected(viewer);
    console.log('PASS: stop/start creates a fresh invitation and reconnects');
    await host.click('#stop');
    await host.check('#hdrNative'); await host.click('#start');
    await host.waitForFunction(() => document.getElementById('status').textContent.includes('Enable HDR color correction'), null, {timeout: 20000});
    assert.equal(await host.locator('#start').isDisabled(), false);
    assert.equal(await host.locator('body > video').count(), 0);
    console.log('PASS: native HDR mode rejects an uncorrected SDR source and cleans up');

    const blocked = await edge.newPage();
    await blocked.addInitScript(() => {
      const NativeSocket = WebSocket;
      window.WebSocket = class extends NativeSocket { constructor(url, protocols) { const u = new URL(url); u.port = '17872'; super(u, protocols); } };
    });
    await blocked.goto(origin + '/direct'); await blocked.fill('#room', '0123456789abcdef'); await blocked.click('#connect');
    await blocked.waitForFunction(() => document.getElementById('status').textContent.includes('exact address and port'));
    assert.equal(await blocked.locator('#connect').isDisabled(), false);
    console.log('PASS: unreachable signaling explains address/port and allows retry');

    const mse = await chrome.newPage(); mse.on('pageerror', e => errors.push(e.message));
    await mse.goto(origin + '/');
    await mse.waitForFunction(() => document.getElementById('streamVideo').videoWidth > 0, null, {timeout: 30000});
    console.log('PASS: real FFmpeg H.264 stream decodes in Chrome');
    await viewerLayout(mse);
    await mse.click('#watchBtn'); await mse.click('#watchBtn');
    await mse.waitForFunction(() => document.getElementById('streamVideo').videoWidth > 0, null, {timeout: 15000});
    console.log('PASS: H.264 pause/resume starts a fresh decoder session');
    const fallback = await edge.newPage(); fallback.on('pageerror', e => errors.push(e.message));
    await fallback.addInitScript(() => { window.MediaSource = undefined; });
    await fallback.goto(origin + '/');
    await fallback.waitForFunction(() => { const i = document.getElementById('stream'); return !i.hidden && i.naturalWidth > 0; }, null, {timeout: 20000});
    console.log('PASS: browser without MSE receives JPEG while H.264 remains active');
    assert.equal(await fallback.locator('#streamVideo').evaluate(element => getComputedStyle(element).display), 'none');
    const rejected = await edge.newPage(); rejected.on('pageerror', e => errors.push(e.message));
    await rejected.addInitScript(() => { MediaSource.isTypeSupported = () => false; });
    await rejected.goto(origin + '/');
    await rejected.waitForFunction(() => document.getElementById('stream').naturalWidth > 0, null, {timeout: 15000});
    console.log('PASS: unsupported codec uses JPEG compatibility stream');
    const decoder = await chrome.newPage(); decoder.on('pageerror', e => errors.push(e.message));
    await decoder.addInitScript(() => {
      const original = MediaSource.prototype.addSourceBuffer;
      MediaSource.prototype.addSourceBuffer = function(...args) { const buffer = original.apply(this, args); window.testBuffer = buffer; return buffer; };
    });
    await decoder.goto(origin + '/');
    await decoder.waitForFunction(() => !!window.testBuffer);
    await decoder.evaluate(() => window.testBuffer.dispatchEvent(new Event('error')));
    await decoder.waitForFunction(() => document.getElementById('stream').naturalWidth > 0, null, {timeout: 15000});
    console.log('PASS: asynchronous decoder rejection falls back to JPEG');
    assert.deepEqual(errors, [], 'Unexpected browser errors');
  } finally { await Promise.all(browsers.map(b => b.close())); }
}
main().catch(e => { console.error(e); process.exitCode = 1; });
