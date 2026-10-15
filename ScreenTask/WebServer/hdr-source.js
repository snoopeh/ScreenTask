/* Receives the locally tone-mapped SDR stream and exposes its decoded video
   track to WebRTC. No media is sent to a third-party service. */
window.ScreenTaskHdrSource = {
  create() {
    if (!window.MediaSource || !HTMLMediaElement.prototype.captureStream && !HTMLMediaElement.prototype.mozCaptureStream)
      throw new Error('HDR monitor sharing requires MediaSource and video captureStream. Try Chrome or Edge.');
    const video = document.createElement('video'); video.muted = true; video.autoplay = true; video.playsInline = true;
    video.style.cssText = 'position:fixed;width:1px;height:1px;opacity:0;pointer-events:none'; document.body.appendChild(video);
    const controller = new AbortController(), mediaSource = new MediaSource(), url = URL.createObjectURL(mediaSource);
    let reader = null, captured = null, settled = false, stopped = false, resolveReady, rejectReady, timer;
    const ready = new Promise((resolve, reject) => { resolveReady = resolve; rejectReady = reject; });
    const source = {ready, onerror: null, stop};
    function stop() {
      if (stopped) return; stopped = true; clearTimeout(timer); controller.abort();
      if (reader) reader.cancel().catch(() => {});
      if (captured) captured.getTracks().forEach(t => t.stop());
      video.pause(); video.removeAttribute('src'); video.load(); video.remove(); URL.revokeObjectURL(url);
      if (!settled) { settled = true; rejectReady(new Error('HDR capture was stopped.')); }
    }
    function fail(error) {
      if (stopped) return;
      if (!settled) { settled = true; rejectReady(error); }
      else if (source.onerror) source.onerror(error);
      stop();
    }
    video.addEventListener('error', () => fail(new Error('The HDR-corrected stream could not be decoded.')));
    video.addEventListener('loadeddata', async () => {
      if (settled || stopped) return;
      try {
        await video.play(); if (stopped) return;
        captured = (video.captureStream || video.mozCaptureStream).call(video);
        if (!captured.getVideoTracks().length) throw new Error('This browser did not expose a decoded video track.');
        settled = true; clearTimeout(timer); resolveReady(captured);
      } catch (e) { fail(e); }
    }, {once: true});
    timer = setTimeout(() => fail(new Error('HDR capture timed out. Enable HDR correction on the Video tab and check the ScreenTask log.')), 20000);
    mediaSource.addEventListener('sourceopen', async () => {
      try {
        const response = await fetch('/stream.mp4', {cache: 'no-store', signal: controller.signal});
        if (!response.ok) throw new Error('HDR capture returned HTTP ' + response.status);
        if (response.headers.get('X-Stream-HDR-Corrected') !== 'true') throw new Error('Enable HDR color correction on ScreenTask’s Video tab before using HDR monitor sharing.');
        const mime = 'video/mp4; codecs="' + response.headers.get('X-Stream-Codecs') + '"';
        if (!MediaSource.isTypeSupported(mime)) throw new Error('This browser does not support the HDR-corrected SDR codec.');
        const buffer = mediaSource.addSourceBuffer(mime); buffer.mode = 'segments';
        const queue = []; let bytes = new Uint8Array(), initialized = false, init = [], prefix = [], fragment = null, started = false;
        function join(parts) { const result = new Uint8Array(parts.reduce((n, p) => n + p.length, 0)); let offset = 0; for (const p of parts) { result.set(p, offset); offset += p.length; } return result; }
        function drain() {
          if (stopped || buffer.updating || !queue.length) return;
          try {
            if (video.buffered.length) {
              const start = video.buffered.start(0), end = video.buffered.end(video.buffered.length - 1);
              if (!started || end - video.currentTime > 1.5) { video.currentTime = Math.max(start, end - .25); started = true; }
              if (end - start > 12) { buffer.remove(start, end - 8); return; }
            }
            buffer.appendBuffer(queue.shift());
          } catch (e) { fail(e); }
        }
        function enqueue(parts) { queue.push(join(parts)); if (queue.length > 20) throw new Error('HDR decoder fell behind. Lower the frame rate or output width.'); drain(); }
        buffer.addEventListener('error', () => fail(new Error('The HDR-corrected video decoder rejected a fragment.')));
        buffer.addEventListener('updateend', drain);
        reader = response.body.getReader();
        while (!stopped) {
          const chunk = await reader.read(); if (chunk.done) throw new Error('HDR capture ended. Restart direct sharing after changing Video settings.');
          bytes = join([bytes, chunk.value]);
          while (bytes.length >= 8) {
            const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.length); let size = view.getUint32(0), header = 8;
            if (size === 1) { if (bytes.length < 16) break; header = 16; size = view.getUint32(8) * 4294967296 + view.getUint32(12); }
            if (size < header || size > 67108864) throw new Error('Invalid HDR stream fragment.');
            if (bytes.length < size) break;
            const type = String.fromCharCode(...bytes.slice(4, 8)), box = bytes.slice(0, size); bytes = bytes.slice(size);
            if (!initialized) { init.push(box); if (type === 'moov') { initialized = true; enqueue(init); init = []; } }
            else if (type === 'moof') { fragment = prefix.concat([box]); prefix = []; }
            else if (fragment) { fragment.push(box); if (type === 'mdat') { enqueue(fragment); fragment = null; } }
            else { prefix.push(box); if (prefix.length > 8) prefix.shift(); }
          }
        }
      } catch (e) { if (!controller.signal.aborted) fail(e); }
    }, {once: true});
    video.src = url;
    return source;
  }
};
