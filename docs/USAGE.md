# ScreenTask usage and troubleshooting

### DirectX capture and audio build

The Windows application uses DirectX Desktop Duplication through the bundled `ffmpeg.exe` when present, and falls back to GDI capture otherwise. Set the target FPS on the **Video** tab, including 60 or 120. Choose original, 1920 px, 1280 px, or 854 px output width, video quality, and an outgoing bitrate cap. The recommended 2 Mbps cap is intended to leave headroom on remote links; raise it if the viewer connection has spare capacity. A target is a ceiling, not a guarantee. Actual FPS depends on the display, capture, encoding, and network. The viewer shows measured capture, changed-frame, and sent rates; unchanged frames are skipped. A 120 FPS target also requires a display and viewer capable of showing more than 60 Hz.

Version 1.8 fixes first-run server setup on Windows installations with non-English firewall and HTTP reservation output. Approve the Windows administrator prompt when starting the server for the first time. If Windows policy blocks firewall changes, the log now distinguishes a ready HTTP reservation from an unconfirmed firewall rule.

This build uses DirectX Desktop Duplication capture and streams low-latency H.264 in fragmented MP4 through browser Media Source Extensions. It prefers the Windows Media Foundation encoder, falls back to FFmpeg software H.264, and retains JPEG as a compatibility fallback. Keep `ffmpeg.exe` next to `ScreenTask.exe` to use DirectX capture and H.264. FFmpeg is distributed separately under its own license; see `FFMPEG-LICENSE.txt` in the Windows package and [FFmpeg downloads](https://ffmpeg.org/download.html) for source and build information.

On the **Audio** tab, enable **Share playback audio** and choose one of three sources:

- **Playback device (all audio)** shares every sound playing through the selected output device.
- **Only selected application** shares the selected application's audio and its child processes.
- **All audio except selected application** shares system audio while leaving out the selected application and its child processes. Select your call application here to prevent the other person hearing their own voice from your stream.

Use **Refresh** if an application was started after ScreenTask. Audio source changes apply automatically and enabled browser audio reconnects. Per-application modes require Windows 10 build 20348 or newer and `ScreenTask.AudioCaptureHelper.exe` next to `ScreenTask.exe`. If a selected application closes or restarts, refresh and select it again. Process capture covers the selected app's process tree; it cannot separate two audio streams produced by one browser process tree. The application modes capture audio across output devices, while playback-device mode captures only that device. Audio is sent as low-latency 4-bit IMA ADPCM at the capture sample rate and channel count. At 48 kHz stereo the payload is about 384 kbps, roughly one-eighth the previous 32-bit float PCM size, with a modest quality tradeoff. The browser decodes it locally before playback, so the existing volume and gain controls still apply.

The v1.8 browser viewer has a dark responsive interface, fullscreen playback with controls, keyboard shortcuts (Space to pause, F for fullscreen, M for audio), and an audio slider from 0% to 200%. Gain above 100% can make quiet streams louder and may still distort very loud source audio. Click **Enable audio** to begin playback; browsers require a user click before they allow audio output. Use **Telemetry** to show or hide capture and send FPS, encoder, viewer frame cadence and jitter, average frame size, send-write time, skipped frames, browser network estimates when supported, observed send rate, and the configured bitrate limit. Telemetry remains in the viewer and is not sent to an external service.

Viewer count reflects connected screen streams. The built-in chat relays messages to connected viewers and keeps up to 100 recent messages in memory until the server stops. It is intended for trusted devices on the local network; use the existing private-session authentication when needed.

### Unified viewer and fullscreen fixes (1.9.1)

The stream viewer now includes **Chat** and **Files** tabs in the same side panel. The header’s **Files** button opens that panel without navigating away from the stream. File invitation routes (`/files/<token>`, `/files`, and the older `/files.html` alias) serve the same viewer HTML. Opening a file invitation selects Files and leaves video paused until you explicitly resume it. In file-only server mode, this same page shows just file transfer controls and makes no screen/audio/chat requests.

The inactive JPEG/video fallback is now removed from the layout: an author CSS rule previously overrode the HTML `hidden` attribute and exposed a broken image/alt-text strip beside the actual stream. Pausing hides both media elements, and failed JPEG requests show the offline message instead of a broken image.

Fullscreen fills the available viewport. The **View** menu remains available there: **Fit** preserves the entire image and its aspect ratio, **Fill (crop)** fills the screen by cropping excess edges, and percentage zoom scales relative to the fitted image. Your choice is preserved when entering and leaving fullscreen. Fit can show black bars when the stream and screen have different aspect ratios; Fill removes those bars with cropping.

Run `tests/run-connection-tests.ps1 -Layout` for deterministic visual/layout checks and synthetic test-pattern screenshots under `tests/artifacts/`. These cover inactive fallback visibility, fullscreen bounds, Fit/Fill/zoom, mobile overflow, shared Files/Chat tabs and file-only mode isolation. They do not record your desktop.

### Browser file sharing (1.11)

Any browser visiting ScreenTask can send files from its own device. On the desktop, Network has **Open browser file sharing** and **File transfers only**; there is no desktop Files tab.

1. Start the server and open `/files`, or choose Files in the shared stream viewer.
2. Choose files or drop them onto Send files, then click **Create share links**. Copy the file’s link. Link address for friends can specify your public forwarded TCP port, VPN address, or HTTPS proxy. A localhost page suggests the server’s LAN address; a remote page uses its actual external address and port.
3. Your friend opens the link and clicks **Download file**. Their browser download manager saves directly to disk. Both pages show transferred bytes, percentage, average speed, remaining time and status. The sender can cancel a download or stop sharing a file.
4. Keep the sender page and ScreenTask server open. Closing the sender page or revoking a link cancels active downloads and expires the link. Reconnect creates fresh links. Files are never staged on the server disk, read in full into memory, or deleted by ScreenTask.

The sender browser holds selected File objects and reads requested 64 KiB slices. A maximum 1 MiB window per download and four concurrent downloads bound memory. Bytes stream through ScreenTask to the recipient, including over ordinary HTTP. This file path does not use WebRTC/STUN or hosted storage. A sender on the server PC uses a local first hop; a remote browser sender also uses the server’s network connection. Practical limits depend on browser, filesystem, network and proxy.

Single HTTP ranges, HEAD, ETags, If-Match and If-Range support download-manager resuming while the same file stays shared. Resume through the browser Downloads UI or a compatible download manager. Keep source files unchanged; reselect a changed/unreadable file and send its new link. Multi-GiB files and offsets beyond 4 GiB are tested.

A recipient can also choose files to send back. Visitors never choose paths on the server filesystem. Same-origin WebSocket checks protect file control; private-session authentication protects control, metadata, downloads and telemetry. Share links and download trackers use random 192-bit capabilities. Use HTTPS or a trusted VPN/private network for sensitive data: HTTP does not encrypt files or credentials.

### Live settings and preferences (1.11)

FPS, resolution, quality, bitrate, mouse visibility, HDR, audio source and privacy changes apply automatically. Encoder changes briefly reconnect the video decoder; enabled browser audio reconnects after its source changes. The HTTP listener, chat and file shares stay running. IP/public binding/port changes update the listener. Invitations contain their original address, so copy an updated link after changing ports. Windows may require administrator access for initial firewall and URL reservation setup.

File transfers only switches screen/audio off without stopping file sharing. Clear it to allow streaming again. Auto-start and start-minimized preferences apply on the next app launch.

Preferences save atomically to `%LOCALAPPDATA%\ScreenTask\appsettings.xml`, with a `.bak` recovery copy. Damaged primary XML can recover from backup. A valid older file beside the executable migrates automatically when no stable file exists. Shared files remain session-only and are never restored from preferences.

### Automatic HDR correction (1.11)

**Video → HDR colors → Automatic (recommended)** detects the selected display’s active Windows HDR color space and reads its calibrated peak-luminance metadata. SDR displays retain their original SDR colors. Automatic mode follows monitor/Windows HDR changes without changing system settings. If Windows cannot report the profile, the status explains that SDR is active and Manual is available.

**Manual / advanced** keeps the exposure and peak-nits controls for special setups; Off disables correction. Changes apply while streaming. Floating-point linear scRGB Desktop Duplication output is tone mapped before SDR quantization. H.264 and JPEG paths both use the corrected output. Correct HDR colors require the bundled FFmpeg and floating-point capture; the log reports when GDI fallback cannot guarantee correction.

The direct host can share the native corrected monitor instead of browser-picked video. Its decoder reconnects through a stable canvas track when capture settings change, preserving the peer connection. Optional browser-picked audio still needs browser consent. This native path adds a local decode/render/re-encode step; reduce width/FPS if CPU/GPU load is high. Chrome and Edge are tested.

Configure HDR and display calibration in Windows for best results. Automatic mode uses that reported profile; it does not perform physical colorimeter measurements or require users to enter brightness values.

### Direct WebRTC sharing (1.8.1 connection fixes)

On the Network screen, start the server and click **Start direct share**. In the host browser, choose your screen, then use **Copy invitation link**. The invitation includes the viewer address, port, and pairing code. A viewer can open that link and click **Connect directly**, or paste the full link into an already open direct viewer. If the link points to a different server or port, the viewer opens that address before connecting.

The host page suggests the selected adapter’s address and ScreenTask’s listening port. For remote friends, change **Viewer server address** to your reachable public address and external TCP port, or an HTTPS reverse proxy address. For example, if ScreenTask listens on 7070 but your router forwards external TCP 9000 to this PC’s TCP 7070, the invitation address must use port 9000. The viewer address must point to the same ScreenTask server that the host’s localhost page uses. For a test on the same PC, use `http://localhost:<ScreenTask-port>`.

**Check port problems in this order:**

1. Open `http://localhost:<ScreenTask-port>/direct` on the streaming PC. If it fails, verify the server is started and the port matches the Network screen.
2. From another PC on the same LAN, open `http://<streaming-PC-LAN-IP>:<ScreenTask-port>/direct`. Select the correct adapter and check the Windows firewall rule.
3. From your friend’s network, open the public invitation. Enable **Allow public access** and map the public TCP port to this PC’s actual listening port. A page that never loads is an address/port/firewall problem; its JavaScript cannot diagnose a server it cannot reach.
4. Once the page loads, connect. “Signaling ready” means the WebSocket reached ScreenTask. A direct ICE failure after pairing means the separate media path could not be established; it is not evidence that the HTTP port is wrong.

The browser names the signaling address and port, reports waiting for a host separately from negotiation, and explains signaling failures and direct media timeouts. The application log now records signaling joins, pairing, disconnects, and handshake failures without logging pairing codes or SDP. FFmpeg progress telemetry no longer floods the log with timestamp and quality lines.

Direct mode carries screen video and optional browser capture audio through WebRTC, using STUN with no TURN server or media relay configured. It retains browser codec negotiation. The viewer displays the selected candidate path and rejects relay candidates. Networks that prevent direct peer connectivity still need network changes; correcting the signaling TCP port alone cannot guarantee a direct media path. Capture must start on localhost or HTTPS in a browser with screen sharing support. System audio availability depends on the browser and screen-picker choice.

When a viewer leaves, the host keeps capturing and accepts a replacement viewer with the same invitation. Stopping sharing creates a fresh pairing code. Signaling messages are processed in order, and callbacks from an old connection cannot close a newer connection. Ending an optional audio track does not stop the screen track. If autoplay is blocked, use the video’s Play control.

The regular HTTP viewer now reads the encoder’s actual AVC profile and level from its initialization segment. Unsupported Media Source APIs/codecs and asynchronous decoder failures use JPEG compatibility playback; JPEG frames are available even while H.264 is active. JPEG compatibility capture is limited to 10 FPS and runs only while needed by JPEG viewers. H.264 level selection now accounts for resolution as well as frame rate, and extended MP4 box headers are read correctly.

**Allow public access exposes the ScreenTask HTTP listener**, including legacy endpoints. Basic Authentication over HTTP does not encrypt credentials or setup messages. For public use, put the app behind HTTPS/WSS and forward the WebSocket signaling route. Media continues through the separate peer connection.

Regression tests live in `tests/`. Run `tests/run-connection-tests.ps1` with Playwright available to Node (or pass `-NodeModules <path-to-node_modules>`). Tests use a hidden instance of the real application on a separate loopback port, synthetic canvas media for direct sharing, installed Chrome and Edge, and the bundled FFmpeg for HTTP playback. They cover viewer-first pairing, decoded direct video, occupied rooms, reconnect, fresh invitations, server address/port navigation, unreachable signaling, H.264 pause/resume, missing MSE, unsupported codecs, decoder errors, AVC codec extraction, H.264 level selection, and extended MP4 boxes. The harness does not load/save your settings or change firewall rules.

Build the Windows app with Visual Studio or `dotnet build ScreenTask.sln -c Release`. The main app targets .NET Framework 4.8; the process audio helper targets .NET 9. For a self-contained Windows x64 helper, run `dotnet publish ScreenTask.AudioCaptureHelper/ScreenTask.AudioCaptureHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true`, then copy its executable next to `ScreenTask.exe`. The build works without FFmpeg but uses GDI capture until `ffmpeg.exe` is copied next to the application.
