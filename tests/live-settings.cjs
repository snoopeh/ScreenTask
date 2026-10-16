const assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), os = require('node:os');
const {spawn} = require('node:child_process'), {createRequire} = require('node:module');
const runtime = process.env.SCREENTASK_NODE_MODULES ? createRequire(process.env.SCREENTASK_NODE_MODULES + '/package.json') : require;
const {chromium} = runtime('playwright');
const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'screentask-live-settings-')), stop = path.join(directory, 'stop');
const binary = path.join(__dirname, 'ConnectionHarness/bin/Release/net48/ConnectionHarness.exe');
const harness = spawn(binary, ['17876', stop, '--live', directory], {windowsHide: true});
let stdout = '', stderr = ''; harness.stdout.on('data', b => { stdout += b; }); harness.stderr.on('data', b => { stderr += b; });
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function change(values) {
  const offset = stdout.length; harness.stdin.write('SET ' + values + '\n');
  for (let n = 0; n < 300; n++) { const text = stdout.slice(offset); if (text.includes('COMMAND-ERROR')) throw new Error(text); const match = /APPLIED (.+)/.exec(text); if (match) return JSON.parse(match[1]); await wait(100); }
  throw new Error('Settings command timed out: ' + stdout.slice(offset));
}
async function main() {
  let chrome, edge;
  try {
    for (let n = 0; n < 100 && !stdout.includes('READY'); n++) await wait(100);
    assert(stdout.includes('READY'), stderr || stdout);
    const original = await change('fps=60,quality=75,width=1280,bitrate=2,hdr=manual');
    chrome = await chromium.launch({channel: 'chrome', headless: true}); edge = await chromium.launch({channel: 'msedge', headless: true});
    const viewer = await chrome.newPage(), host = await chrome.newPage(), direct = await edge.newPage(), sender = await edge.newPage();
    let audioConnections = 0; viewer.on('websocket', ws => { if (ws.url().endsWith('/audio')) audioConnections++; });
    const errors = []; for (const page of [viewer, host, direct, sender]) page.on('pageerror', e => errors.push(e.message));
    await viewer.goto('http://localhost:17876/');
    await viewer.waitForFunction(() => document.getElementById('streamVideo').videoWidth >= 854, null, {timeout: 30000});
    await sender.goto('http://localhost:17876/files');
    await sender.locator('#shareFiles').setInputFiles({name: 'keep-sharing.txt', mimeType: 'text/plain', buffer: Buffer.from('Files survive live capture reconfiguration')});
    await sender.click('#createShares'); await sender.waitForFunction(() => document.querySelector('.browserShare input')?.value.includes('?share='));
    const shareLink = await sender.locator('.browserShare input').inputValue(), token = new URL(shareLink).searchParams.get('share');
    await host.goto('http://localhost:17876/host'); await host.waitForFunction(() => document.getElementById('hdrNative').checked);
    await host.click('#start'); await host.waitForFunction(() => document.getElementById('status').textContent.includes('Waiting for your viewer'), null, {timeout: 30000});
    const room = await host.locator('#room').textContent(); await direct.goto('http://localhost:17876/direct?room=' + room); await direct.click('#connect');
    await direct.waitForFunction(() => document.getElementById('video').readyState >= 2 && document.getElementById('status').textContent.includes('Direct connected'), null, {timeout: 30000});
    const changed = await change('fps=30,width=854,bitrate=4,quality=70,exposure=0.2,peak=800,mouse=true');
    assert.equal(changed.listener, original.listener); assert.equal(changed.generation, original.generation); assert(changed.listening);
    await viewer.waitForFunction(() => document.getElementById('streamVideo').videoWidth === 854 && !document.getElementById('streamVideo').hidden, null, {timeout: 25000});
    await host.waitForFunction(() => document.querySelector('body > video')?.videoWidth === 854 && document.getElementById('preview').srcObject?.getVideoTracks()[0]?.getSettings().width === 854, null, {timeout: 25000});
    const before = await direct.locator('#video').evaluate(v => v.currentTime);
    await direct.waitForFunction(time => document.getElementById('video').currentTime > time + .4, before, {timeout: 15000});
    assert((await direct.locator('#status').textContent()).includes('Direct connected'));
    const stats = await (await fetch('http://localhost:17876/stats')).json(); assert.equal(stats.targetFps, 30); assert.equal(stats.bitrateLimitMbps, 4); assert.equal(stats.codec, 'H.264');
    assert.equal(await (await fetch('http://localhost:17876/browser-download/' + token)).text(), 'Files survive live capture reconfiguration');
    console.log('PASS: FPS, width, bitrate, quality, mouse and HDR overrides apply live; HTTP video and native HDR direct sharing recover without restarting the listener or revoking file links');
    const automatic = await change('hdr=auto'); assert(automatic.automaticHdr); assert.equal(automatic.manualVisible, false);
    await host.waitForFunction(() => document.querySelector('body > video')?.readyState >= 2, null, {timeout: 25000});
    const time = await direct.locator('#video').evaluate(v => v.currentTime); await direct.waitForFunction(t => document.getElementById('video').currentTime > t + .2, time);
    assert.equal(automatic.listener, original.listener);
    console.log('PASS: automatic HDR uses the active display profile and hides manual overrides; switching modes preserves the direct peer');
    const audioOn = await change('audio=true');
    await viewer.click('#audioBtn');
    await viewer.waitForFunction(() => document.getElementById('audioBtn').textContent.includes('Disable audio'), null, {timeout: 15000});
    const previousAudioConnections = audioConnections;
    const audioOff = await change('audio=false');
    await change('audio=true');
    for (let n = 0; n < 150 && audioConnections <= previousAudioConnections; n++) await wait(100);
    assert(audioConnections > previousAudioConnections, 'Browser did not reconnect its audio socket');
    await viewer.waitForFunction(() => document.getElementById('audioBtn').textContent.includes('Disable audio'), null, {timeout: 15000});
    await change('audio=false');
    assert(audioOn.audioGeneration > automatic.audioGeneration && audioOff.audioGeneration > audioOn.audioGeneration);
    assert.equal(audioOff.listener, original.listener);
    console.log('PASS: audio changes replace only audio capture; an enabled browser reconnects automatically and keeps the network listener running');
    const fileOnly = await change('fileOnly=true'); assert(fileOnly.fileOnly);
    assert.equal((await fetch('http://localhost:17876/stream.mp4')).status, 403);
    assert.equal(await (await fetch('http://localhost:17876/browser-download/' + token)).text(), 'Files survive live capture reconfiguration');
    const streaming = await change('fileOnly=false,hdr=off'); assert(!streaming.fileOnly);
    await viewer.reload(); await viewer.waitForFunction(() => document.getElementById('streamVideo').videoWidth === 854, null, {timeout: 25000});
    assert.equal(streaming.listener, original.listener);
    console.log('PASS: file-only mode switches on/off live without interrupting registered file sharing');
    await change('fileOnly=true');
    const moved = await change('port=17877'); assert.equal(moved.listener, original.listener); assert.equal(moved.generation, original.generation);
    const info = await (await fetch('http://localhost:17877/connection-info')).json(); assert.equal(info.viewerOrigin, 'http://127.0.0.1:17877');
    assert(fs.readFileSync(path.join(directory, 'preferences.xml'), 'utf8').includes('<Port>17877</Port>'));
    assert.equal(stderr, ''); assert.deepEqual(errors, []);
    console.log('PASS: port changes rebind the running listener automatically; settings save atomically in the isolated preferences folder');
  } finally {
    if (chrome) await chrome.close(); if (edge) await edge.close(); fs.writeFileSync(stop, 'stop');
    for (let n = 0; n < 80 && harness.exitCode === null; n++) await wait(100);
    if (harness.exitCode === null) harness.kill();
    assert(path.resolve(directory).startsWith(path.resolve(os.tmpdir()) + path.sep + 'screentask-live-settings-'));
    fs.rmSync(directory, {recursive: true, force: true});
  }
}
main().catch(e => { console.error(e); console.error(stdout.slice(-3500)); process.exitCode = 1; });
