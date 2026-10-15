const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {execFileSync} = require('node:child_process');
const {createRequire} = require('node:module');
const runtime = process.env.SCREENTASK_NODE_MODULES ? createRequire(process.env.SCREENTASK_NODE_MODULES + '/package.json') : require;
const {chromium} = runtime('playwright');
const origin = process.env.SCREENTASK_TEST_ORIGIN || 'http://localhost:17871';
const artifacts = path.join(__dirname, 'artifacts'); fs.mkdirSync(artifacts, {recursive: true});
const ffmpeg = path.join(__dirname, 'ConnectionHarness/bin/Release/net48/ffmpeg.exe');
const mp4 = path.join(artifacts, 'viewer-pattern.mp4'), png = path.join(artifacts, 'viewer-pattern.png');
execFileSync(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-y', '-f', 'lavfi', '-i', 'testsrc2=size=1920x1080:rate=5', '-t', '2', '-c:v', 'libx264', '-preset', 'ultrafast', '-profile:v', 'baseline', '-level:v', '4.0', '-pix_fmt', 'yuv420p', '-g', '5', '-movflags', '+empty_moov+default_base_moof+frag_keyframe', mp4]);
execFileSync(ffmpeg, ['-hide_banner', '-loglevel', 'error', '-y', '-i', mp4, '-frames:v', '1', png]);

async function main() {
  const browser = await chromium.launch({channel: 'chrome', headless: true});
  try {
    const page = await browser.newPage({viewport: {width: 1440, height: 900}}), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(base64 => {
      const original = window.fetch;
      window.fetch = (input, options) => {
        if (input === '/stream.mp4') {
          const bytes = Uint8Array.from(atob(base64), c => c.charCodeAt(0));
          return Promise.resolve(new Response(new ReadableStream({start(controller) { controller.enqueue(bytes); options?.signal?.addEventListener('abort', () => { try { controller.close(); } catch {} }); }}), {headers: {'X-Stream-Codecs': 'avc1.42C028'}}));
        }
        return original(input, options);
      };
    }, fs.readFileSync(mp4).toString('base64'));
    await page.goto(origin + '/'); await page.waitForFunction(() => document.getElementById('streamVideo').videoWidth === 1920);
    assert.equal(await page.locator('#stream').isVisible(), false);
    await page.screenshot({path: path.join(artifacts, 'viewer-window.png')});
    await page.click('#fullscreenBtn'); await page.waitForFunction(() => !!document.fullscreenElement);
    for (const mode of ['fit', 'fill', '150']) {
      await page.selectOption('#viewMode', mode);
      const geometry = await page.evaluate(() => {
        const stage = document.getElementById('stage').getBoundingClientRect();
        return [stage.x, stage.y, stage.width, stage.height, innerWidth, innerHeight, getComputedStyle(document.getElementById('streamVideo')).objectFit];
      });
      assert(geometry[0] === 0 && geometry[1] === 0 && geometry[2] === geometry[4] && geometry[3] === geometry[5]);
      assert.equal(geometry[6], mode === 'fill' ? 'cover' : 'contain');
      await page.screenshot({path: path.join(artifacts, 'viewer-fullscreen-' + mode + '.png')});
    }
    await page.evaluate(() => document.exitFullscreen()); await page.waitForFunction(() => !document.fullscreenElement);
    assert.equal(await page.locator('#viewMode').inputValue(), '150');
    await page.selectOption('#viewMode', 'fit');
    await page.click('#filesToggle'); assert.equal(await page.locator('#filePanel').isVisible(), true);
    await page.screenshot({path: path.join(artifacts, 'viewer-with-files.png')});
    await page.setViewportSize({width: 390, height: 844});
    assert.equal(await page.locator('#filePanel').isVisible(), true);
    assert(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1), 'Mobile viewer overflows horizontally');
    await page.screenshot({path: path.join(artifacts, 'viewer-mobile.png'), fullPage: true});
    await page.route('**/file-info/*', route => route.fulfill({json: {name: 'Shared project.zip', size: '1048576', download: '/download/' + 'b'.repeat(48)}}));
    await page.fill('#link', origin + '/files/' + 'b'.repeat(48));
    await page.focus('#open');
    await Promise.all([page.waitForURL(origin + '/files/' + 'b'.repeat(48)), page.keyboard.press('Space')]);
    await page.waitForFunction(() => document.getElementById('connection').textContent === 'Paused' && !document.getElementById('file').hidden);
    assert.equal(await page.locator('#filePanel').isVisible(), true);
    assert.equal(await page.locator('#watchBtn').getAttribute('aria-label'), 'Resume');
    console.log('PASS: visual viewer checks for Fit, Fill, fullscreen zoom, shared Files tab and mobile width');
    console.log('PASS: keyboard file-link opening uses the shared HTML and leaves screen playback paused');
    const jpeg = await browser.newPage({viewport: {width: 1440, height: 900}});
    await jpeg.addInitScript(() => { window.MediaSource = undefined; });
    await jpeg.route('**/stream.mjpg?*', route => route.fulfill({path: png, contentType: 'image/png'}));
    await jpeg.goto(origin + '/'); await jpeg.waitForFunction(() => document.getElementById('stream').naturalWidth === 1920);
    assert.equal(await jpeg.locator('#streamVideo').isVisible(), false);
    await jpeg.screenshot({path: path.join(artifacts, 'viewer-jpeg.png')});
    console.log('PASS: JPEG fallback occupies the player alone');
    const files = await browser.newPage({viewport: {width: 1280, height: 800}}), requests = [];
    files.on('request', request => { if (/\/(stream\.mp4|stream\.mjpg|stats|chat|audio)(\?|$)/.test(request.url())) requests.push(request.url()); });
    await files.route('**/connection-info', route => route.fulfill({json: {fileOnly: true}}));
    await files.route('**/file-info/*', route => route.fulfill({json: {name: 'Large project.zip', size: '6442450951', download: '/download/' + 'a'.repeat(48)}}));
    await files.goto(origin + '/files/' + 'a'.repeat(48)); await files.waitForFunction(() => !document.getElementById('file').hidden && document.body.classList.contains('files-only'));
    assert.equal(await files.locator('#player').isVisible(), false); assert.equal(await files.locator('#filePanel').isVisible(), true);
    assert.deepEqual(requests, [], 'File-only mode started a media request');
    await files.screenshot({path: path.join(artifacts, 'viewer-file-only.png')});
    assert.deepEqual(errors, []);
    console.log('PASS: same HTML renders file-only mode without requesting screen, audio or chat');
  } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
