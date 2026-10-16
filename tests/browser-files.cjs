const assert = require('node:assert/strict');
const fs = require('node:fs'), os = require('node:os'), path = require('node:path'), crypto = require('node:crypto');
const {spawn} = require('node:child_process'), {createRequire} = require('node:module');
const runtime = process.env.SCREENTASK_NODE_MODULES ? createRequire(process.env.SCREENTASK_NODE_MODULES + '/package.json') : require;
const {chromium} = runtime('playwright');
const origin = 'http://localhost:17875', directory = fs.mkdtempSync(path.join(os.tmpdir(), 'screentask-browser-files-'));
const fixtures = path.join(directory, 'fixtures'), stop = path.join(directory, 'stop');
const harness = spawn(path.join(__dirname, 'ConnectionHarness/bin/Release/net48/ConnectionHarness.exe'), ['17875', stop, '--files', fixtures], {windowsHide: true});
let output = '', errors = '';
harness.stdout.on('data', b => { output += b; }); harness.stderr.on('data', b => { errors += b; });
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function memory() { const before = output.length; harness.stdin.write('MEMORY\n'); for (let n = 0; n < 50; n++) { const match = /MEMORY (\d+)/.exec(output.slice(before)); if (match) return +match[1]; await wait(50); } throw new Error('No memory sample'); }
async function share(page, name) {
  await page.locator('#shareFiles').setInputFiles(path.join(fixtures, name)); await page.click('#createShares');
  const row = page.locator('.browserShare').filter({has: page.locator('h3', {hasText: name})});
  await row.locator('input').waitFor(); await page.waitForFunction(name => [...document.querySelectorAll('.browserShare')].some(row => row.querySelector('h3').textContent === name && row.querySelector('input').value.includes('?share=')), name);
  const link = await row.locator('input').inputValue(); return {row, link, token: new URL(link).searchParams.get('share')};
}
async function main() {
  let chrome, edge;
  try {
    for (let n = 0; n < 100; n++) { if (output.includes('READY')) break; if (harness.exitCode !== null) throw new Error(errors || output); await wait(100); }
    assert(output.includes('READY'), 'Listener did not start');
    chrome = await chromium.launch({channel: 'chrome', headless: true}); edge = await chromium.launch({channel: 'msedge', headless: true});
    const sender = await chrome.newPage(), receiver = await edge.newPage(), pageErrors = [];
    for (const page of [sender, receiver]) page.on('pageerror', error => pageErrors.push(error.message));
    await sender.goto(origin + '/files');
    await sender.locator('#browserSend summary').click();
    await sender.locator('#shareOrigin').fill(origin); // Loopback test invitation, without the LAN suggestion.
    const small = await share(sender, 'sample.bin');
    await receiver.goto(small.link); await receiver.locator('#browserDownload').waitFor({state: 'visible'});
    const downloadEvent = receiver.waitForEvent('download'); await receiver.click('#browserDownload'); const download = await downloadEvent;
    const saved = path.join(directory, 'received.bin'); await download.saveAs(saved);
    assert.equal(crypto.createHash('sha256').update(fs.readFileSync(saved)).digest('hex'), crypto.createHash('sha256').update(fs.readFileSync(path.join(fixtures, 'sample.bin'))).digest('hex'));
    await receiver.waitForFunction(() => document.getElementById('receiveStatus').textContent.includes('Check Downloads'));
    await sender.waitForFunction(() => document.querySelector('.transferTelemetry')?.textContent.includes('complete'));
    console.log('PASS: any browser selects files, creates links, and Chrome sends a verified file to Edge with progress on both pages');
    const huge = await share(sender, 'large-6GiB.bin');
    const meta = await (await fetch(origin + '/browser-file-info/' + huge.token)).json(); assert.equal(meta.size, String(6 * 1024 ** 3 + 7));
    for (const offset of [2147483651, 4294967303, 6 * 1024 ** 3 + 7 - 16]) {
      const response = await fetch(origin + '/browser-download/' + huge.token, {headers: {Range: `bytes=${offset}-${offset + 15}`}});
      assert.equal(response.status, 206); assert.equal(await response.text(), 'RANGE-64BIT-PASS');
    }
    console.log('PASS: browser File.slice and HTTP byte ranges preserve 64-bit offsets past 2/4 GiB');
    const before = await memory(), response = await fetch(origin + '/browser-download/' + huge.token, {headers: {Range: 'bytes=0-134217727'}});
    const hash = crypto.createHash('sha256'); let bytes = 0;
    for await (const chunk of response.body) { hash.update(chunk); bytes += chunk.length; }
    assert.equal(bytes, 128 * 1024 ** 2);
    const expected = crypto.createHash('sha256'), zeros = Buffer.alloc(1024 * 1024); for (let n = 0; n < 128; n++) expected.update(zeros);
    assert.equal(hash.digest('hex'), expected.digest('hex'));
    const after = await memory(), diagnostics = await sender.evaluate(() => window.ScreenTaskFilesDiagnostics);
    assert(after - before < 96 * 1024 ** 2, 'Server buffered too much of the file');
    assert(diagnostics.maxInflightBytes <= 1024 ** 2 && diagnostics.maxBufferedBytes <= 1024 ** 2, JSON.stringify(diagnostics));
    console.log('PASS: 128 MiB browser-sourced transfer streams with bounded server memory and at most 1 MiB browser data in flight', diagnostics);
    const id = 'a'.repeat(48);
    const initial = await fetch(origin + '/browser-download/' + huge.token + '?transfer=' + id, {headers: {Range: 'bytes=0-16777215'}});
    const reader = initial.body.getReader(); await reader.read(); await reader.cancel(); await wait(300);
    const resumed = await fetch(origin + '/browser-download/' + huge.token + '?transfer=' + id, {headers: {Range: 'bytes=1048576-1048591'}});
    assert.equal(resumed.status, 206); assert.equal((await resumed.arrayBuffer()).byteLength, 16);
    console.log('PASS: interrupted browser transfer resumes with the same download URL and a byte range');
    const cancellable = await fetch(origin + '/browser-download/' + huge.token), cancelReader = cancellable.body.getReader(); await cancelReader.read();
    await huge.row.getByRole('button', {name: 'Cancel download', exact: true}).first().click();
    let cancelled = false; try { while (!(await cancelReader.read()).done) {} } catch { cancelled = true; }
    assert(cancelled); assert.equal((await fetch(origin + '/browser-file-info/' + huge.token)).status, 200);
    console.log('PASS: sender can cancel a download in browser telemetry while retaining its share link');
    const held = []; for (let n = 0; n < 4; n++) held.push(await fetch(origin + '/browser-download/' + huge.token));
    const limited = await fetch(origin + '/browser-download/' + huge.token); assert.equal(limited.status, 429); await limited.text();
    await Promise.all(held.map(response => response.body.cancel())); await wait(200);
    console.log('PASS: browser-sourced downloads have a bounded concurrency limit');
    const active = await fetch(origin + '/browser-download/' + huge.token); const activeReader = active.body.getReader(); await activeReader.read();
    await huge.row.getByRole('button', {name: 'Stop sharing', exact: true}).click();
    let revoked = false; try { while (!(await activeReader.read()).done) {} } catch { revoked = true; }
    assert(revoked, 'Revocation should abort an active download');
    assert.equal((await fetch(origin + '/browser-file-info/' + huge.token)).status, 404);
    assert(fs.existsSync(path.join(fixtures, 'large-6GiB.bin')));
    console.log('PASS: browser revocation cancels active transfers and expires links without changing the source file');
    await receiver.goto(origin + '/files');
    const reverse = await share(receiver, "ação & 'friend'.txt");
    const content = await (await fetch(origin + '/browser-download/' + reverse.token)).text(); assert(content.includes('Large files'));
    console.log('PASS: a remote browser can send back its own selected file; Unicode names are preserved');
    await receiver.close(); await wait(200);
    assert.equal((await fetch(origin + '/browser-file-info/' + reverse.token)).status, 404);
    const forbidden = await sender.evaluate(async () => await new Promise(resolve => { const socket = new WebSocket('ws://127.0.0.1:17875/file-control'); socket.onopen = () => { socket.close(); resolve(false); }; socket.onerror = () => resolve(true); }));
    assert(forbidden, 'Cross-origin file control was allowed');
    harness.stdin.write('PRIVATE\n'); for (let n = 0; n < 50 && !output.includes('PRIVATE\n'); n++) await wait(50);
    assert.equal((await fetch(origin + '/browser-file-info/' + small.token)).status, 401);
    assert.equal((await fetch(origin + '/browser-file-info/' + small.token, {headers: {Authorization: 'Basic ' + Buffer.from('test:transfer').toString('base64')}})).status, 200);
    assert.deepEqual(pageErrors, []); assert.equal(errors, '');
    console.log('PASS: closing sender revokes its links; cross-origin control is rejected; file-only page stays independent of screen/audio');
    fs.mkdirSync(path.join(__dirname, 'artifacts'), {recursive: true}); await sender.screenshot({path: path.join(__dirname, 'artifacts/browser-file-sharing.png'), fullPage: true});
  } finally {
    if (chrome) await chrome.close(); if (edge) await edge.close(); fs.writeFileSync(stop, 'stop');
    for (let n = 0; n < 50 && harness.exitCode === null; n++) await wait(100);
    if (harness.exitCode === null) harness.kill();
    assert(path.resolve(directory).startsWith(path.resolve(os.tmpdir()) + path.sep + 'screentask-browser-files-'));
    fs.rmSync(directory, {recursive: true, force: true});
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
