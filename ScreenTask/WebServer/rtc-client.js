/* Shared direct-only WebRTC lifecycle. Serialize signaling operations and keep
   callbacks scoped to their socket/peer so an old session cannot tear down a new one. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const host = !!$('start');
  const media = $(host ? 'preview' : 'video');
  const config = {iceServers: [{urls: ['stun:stun.l.google.com:19302', 'stun:stun.cloudflare.com:3478']}], iceTransportPolicy: 'all'};
  let nativeSource = null, browserCapture = null;
  let socket = null, pc = null, stream = null, room = '', chain = Promise.resolve();
  let pending = [], remoteSet = false, connectTimer = 0, peerTimer = 0, statsTimer = 0;
  let lastBytes = 0, lastFrames = 0, lastAt = 0, captureGeneration = 0;

  function status(text, good = false, bad = false) {
    $('status').textContent = text;
    if ($('dot')) $('dot').style.background = good ? '#42d392' : bad ? '#ef6262' : '#e1aa55';
  }
  function relay(candidate) {
    return candidate && (candidate.type === 'relay' || /\btyp relay\b/.test(candidate.candidate || ''));
  }
  function send(message) {
    if (socket && socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(message));
  }
  function resetPeer() {
    clearTimeout(peerTimer); clearInterval(statsTimer);
    const old = pc; pc = null;
    if (old) old.close();
    pending = []; remoteSet = false;
    lastBytes = lastFrames = lastAt = 0;
    if (!host) media.srcObject = null;
  }
  function stop(message) {
    ++captureGeneration;
    clearTimeout(connectTimer);
    const old = socket; socket = null;
    if (old) old.close();
    resetPeer();
    if (host) {
      if (stream) stream.getTracks().forEach(t => t.stop());
      stream = null; media.srcObject = null;
      if (nativeSource) { nativeSource.stop(); nativeSource = null; }
      if (browserCapture) { browserCapture.getTracks().forEach(t => t.stop()); browserCapture = null; }
      $('hdrNative').disabled = false; $('hdrAudio').disabled = false;
      $('start').disabled = false; $('stop').disabled = true;
      $('copy').disabled = true; $('copyLink').disabled = true;
      newRoom();
    } else {
      $('connect').disabled = false; $('disconnect').disabled = true;
      $('telemetry').textContent = 'Transport: not connected';
    }
    if (message) status(message, false, true);
  }
  function newRoom() {
    const bytes = new Uint8Array(18); crypto.getRandomValues(bytes);
    room = Array.from(bytes, b => b.toString(16).padStart(2, '0')).join('');
    $('room').textContent = room;
    refreshLink();
  }
  function invitation() {
    const url = new URL($('viewerAddress').value.trim());
    if (!['http:', 'https:'].includes(url.protocol) || url.username || url.password)
      throw new Error('Use an http:// or https:// viewer address without credentials.');
    url.pathname = '/direct'; url.search = ''; url.hash = '';
    url.searchParams.set('room', room);
    return url;
  }
  function refreshLink() {
    try { const url = invitation(); $('invite').href = url.href; $('invite').textContent = url.href; }
    catch (e) { $('invite').removeAttribute('href'); $('invite').textContent = e.message; }
  }
  async function copy(text) {
    try { await navigator.clipboard.writeText(text); status('Copied. Send it to your viewer.'); }
    catch { status('Clipboard unavailable. Select and copy the displayed text.'); }
  }
  function armPeerTimeout(peer) {
    clearTimeout(peerTimer);
    peerTimer = setTimeout(() => {
      if (pc !== peer || peer.connectionState === 'connected') return;
      const text = 'Signaling works, but the direct media path timed out. Check UDP/firewall/NAT rules on both PCs. No relay is configured.';
      if (host) { resetPeer(); status(text, false, true); }
      else stop(text);
    }, 30000);
  }
  async function sample(peer) {
    if (pc !== peer) return;
    try {
      const reports = await peer.getStats();
      if (pc !== peer) return;
      let pair = null, fallback = null, inbound = null;
      reports.forEach(r => {
        if (r.type === 'transport' && r.selectedCandidatePairId) pair = reports.get(r.selectedCandidatePairId);
        if (r.type === 'candidate-pair' && (r.selected || r.nominated && r.state === 'succeeded')) fallback = r;
        if (r.type === 'inbound-rtp' && (r.kind || r.mediaType) === 'video') inbound = r;
      });
      pair = pair || fallback;
      if (!pair) return;
      const local = reports.get(pair.localCandidateId), remote = reports.get(pair.remoteCandidateId);
      if (!local || !remote) return;
      if (local.candidateType === 'relay' || remote.candidateType === 'relay') {
        stop('Relay route rejected. Direct connections only.'); return;
      }
      const path = `${local.candidateType} → ${remote.candidateType} · ${local.protocol || 'ICE'}`;
      const rtt = ((pair.currentRoundTripTime || 0) * 1000).toFixed(0);
      status(`Direct connected · ${path} · ${rtt} ms RTT`, true);
      if (!host) {
        const at = performance.now(), elapsed = at - lastAt;
        const rate = inbound && lastAt && inbound.bytesReceived >= lastBytes ?
          ((inbound.bytesReceived - lastBytes) * 8 / elapsed / 1000).toFixed(2) + ' Mbps' : '—';
        const fps = inbound && Number.isFinite(inbound.framesPerSecond) ? inbound.framesPerSecond :
          inbound && lastAt ? Math.max(0, ((inbound.framesDecoded || 0) - lastFrames) * 1000 / elapsed) : 0;
        if (inbound) { lastBytes = inbound.bytesReceived; lastFrames = inbound.framesDecoded || 0; lastAt = at; }
        $('telemetry').textContent = `Transport: DIRECT\nPair: ${path}\nRTT / jitter / cumulative loss: ${rtt} ms / ${((inbound?.jitter || 0) * 1000).toFixed(1)} ms / ${inbound?.packetsLost || 0}\nReceive rate / decoded FPS: ${rate} / ${fps.toFixed(1)} FPS`;
      }
    } catch { /* Stats are optional; they must not interrupt the stream. */ }
  }
  function createPeer() {
    resetPeer();
    const peer = pc = new RTCPeerConnection(config);
    peer.onicecandidate = e => { if (pc === peer && e.candidate && !relay(e.candidate)) send({type: 'candidate', candidate: e.candidate}); };
    peer.ontrack = e => {
      if (pc !== peer || host) return;
      if (!media.srcObject) media.srcObject = new MediaStream();
      if (!media.srcObject.getTracks().some(t => t.id === e.track.id)) media.srcObject.addTrack(e.track);
      media.play().catch(() => { if (pc === peer) status('Connected. Press Play on the video to allow playback.'); });
    };
    const check = () => {
      if (pc !== peer) return;
      const state = peer.connectionState || peer.iceConnectionState;
      if (['connected', 'completed'].includes(state)) {
        clearTimeout(peerTimer); status('Direct connected', true);
        clearInterval(statsTimer); statsTimer = setInterval(() => sample(peer), 1000); sample(peer);
      } else if (state === 'failed' || peer.iceConnectionState === 'failed') {
        const text = 'Signaling works, but direct ICE checks failed. Check UDP/firewall/NAT rules. No relay fallback.';
        if (host) { resetPeer(); status(text, false, true); } else stop(text);
      } else if (state === 'disconnected') { status('Direct path interrupted; waiting for recovery…'); armPeerTimeout(peer); }
    };
    peer.onconnectionstatechange = check; peer.oniceconnectionstatechange = check;
    armPeerTimeout(peer);
    return peer;
  }
  async function flush(peer) {
    while (pc === peer && pending.length) await peer.addIceCandidate(pending.shift());
  }
  async function handle(message, currentSocket) {
    if (socket !== currentSocket) return;
    if (message.type === 'joined') {
      clearTimeout(connectTimer);
      if (host) { $('copy').disabled = false; $('copyLink').disabled = false; }
      status(host ? 'Signaling ready. Waiting for your viewer…' : 'Signaling ready. Waiting for the host to start sharing with this code…');
      return;
    }
    if (message.type === 'busy') { stop(host ? 'Pairing code already in use. Start again.' : 'Another viewer is using this code. Disconnect that viewer first.'); return; }
    if (message.type === 'peer-left') {
      if (host) { resetPeer(); status('Viewer disconnected. Ready for another viewer with the same invitation.'); }
      else stop('Host stopped sharing or disconnected. Ask for their current invitation.');
      return;
    }
    if (message.type === 'peer-ready') {
      clearTimeout(connectTimer);
      if (!host) { status('Host found. Waiting for the screen offer…'); return; }
      if (pc) return;
      const peer = createPeer();
      for (const track of stream.getTracks()) peer.addTrack(track, stream);
      await peer.setLocalDescription(await peer.createOffer());
      if (socket !== currentSocket || pc !== peer) return;
      send({type: 'offer', description: peer.localDescription});
      // Parameters are most portable after the offer establishes sender encodings.
      for (const sender of peer.getSenders()) {
        if (sender.track?.kind !== 'video') continue;
        try {
          const p = sender.getParameters();
          if (p.encodings?.length) { p.encodings[0].maxBitrate = 3000000; p.encodings[0].maxFramerate = 60; await sender.setParameters(p); }
        } catch { /* Let this browser negotiate its default encoding. */ }
      }
      if (pc === peer) status('Offer sent. Negotiating the direct media path…');
      return;
    }
    if (message.type === 'offer' && !host) {
      const peer = pc || createPeer();
      await peer.setRemoteDescription(message.description);
      if (pc !== peer || socket !== currentSocket) return;
      remoteSet = true; await flush(peer);
      await peer.setLocalDescription(await peer.createAnswer());
      if (pc !== peer || socket !== currentSocket) return;
      send({type: 'answer', description: peer.localDescription}); status('Negotiating the direct media path…');
    } else if (message.type === 'answer' && host && pc) {
      const peer = pc; await peer.setRemoteDescription(message.description);
      if (pc !== peer || socket !== currentSocket) return;
      remoteSet = true; await flush(peer);
    } else if (message.type === 'candidate' && message.candidate && !relay(message.candidate)) {
      if (!pc && !host) createPeer();
      if (pc) { if (remoteSet) await pc.addIceCandidate(message.candidate); else pending.push(message.candidate); }
    }
  }
  function connectSignal() {
    const url = new URL('/rtc-signaling', location.href);
    url.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    url.searchParams.set('role', host ? 'host' : 'viewer'); url.searchParams.set('room', room);
    const current = socket = new WebSocket(url);
    chain = Promise.resolve();
    const reachability = `Could not open signaling at ${location.host}. Check the exact address and port, that ScreenTask is running, and firewall/TCP port forwarding. Private sessions also require signing in on this page.`;
    status(`Connecting to signaling at ${location.host}…`);
    clearTimeout(connectTimer);
    connectTimer = setTimeout(() => { if (socket === current) stop(reachability); }, 10000);
    current.onopen = () => { if (socket === current) status('Signaling socket open. Confirming pairing…'); };
    current.onmessage = e => {
      if (socket !== current) return;
      let message; try { message = JSON.parse(e.data); } catch { return; }
      chain = chain.then(() => handle(message, current)).catch(err => {
        if (socket === current) stop('WebRTC negotiation failed: ' + err.message);
      });
    };
    current.onerror = () => { if (socket === current) stop(reachability); };
    current.onclose = e => { if (socket === current) stop(`Signaling closed (${e.code}). Check the host server and port ${location.port || (location.protocol === 'https:' ? '443' : '80')}. Start again to reconnect.`); };
  }
  if (host) {
    let nativeChoiceMade = false;
    $('hdrNative').onchange = () => { nativeChoiceMade = true; };
    newRoom();
    $('viewerAddress').value = location.origin;
    refreshLink(); $('viewerAddress').oninput = refreshLink;
    fetch('/connection-info', {cache: 'no-store'}).then(r => r.ok ? r.json() : null).then(info => {
      if (info && !nativeChoiceMade && !$('start').disabled) $('hdrNative').checked = !!info.hdrCorrection;
      if (info && $('viewerAddress').value === location.origin) { $('viewerAddress').value = info.viewerOrigin; refreshLink(); }
    }).catch(() => {});
    $('start').onclick = async () => {
      if (!window.isSecureContext || !window.RTCPeerConnection || (!$('hdrNative').checked || $('hdrAudio').checked) && !navigator.mediaDevices?.getDisplayMedia) {
        status('Open the host page on localhost or HTTPS in a browser with screen sharing support.', false, true); return;
      }
      const generation = ++captureGeneration;
      $('start').disabled = true; $('stop').disabled = false;
      $('hdrNative').disabled = true; $('hdrAudio').disabled = true;
      try {
        let captured;
        if ($('hdrNative').checked) {
          // Invoke the optional screen/audio picker immediately within the click
          // activation, before waiting for the native HDR decoder to become ready.
          if ($('hdrAudio').checked) {
            const audioCapture = await navigator.mediaDevices.getDisplayMedia({video: true, audio: true});
            if (generation !== captureGeneration) { audioCapture.getTracks().forEach(t => t.stop()); return; }
            browserCapture = audioCapture;
            audioCapture.getVideoTracks()[0]?.addEventListener('ended', () => { if (browserCapture === audioCapture) stop('Browser audio capture ended.'); }, {once: true});
          }
          nativeSource = ScreenTaskHdrSource.create();
          const currentSource = nativeSource;
          currentSource.onerror = err => { if (nativeSource === currentSource) stop(err.message); };
          status('Preparing the HDR-corrected monitor stream…');
          captured = await currentSource.ready;
          if (browserCapture) for (const track of browserCapture.getAudioTracks()) captured.addTrack(track);
        } else captured = await navigator.mediaDevices.getDisplayMedia({video: {frameRate: {ideal: 60, max: 60}}, audio: true});
        if (generation !== captureGeneration) { captured.getTracks().forEach(t => t.stop()); return; }
        stream = captured; media.srcObject = stream;
        const videoTrack = stream.getVideoTracks()[0];
        if (!videoTrack) throw new Error('No screen video track was returned.');
        videoTrack.addEventListener('ended', () => { if (stream === captured) stop('Screen sharing ended.'); }, {once: true});
        refreshLink(); connectSignal();
      } catch (err) {
        if (generation === captureGeneration) stop(err.name === 'NotAllowedError' ? 'Screen sharing was cancelled or denied.' : 'Could not start sharing: ' + err.message);
      }
    };
    $('copy').onclick = () => copy(room);
    $('copyLink').onclick = () => { try { copy(invitation().href); } catch (e) { status(e.message, false, true); } };
    $('stop').onclick = () => stop('Sharing stopped.');
  } else {
    $('connect').onclick = () => {
      const text = $('room').value.trim();
      if (/^https?:\/\//i.test(text)) {
        try {
          const url = new URL(text), token = url.searchParams.get('room');
          if (url.username || url.password || !/^[A-Za-z0-9_-]{16,128}$/.test(token || '')) throw new Error('Invalid invitation');
          if (url.origin !== location.origin) { url.pathname = '/direct'; url.hash = ''; location.assign(url.href); return; }
          $('room').value = token;
        } catch { status('Paste a complete ScreenTask invitation link containing the pairing code.', false, true); return; }
      }
      room = $('room').value.trim();
      if (!/^[A-Za-z0-9_-]{16,128}$/.test(room)) { status('Enter the complete pairing code or invitation link.', false, true); return; }
      if (!window.RTCPeerConnection || !window.WebSocket) { status('This browser does not support WebRTC. Try a current browser.', false, true); return; }
      $('connect').disabled = true; $('disconnect').disabled = false;
      try { connectSignal(); } catch (err) { stop('Could not start signaling: ' + err.message); }
    };
    $('disconnect').onclick = () => stop('Disconnected.');
    const token = new URLSearchParams(location.search).get('room');
    if (token) $('room').value = token;
  }
  window.addEventListener('pagehide', () => stop());
})();
