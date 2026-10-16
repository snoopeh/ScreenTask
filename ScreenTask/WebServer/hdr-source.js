/* Native tone-mapped video feeds a stable canvas track. Decoder reconnects
   after live capture changes without ending the direct peer's video track. */
window.ScreenTaskHdrSource = {
  create() {
    if (!window.MediaSource || !HTMLCanvasElement.prototype.captureStream)
      throw new Error('Native HDR sharing requires MediaSource and canvas captureStream. Try Chrome or Edge.');
    const video = document.createElement('video'), canvas = document.createElement('canvas');
    video.muted = true; video.autoplay = true; video.playsInline = true;
    video.style.cssText = 'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none'; document.body.appendChild(video);
    let controller = null, reader = null, url = null, captured = null, settled = false, stopped = false;
    let resolveReady, rejectReady, retryTimer, paintFrame, failureSince = 0;
    const ready = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
    const source = {ready, onerror: null, stop};
    const startup = setTimeout(() => fatal(new Error('Native HDR capture timed out. Check the selected display and Video settings.')), 25000);
    function cleanupDecoder() {
      if (paintFrame != null) { if (video.cancelVideoFrameCallback) video.cancelVideoFrameCallback(paintFrame); else cancelAnimationFrame(paintFrame); paintFrame = null; }
      controller?.abort(); reader?.cancel().catch(() => {}); reader = null;
      video.pause(); video.removeAttribute('src'); video.load();
      if (url) URL.revokeObjectURL(url); url = null;
    }
    function stop() {
      if (stopped) return; stopped = true; clearTimeout(startup); clearTimeout(retryTimer);
      cleanupDecoder(); if (captured) captured.getTracks().forEach(t => t.stop()); video.remove();
      if (!settled) { settled = true; rejectReady(new Error('HDR capture was stopped.')); }
    }
    function fatal(error) {
      if (stopped) return;
      if (!settled) { settled = true; rejectReady(error); }
      else if (source.onerror) source.onerror(error);
      stop();
    }
    function reconnect(error) {
      if (stopped) return;
      if (!failureSince) failureSince = Date.now();
      if (Date.now() - failureSince > 30000) { fatal(error); return; }
      clearTimeout(retryTimer); cleanupDecoder(); retryTimer = setTimeout(connect, 400);
    }
    function paint() {
      paintFrame = null;
      if (stopped) return;
      if (video.readyState >= 2 && video.videoWidth && captured) {
        if (canvas.width !== video.videoWidth || canvas.height !== video.videoHeight) { canvas.width = video.videoWidth; canvas.height = video.videoHeight; }
        canvas.getContext('2d', {alpha: false}).drawImage(video, 0, 0, canvas.width, canvas.height);
        captured.getVideoTracks()[0]?.requestFrame?.();
      }
      paintFrame = video.requestVideoFrameCallback ? video.requestVideoFrameCallback(paint) : requestAnimationFrame(paint);
    }
    video.addEventListener('error', () => reconnect(new Error('Native video decoder is reconnecting.')));
    video.addEventListener('loadeddata', async () => {
      if (stopped) return;
      try {
        await video.play(); if (stopped) return;
        failureSince = 0;
        if (!captured) {
          canvas.width = video.videoWidth; canvas.height = video.videoHeight;
          canvas.getContext('2d', {alpha: false}).drawImage(video, 0, 0);
          captured = canvas.captureStream(0);
          if (!captured.getVideoTracks()[0]?.requestFrame) { captured.getTracks().forEach(t => t.stop()); captured = canvas.captureStream(60); }
          paint(); settled = true; clearTimeout(startup); resolveReady(captured);
        }
        else if (paintFrame == null) paint();
      } catch (error) { reconnect(error); }
    });
    async function connect() {
      if (stopped) return;
      const current = controller = new AbortController(), mediaSource = new MediaSource();
      url = URL.createObjectURL(mediaSource);
      mediaSource.addEventListener('sourceopen', async () => {
        try {
          const response = await fetch('/stream.mp4', {cache: 'no-store', signal: current.signal});
          if (!response.ok) throw new Error('Native capture returned HTTP ' + response.status);
          if (!settled && response.headers.get('X-Stream-HDR-Corrected') !== 'true') {
            fatal(new Error('Enable HDR color correction on ScreenTask’s Video tab before using HDR monitor sharing.')); return;
          }
          const mime = 'video/mp4; codecs="' + response.headers.get('X-Stream-Codecs') + '"';
          if (!MediaSource.isTypeSupported(mime)) { fatal(new Error('This browser does not support the native video codec.')); return; }
          if (current.signal.aborted) return;
          const buffer = mediaSource.addSourceBuffer(mime); buffer.mode = 'segments';
          const queue = []; let bytes = new Uint8Array(), initialized = false, init = [], prefix = [], fragment = null, started = false;
          const join = parts => { const value = new Uint8Array(parts.reduce((n, p) => n + p.length, 0)); let offset = 0; for (const p of parts) { value.set(p, offset); offset += p.length; } return value; };
          function drain() {
            if (stopped || current.signal.aborted || buffer.updating || !queue.length) return;
            try {
              if (video.buffered.length) {
                const start = video.buffered.start(0), end = video.buffered.end(video.buffered.length - 1);
                if (!started || end - video.currentTime > 1.5) { video.currentTime = Math.max(start, end - .25); started = true; }
                if (end - start > 12) { buffer.remove(start, end - 8); return; }
              }
              buffer.appendBuffer(queue.shift());
            } catch (error) { if (!current.signal.aborted) reconnect(error); }
          }
          function enqueue(parts) { queue.push(join(parts)); if (queue.length > 20) throw new Error('Native decoder fell behind. Lower the frame rate or width.'); drain(); }
          buffer.addEventListener('error', () => { if (!current.signal.aborted) reconnect(new Error('Native decoder rejected a fragment.')); });
          buffer.addEventListener('updateend', drain);
          const input = reader = response.body.getReader();
          while (!stopped && !current.signal.aborted) {
            const chunk = await input.read(); if (chunk.done) throw new Error('Native capture changed. Reconnecting…');
            bytes = join([bytes, chunk.value]);
            while (bytes.length >= 8) {
              const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.length); let size = view.getUint32(0), header = 8;
              if (size === 1) { if (bytes.length < 16) break; header = 16; size = view.getUint32(8) * 4294967296 + view.getUint32(12); }
              if (size < header || size > 67108864) throw new Error('Invalid native video fragment.');
              if (bytes.length < size) break;
              const type = String.fromCharCode(...bytes.slice(4, 8)), box = bytes.slice(0, size); bytes = bytes.slice(size);
              if (!initialized) { init.push(box); if (type === 'moov') { initialized = true; enqueue(init); init = []; } }
              else if (type === 'moof') { fragment = prefix.concat([box]); prefix = []; }
              else if (fragment) { fragment.push(box); if (type === 'mdat') { enqueue(fragment); fragment = null; } }
              else { prefix.push(box); if (prefix.length > 8) prefix.shift(); }
            }
          }
        } catch (error) { if (!current.signal.aborted) reconnect(error); }
      }, {once: true});
      video.src = url;
    }
    connect(); return source;
  }
};
