const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const crypto = require('node:crypto');
const {spawn} = require('node:child_process');
const {createRequire} = require('node:module');
const requireRuntime = process.env.SCREENTASK_NODE_MODULES ? createRequire(process.env.SCREENTASK_NODE_MODULES + '/package.json') : require;
const {chromium} = requireRuntime('playwright');
const origin = 'http://localhost:17874';
const binary = path.join(__dirname, 'ConnectionHarness/bin/Release/net48/ConnectionHarness.exe');
const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'screentask-files-'));
const stopFile = path.join(directory, 'stop.signal');
const processHandle = spawn(binary, ['17874', stopFile, '--files', path.join(directory, 'fixtures')], {windowsHide: true});
let stdout = '', stderr = '';
const tokens = {};
processHandle.stdout.setEncoding('utf8'); processHandle.stderr.setEncoding('utf8');
processHandle.stdout.on('data', chunk => { stdout += chunk; for (const line of stdout.split(/\r?\n/)) { const match = /^FILE ([a-f0-9]{48}) (\S+)$/.exec(line); if (match) tokens[Buffer.from(match[2], 'base64').toString('utf8')] = match[1]; } });
processHandle.stderr.on('data', chunk => { stderr += chunk; });
const wait = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, timeout = 15000) { const start = Date.now(); while (!predicate()) { if (processHandle.exitCode !== null) throw new Error('Test server exited: ' + stderr); if (Date.now() - start > timeout) throw new Error('Test timed out: ' + stdout + stderr); await wait(25); } }
async function command(text, answer) { const previous = stdout.length; processHandle.stdin.write(text + '\n'); await until(() => stdout.slice(previous).includes(answer)); }
async function memory() { const previous = stdout.length; await command('MEMORY', 'MEMORY '); return Number(/MEMORY (\d+)/.exec(stdout.slice(previous))[1]); }
async function status(token) { const previous = stdout.length; await command('STATUS ' + token, 'STATUS '); const found = /STATUS (\d+) (\d+)/.exec(stdout.slice(previous)); return {active: Number(found[1]), bytes: Number(found[2])}; }
const request = (token, options) => fetch(origin + '/download/' + token, options);
const digest = bytes => crypto.createHash('sha256').update(bytes).digest('hex');

