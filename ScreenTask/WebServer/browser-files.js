/* File inputs stay in the sender's browser. Only requested slices are read.
   Downloads use the browser download manager, including on ordinary HTTP. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id), files = new Map(), readers = new Map();
  const selected = $('shareFiles'), create = $('createShares'), note = $('sharingStatus'), address = $('shareOrigin');
  let socket = null, connecting = null, pageClosing = false, progressTimer;
  const base = () => (location.protocol === 'https:' ? 'wss://' : 'ws://') + location.host;
  const random = () => [...crypto.getRandomValues(new Uint8Array(24))].map(n => n.toString(16).padStart(2, '0')).join('');
  const size = number => { let n = Number(number), i = 0; const units = ['B', 'KiB', 'MiB', 'GiB', 'TiB', 'PiB']; while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; } return n.toLocaleString(undefined, {maximumFractionDigits: i ? 2 : 0}) + ' ' + units[i]; };
  const diagnostics = window.ScreenTaskFilesDiagnostics = {maxBufferedBytes: 0, maxInflightBytes: 0};
  function origin() {
    const value = new URL(address.value.trim() || location.origin);
    if (!['http:', 'https:'].includes(value.protocol) || value.username || value.password || value.pathname !== '/' || value.search || value.hash) throw new Error('Enter a reachable http:// or https:// server address and port, without a path.');
    return value.origin;
  }
  function updateLink(item) { if (item.token) item.link.value = origin() + '/files?share=' + item.token; }
  address.value = location.origin;
  fetch('/connection-info', {cache: 'no-store'}).then(r => r.json()).then(info => {
    if (['localhost', '127.0.0.1', '[::1]'].includes(location.hostname) && address.value === location.origin) {
      address.value = info.viewerOrigin || location.origin; for (const item of files.values()) updateLink(item);
    }
  }).catch(() => {});
  address.addEventListener('change', () => { try { for (const item of files.values()) updateLink(item); note.textContent = 'Link addresses updated. Make sure your friend can reach this address and port.'; } catch (e) { note.textContent = e.message; } });
  async function copy(input) {
    try { if (navigator.clipboard) await navigator.clipboard.writeText(input.value); else { input.select(); if (!document.execCommand('copy')) throw new Error(); } note.textContent = 'Link copied. Keep this page open until the download finishes.'; }
    catch { input.select(); note.textContent = 'Copy the selected link from the box.'; }
  }
  function addRow(file) {
    const item = {file, id: random(), token: null};
    const row = item.row = document.createElement('article'); row.className = 'card browserShare'; row.dataset.fileId = item.id;
    const name = document.createElement('h3'); name.textContent = file.name;
    const meta = document.createElement('div'); meta.className = 'meta'; meta.textContent = size(file.size);
    item.link = document.createElement('input'); item.link.readOnly = true; item.link.placeholder = 'Creating share link…'; item.link.setAttribute('aria-label', 'Share link for ' + file.name);
    const actions = document.createElement('div'); actions.className = 'actions';
    const copyButton = item.copy = document.createElement('button'); copyButton.textContent = 'Copy link'; copyButton.disabled = true; copyButton.onclick = () => copy(item.link);
    const remove = document.createElement('button'); remove.textContent = 'Stop sharing'; remove.onclick = () => {
      if (socket?.readyState === WebSocket.OPEN && item.token) socket.send(JSON.stringify({type: 'remove', token: item.token}));
      for (const reader of readers.values()) if (reader.id === item.id) reader.socket.close();
      files.delete(item.id); row.remove(); note.textContent = 'Sharing stopped. The original file stays on your PC.';
    };
    actions.append(copyButton, remove);
    item.progress = document.createElement('div'); item.progress.className = 'shareTransfers'; item.progress.textContent = 'Waiting for your friend to download.';
    row.append(name, meta, item.link, actions, item.progress); $('sharedFiles').append(row); files.set(item.id, item); return item;
  }
  function renderProgress(container, p, cancel) {
    const total = Number(p.size), sent = Number(p.sent), rate = p.speed || 0, percent = total ? Math.min(100, sent * 100 / total) : p.state === 'complete' ? 100 : 0;
    const progress = document.createElement('progress'); progress.max = 100; progress.value = percent;
    const label = document.createElement('div'); label.className = 'meta transferTelemetry';
    const eta = rate > 0 && p.state === 'sending' ? ' · ' + Math.ceil((total - sent) / rate) + ' s remaining' : '';
    label.textContent = `${p.state} · ${percent.toFixed(1)}% · ${size(sent)} / ${size(total)} · ${size(rate)}/s${eta}`;
    const identity = document.createElement('div'); identity.className = 'hint'; identity.textContent = p.transfer ? 'Download ' + p.transfer.slice(-6) : 'Download progress';
    container.replaceChildren(identity, progress, label);
    if (cancel && ['waiting', 'sending'].includes(p.state)) { const button = document.createElement('button'); button.textContent = 'Cancel download'; button.onclick = cancel; container.append(button); }
  }
  function serve(request) {
    const item = files.get(request.id);
    if (!item || item.token !== request.token || !/^[a-f0-9]{48}$/.test(request.nonce)) return;
    const start = Number(request.start), length = Number(request.length);
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(length) || start < 0 || length < 0 || length > item.file.size - start) return;
    const source = new WebSocket(base() + '/file-source/' + request.nonce);
    readers.set(request.nonce, {id: item.id, socket: source});
    let sent = 0, acknowledged = 0, reading = false, stopped = false;
    async function pump() {
      if (reading || stopped || source.readyState !== WebSocket.OPEN) return;
      reading = true;
      try {
        while (!stopped && source.readyState === WebSocket.OPEN && sent < length && sent - acknowledged < 1024 * 1024 && source.bufferedAmount < 1024 * 1024) {
          // A WebSocket receive/ACK can split a chunk at any byte. Reserve the
          // actual remaining credit, rather than assuming 64 KiB-aligned ACKs.
          const count = Math.min(64 * 1024, length - sent, 1024 * 1024 - (sent - acknowledged), Math.floor(1024 * 1024 - source.bufferedAmount - 1024));
          if (count <= 0) break;
          const data = await item.file.slice(start + sent, start + sent + count).arrayBuffer();
          if (stopped || source.readyState !== WebSocket.OPEN) break;
          if (data.byteLength !== count) throw new Error('The selected file changed or could not be read.');
          source.send(data); sent += count;
          diagnostics.maxBufferedBytes = Math.max(diagnostics.maxBufferedBytes, source.bufferedAmount);
          diagnostics.maxInflightBytes = Math.max(diagnostics.maxInflightBytes, sent - acknowledged);
        }
      } catch (e) { note.textContent = e.message || 'Could not read the selected file. Select it again.'; source.close(); }
      finally { reading = false; }
    }
    source.onopen = pump;
    source.onmessage = event => { const value = Number(event.data); if (!Number.isSafeInteger(value) || value < acknowledged || value > sent) { source.close(); return; } acknowledged = value; pump(); };
    source.onerror = () => { note.textContent = 'A download connection was interrupted. Your friend can retry or resume while this page stays open.'; };
    source.onclose = () => { stopped = true; readers.delete(request.nonce); };
  }
  function connect() {
    if (socket?.readyState === WebSocket.OPEN) return Promise.resolve(socket);
    if (connecting) return connecting;
    connecting = new Promise((resolve, reject) => {
      const current = socket = new WebSocket(base() + '/file-control');
      const timeout = setTimeout(() => { reject(new Error('Could not reach the file server. Check its address and port.')); current.close(); }, 15000);
      current.onmessage = event => {
        let message; try { message = JSON.parse(event.data); } catch { return; }
        if (message.type === 'ready') { clearTimeout(timeout); resolve(current); }
        else if (message.type === 'shared') {
          const item = files.get(message.id); if (!item) return; item.token = message.token; item.copy.disabled = false; updateLink(item);
          note.textContent = 'Links are ready. Files stay on your PC; keep this browser page open.';
        } else if (message.type === 'read') serve(message);
        else if (message.type === 'error') note.textContent = message.message;
        else if (message.type === 'progress') {
          for (const item of files.values()) {
            const transfers = message.transfers.filter(t => t.token === item.token); if (!transfers.length) continue;
            const rows = transfers.map(t => { const div = document.createElement('div'); div.className = 'transfer'; div.dataset.transfer = t.transfer; renderProgress(div, t.progress, () => { if (current.readyState === WebSocket.OPEN) current.send(JSON.stringify({type: 'cancel', transfer: t.transfer})); }); return div; });
            item.progress.replaceChildren(...rows);
          }
        }
      };
      current.onerror = () => { clearTimeout(timeout); reject(new Error('File sharing could not connect. Check the server address and port.')); };
      current.onclose = () => {
        clearTimeout(timeout); connecting = null; reject(new Error('File sharing disconnected.'));
        for (const item of files.values()) { item.copy.disabled = true; item.token = null; item.link.value = ''; item.link.placeholder = 'Reconnect to create a new link'; }
        if (!pageClosing) { note.textContent = 'Connection closed. Reconnect to create fresh links for your selected files.'; $('reconnectShares').hidden = files.size === 0; }
      };
    });
    return connecting;
  }
  async function register() {
    const control = await connect(); origin();
    for (const item of files.values()) if (!item.token) control.send(JSON.stringify({type: 'add', id: item.id, name: item.file.name, size: item.file.size}));
    $('reconnectShares').hidden = true;
  }
  create.onclick = async () => {
    if (!selected.files.length) { note.textContent = 'Choose one or more files first.'; return; }
    if (selected.files.length + files.size > 64) { note.textContent = 'Share up to 64 files at a time.'; return; }
    create.disabled = true;
    try { origin(); for (const file of selected.files) addRow(file); selected.value = ''; await register(); }
    catch (e) { note.textContent = e.message; }
    finally { create.disabled = false; }
  };
  $('browserSend').addEventListener('dragover', event => { event.preventDefault(); });
  $('browserSend').addEventListener('drop', event => { event.preventDefault(); if(event.dataTransfer.files.length){ selected.files = event.dataTransfer.files; note.textContent = 'Files selected. Click Create share links to share them.'; } });
  window.addEventListener('pageshow', event => { if(event.persisted){ pageClosing = false; if(files.size && socket?.readyState !== WebSocket.OPEN){ $('reconnectShares').hidden = false; note.textContent = 'Reconnect to create fresh share links.'; } } });
  $('reconnectShares').onclick = () => register().catch(e => { note.textContent = e.message; });

  const token = new URLSearchParams(location.search).get('share');
  if (token) {
    $('join').hidden = true; $('browserReceive').hidden = false; $('filePanel').insertBefore($('browserReceive'), $('browserSend')); window.showSidebarPanel('files');
    const status = $('receiveStatus'), download = $('browserDownload');
    (async () => {
      try {
        if (!/^[a-f0-9]{48}$/.test(token)) throw new Error('This share link is invalid.');
        const response = await fetch('/browser-file-info/' + token, {cache: 'no-store'});
        if (!response.ok) throw new Error(await response.text());
        const info = await response.json(); $('receiveName').textContent = info.name; $('receiveSize').textContent = size(info.size);
        status.textContent = 'Ready. The sender must keep their sharing page open.'; download.hidden = false;
        download.onclick = () => {
          const transfer = random(); download.href = info.download + '?transfer=' + transfer;
          clearInterval(progressTimer); status.textContent = 'Starting download. Your browser saves directly to disk.';
          progressTimer = setInterval(async () => {
            try {
              const response = await fetch('/file-progress/' + token + '?transfer=' + transfer, {cache: 'no-store'});
              if (!response.ok) { clearInterval(progressTimer); throw new Error('The share is no longer available.'); }
              const progress = await response.json(); renderProgress($('receiveProgress'), progress);
              if (['complete', 'cancelled', 'interrupted'].includes(progress.state)) {
                clearInterval(progressTimer); status.textContent = progress.state === 'complete' ? 'Transfer sent to your browser. Check Downloads for the saved file.' : 'Transfer ' + progress.state + '. Retry or resume in your browser Downloads.';
              }
            } catch (e) { status.textContent = e.message || 'Transfer telemetry is unavailable.'; }
          }, 700);
        };
      } catch (e) { status.textContent = e.message || 'Could not reach the sender.'; }
    })();
  }
  window.addEventListener('pagehide', () => { pageClosing = true; clearInterval(progressTimer); socket?.close(); for (const reader of readers.values()) reader.socket.close(); });
  window.addEventListener('beforeunload', event => { if (files.size && !pageClosing) { event.preventDefault(); event.returnValue = ''; } });
})();
