# ScreenTask

Screen sharing, audio and large file transfers straight from your Windows PC to a friend's browser.

**[Download the latest Windows app](https://github.com/snoopeh/ScreenTask/releases/latest/download/ScreenTask-win-x64.zip)** · **[Releases](https://github.com/snoopeh/ScreenTask/releases)** · **[Usage and connection troubleshooting](https://github.com/snoopeh/ScreenTask/blob/main/docs/USAGE.md)**

## Run it

1. Download the Windows app above, extract the whole folder, and run **ScreenTask.exe**. Keep the included files together. Windows 10/11 x64 with .NET Framework 4.8 is required. The audio helper includes its .NET runtime.
2. Select your adapter and port on **Network**, then start the server. Open the displayed address in a browser. Windows may request administrator access to configure its HTTP reservation and firewall on first use.
3. For large files, open **Files**, choose **Start file server only**, add files, and copy their links to your friend. Downloads stream from disk and support resuming and files larger than 4 GiB. Links last for the current app session. Your friend needs only a browser. To send back, they can run their own copy.
4. For remote friends, use a reachable VPN address, HTTPS proxy, or your router's forwarded external TCP port in the invitation/link address. A wrong port prevents the page from loading. File transfers use the TCP listener; STUN does not make that port reachable. Direct WebRTC media also requires a working peer path; no TURN relay is configured.

## Updates

Open the desktop app's **Updates** tab:

- **Check for updates** checks this repository's latest stable release.
- **Update now** downloads the Windows build, verifies GitHub's SHA-256 digest and its application version, installs it, and restarts ScreenTask. Active streams and transfers stop. Settings and unrelated files stay in place. An interrupted install restores the previous application files. Recovery copies remain in `.screentask-backup-*` beside the app.
- **Download latest source** saves the current `main` branch's source archive. It does not install or build that source; this can contain changes newer than the latest release.

Keep ScreenTask in a folder you can write to, such as Documents or Downloads. Updates reject linked installation paths. Update errors are shown in the tab or saved under `%TEMP%/ScreenTask-update-error-*.txt` by the installer. Releases are currently unsigned; get them from this repository.

## Features

- Direct WebRTC sharing with pairing links, browser codec negotiation, reconnects and useful address/port diagnostics.
- Native DirectX capture with H.264 playback and JPEG compatibility fallback.
- HDR to SDR color correction, exposure and peak-brightness controls.
- Playback-device audio and per-application include/exclude capture. Per-application capture requires Windows build 20348 or newer.
- One browser viewer for video, chat and files, with fullscreen Fit/Fill/percentage zoom and mobile layouts.
- Large file links, byte ranges, download resuming, revocation, private-session authentication and a file-only mode.

Use trusted networks or HTTPS for sensitive data: plain HTTP does not encrypt files or Basic Authentication credentials. There is no hosted storage service or app-imposed file size cap; your connection, disks and any proxy determine practical limits. See [the detailed guide](https://github.com/snoopeh/ScreenTask/blob/main/docs/USAGE.md) for controls, HDR, audio, networking and browser compatibility.

## Build and test

Install the .NET 9 SDK and .NET Framework 4.8 developer/targeting pack on Windows, or Visual Studio with desktop .NET development tools.

```powershell
dotnet build ScreenTask.sln -c Release
dotnet build tests/UpdateTests -c Release
./tests/UpdateTests/bin/Release/net48/UpdateTests.exe
./scripts/Build-Release.ps1
```

The packaging script downloads a pinned FFmpeg 9.0.2 build, verifies its executable SHA-256, publishes the audio helper self-contained, and checks the actual update archive. Pass `-FfmpegPath <path>` to reuse the matching FFmpeg executable. Outputs go under ignored `dist/`. User settings, shared files and credentials are excluded from source control and releases.

Browser regression tests require Node, Playwright, installed Chrome and Edge, and a packaged `dist/*/ffmpeg.exe`. Set `SCREENTASK_NODE_MODULES` if Playwright is installed outside this repository:

```powershell
./tests/run-connection-tests.ps1
./tests/run-connection-tests.ps1 -Hdr
./tests/run-connection-tests.ps1 -Layout
node tests/file-transfers.cjs
```

The tests use an isolated listener and temporary fixtures without loading user settings or changing firewall rules. The file suite covers a sparse 6 GiB file and bounded-memory transfers.

Push code to `main` for automated build checks. To publish, bump the assembly version in `ScreenTask/Properties/AssemblyInfo.cs`, commit, and push a matching tag such as `v1.10.0`. The release workflow builds and publishes `ScreenTask-win-x64.zip` and `SHA256SUMS.txt`; existing apps then discover it through Updates.

## Attribution and license

This project continues [Eslam Hamouda's ScreenTask](https://github.com/EslaMx7/ScreenTask), retaining its Git history and copyright notices. ScreenTask is GPL version 3 or later; see [COPYING](COPYING). Bundled components and their source/license links are listed in [THIRD-PARTY.md](THIRD-PARTY.md).