async function main() {
  let browser;
  try {
    await until(() => stdout.includes('READY'));
    const token = tokens['sample.bin'], huge = tokens['large-6GiB.bin'];
    const source = fs.readFileSync(path.join(directory, 'fixtures/sample.bin'));
    let response = await request(token);
    assert.equal(response.status, 200); assert.equal(response.headers.get('accept-ranges'), 'bytes');
    assert.equal(digest(Buffer.from(await response.arrayBuffer())), digest(source));
    const etag = response.headers.get('etag');
    console.log('PASS: full download matches SHA-256 and advertises resumability');
    for (const [range, first, last] of [['bytes=0-99', 0, 99], ['bytes=65536-', 65536, source.length - 1], ['bytes=-31', source.length - 31, source.length - 1], ['bytes=0-9999999', 0, source.length - 1]]) {
      response = await request(token, {headers: {Range: range}}); assert.equal(response.status, 206);
      assert.equal(response.headers.get('content-range'), `bytes ${first}-${last}/${source.length}`);
      assert.deepEqual(Buffer.from(await response.arrayBuffer()), source.subarray(first, last + 1));
    }
    for (const range of ['bytes=9-1', 'bytes=-0', 'bytes=99999999-', 'bytes=0-1,2-3', 'bytes=+1-3', 'bytes=9223372036854775808-']) {
      response = await request(token, {headers: {Range: range}}); assert([400, 416].includes(response.status), range); await response.body.cancel();
    }
    console.log('PASS: bounded, open-ended and suffix ranges; invalid and overflowing ranges rejected');
    const head = await request(huge, {method: 'HEAD'});
    const hugeLength = 6n * 1024n * 1024n * 1024n + 7n;
    assert.equal(head.headers.get('content-length'), String(hugeLength));
    for (const offset of [2147483651n, 4294967303n, hugeLength - 16n]) {
      response = await request(huge, {headers: {Range: `bytes=${offset}-${offset + 15n}`}}); assert.equal(response.status, 206);
      assert.equal(await response.text(), 'RANGE-64BIT-PASS');
    }
    console.log('PASS: 6 GiB sparse file uses 64-bit size/offsets beyond 2 GiB and 4 GiB');
    const memoryBefore = await memory(), bytesToSend = 512 * 1024 * 1024;
    response = await request(huge, {headers: {Range: `bytes=0-${bytesToSend - 1}`}}); assert.equal(response.status, 206);
    let received = 0; const transferHash = crypto.createHash('sha256');
    for await (const chunk of response.body) { received += chunk.length; transferHash.update(chunk); }
    const expectedHash = crypto.createHash('sha256'), zeroes = Buffer.alloc(1024 * 1024);
    for (let i = 0; i < 512; i++) expectedHash.update(zeroes);
    assert.equal(received, bytesToSend); assert.equal(transferHash.digest('hex'), expectedHash.digest('hex'));
    const memoryAfter = await memory();
    assert(memoryAfter - memoryBefore < 128 * 1024 * 1024, 'Server buffered a large portion of the file');
    console.log('PASS: 512 MiB streamed and SHA-256 verified with bounded server memory growth', Math.round((memoryAfter - memoryBefore) / 1024 / 1024) + ' MiB');
    response = await request(token, {method: 'HEAD', headers: {Range: 'bytes=0-1'}});
    assert.equal(response.status, 200); assert.equal(response.headers.get('content-length'), String(source.length)); assert.equal((await response.arrayBuffer()).byteLength, 0);
    response = await request(token, {headers: {'If-None-Match': 'W/' + etag}}); assert.equal(response.status, 304);
    response = await request(token, {headers: {'If-Match': '"wrong"'}}); assert.equal(response.status, 412); await response.body.cancel();
    response = await request(token, {headers: {Range: 'bytes=0-7', 'If-Range': '"old-version"'}}); assert.equal(response.status, 200); await response.body.cancel();
    response = await request(token, {headers: {Range: 'bytes=0-7', 'If-Range': etag}}); assert.equal(response.status, 206); await response.body.cancel();
    console.log('PASS: HEAD and conditional requests preserve correct resume semantics');
    const beforeAbort = await status(huge);
    const abort = new AbortController(); response = await request(huge, {signal: abort.signal}); const reader = response.body.getReader(); await reader.read(); abort.abort();
    await reader.cancel().catch(() => {});
    let stopped; for (let attempt = 0; attempt < 50; attempt++) { stopped = await status(huge); if (!stopped.active) break; await wait(50); }
    assert.equal(stopped.active, 0, 'Disconnected downloader still occupies a transfer slot');
    assert(stopped.bytes - beforeAbort.bytes < 128 * 1024 * 1024, 'Server kept reading the large file after disconnect');
    const prefix = source.subarray(0, 128 * 1024);
    response = await request(token, {headers: {Range: 'bytes=' + prefix.length + '-', 'If-Range': etag}});
    assert.equal(digest(Buffer.concat([prefix, Buffer.from(await response.arrayBuffer())])), digest(source));
    console.log('PASS: interrupted transfer can resume without corrupting the result');
    response = await request(tokens['zero.bin']); assert.equal(response.status, 200); assert.equal((await response.arrayBuffer()).byteLength, 0);
    response = await request(tokens['zero.bin'], {headers: {Range: 'bytes=0-0'}}); assert.equal(response.status, 416); await response.body.cancel();
    fs.writeFileSync(path.join(directory, 'fixtures/changed.txt'), 'changed after registration');
    response = await request(tokens['changed.txt']); assert.equal(response.status, 409); assert(!(await response.text()).includes(directory));
    fs.unlinkSync(path.join(directory, 'fixtures/deleted.txt'));
    response = await request(tokens['deleted.txt']); assert.equal(response.status, 404); await response.body.cancel();
    response = await request('0'.repeat(48)); assert.equal(response.status, 404); await response.body.cancel();
    response = await request(token, {method: 'POST', body: 'not an upload'}); assert.equal(response.status, 405); await response.body.cancel();
    console.log('PASS: empty, changed, missing and unshared files; no remote file-registration/upload API');
    for (const route of ['/stream.mp4', '/stream.mjpg', '/ScreenTask.jpg', '/audio', '/chat', '/rtc-signaling', '/host', '/direct']) {
      response = await fetch(origin + route); assert.equal(response.status, 403, route); await response.body.cancel();
    }
    response = await fetch(origin + '/%2e%2e%5cScreenTask.exe'); assert([403, 404].includes(response.status)); await response.body.cancel();
    console.log('PASS: file-only server blocks screen/audio endpoints and filesystem traversal');
    await command('PRIVATE', 'PRIVATE');
    for (const route of ['/files/' + token, '/file-info/' + token, '/download/' + token]) {
      response = await fetch(origin + route, {headers: {Authorization: 'Basic invalid!'}}); assert.equal(response.status, 401); await response.body.cancel();
    }
    response = await request(token, {headers: {Authorization: 'Basic ' + Buffer.from('test:transfer').toString('base64')}}); assert.equal(response.status, 200); await response.body.cancel();
    await command('PUBLIC', 'PUBLIC');
    console.log('PASS: private session authentication covers file pages, metadata and downloads');
    browser = await chromium.launch({channel: 'msedge', headless: true});
    const page = await browser.newPage(); const errors = []; page.on('pageerror', e => errors.push(e.message));
    const unicodeName = "ação & 'friend'.txt";
    await page.goto(origin + '/files/' + tokens[unicodeName]);
    await page.waitForFunction(() => !document.getElementById('file').hidden);
    assert.equal(await page.locator('#name').textContent(), unicodeName);
    const downloaded = page.waitForEvent('download'); await page.click('#download'); const download = await downloaded;
    assert.equal(download.suggestedFilename(), unicodeName);
    assert.equal(digest(fs.readFileSync(await download.path())), digest(fs.readFileSync(path.join(directory, 'fixtures', unicodeName))));
    await page.goto(origin + '/files/' + huge); await page.waitForFunction(() => !document.getElementById('file').hidden);
    assert.match(await page.locator('#size').textContent(), /6 GiB/);
    assert.deepEqual(errors, []);
    console.log('PASS: browser download uses disk and preserves Unicode names; multi-GiB page loads without buffering file');
    // Leave four large downloads unread to exercise the bounded transfer pool.
    const held = [];
    for (let i = 0; i < 4; i++) {
      const controller = new AbortController(); const reply = await request(huge, {signal: controller.signal}); assert.equal(reply.status, 200); held.push({controller, reply});
    }
    response = await request(huge); assert.equal(response.status, 429); await response.body.cancel();
    const workingSet = await page.request.get(origin + '/connection-info'); assert.equal(workingSet.status(), 200);
    await command('REVOKE ' + huge, 'REVOKED');
    for (const item of held) {
      let ended = false, count = 0;
      const timed = setTimeout(() => item.controller.abort(), 5000);
      try { for await (const chunk of item.reply.body) count += chunk.length; ended = true; }
      catch { ended = !item.controller.signal.aborted; }
      finally { clearTimeout(timed); }
      assert(ended && count < 128 * 1024 * 1024, 'Revocation did not interrupt the active transfer');
    }
    response = await request(huge); assert.equal(response.status, 404); await response.body.cancel();
    assert(fs.existsSync(path.join(directory, 'fixtures/large-6GiB.bin')));
    console.log('PASS: concurrency bounded; server stays responsive; revoking share stops downloads without deleting source');
    await command('OFFLINE', 'OFFLINE'); response = await request(token); assert.equal(response.status, 503); await response.body.cancel();
    await command('ONLINE', 'ONLINE'); response = await request(token); assert.equal(response.status, 200);
    assert.equal(digest(Buffer.from(await response.arrayBuffer())), digest(source));
    console.log('PASS: stopping serving rejects queued requests; restarting retains live share links');
  } finally {
    if (browser) await browser.close();
    fs.writeFileSync(stopFile, 'stop'); processHandle.stdin.end();
    await Promise.race([new Promise(resolve => processHandle.once('exit', resolve)), wait(5000)]);
    if (processHandle.exitCode === null) processHandle.kill();
    // All temporary fixtures, including the sparse file, belong to this test.
    await wait(150);
    const resolved = fs.realpathSync(directory), temporaryRoot = fs.realpathSync(os.tmpdir());
    if (path.dirname(resolved) !== temporaryRoot || !path.basename(resolved).startsWith('screentask-files-')) throw new Error('Unexpected test cleanup path');
    fs.rmSync(resolved, {recursive: true, force: true});
  }
}
main().catch(e => { console.error(e); console.error(stderr); process.exitCode = 1; });
