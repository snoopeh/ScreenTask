# Third-party components

ScreenTask derives from [EslaMx7/ScreenTask](https://github.com/EslaMx7/ScreenTask), by Eslam Hamouda, under GPL-3.0-or-later. Original history and notices are preserved. See COPYING.

## FFmpeg

The portable package includes the unmodified **FFmpeg 9.0.2 essentials build** by Gyan Doshi, built with GPL/version3 enabled. FFmpeg runs as a separate executable. GPLv3 text is included as FFMPEG-LICENSE.txt and COPYING.

- Exact binary distribution and build information: https://github.com/GyanD/codexffmpeg/releases/tag/9.0.2
- Build provider and dependency list: https://www.gyan.dev/ffmpeg/builds/
- Corresponding FFmpeg source revision linked by the build provider: https://github.com/FFmpeg/FFmpeg/tree/946fcce07b
- FFmpeg source archive: https://github.com/FFmpeg/FFmpeg/archive/946fcce07b.tar.gz
- Dependency source and licenses: https://github.com/GyanD/codexffmpeg (build provider), https://ffmpeg.org/legal.html
- Tested ffmpeg.exe SHA-256: `3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec`

The build includes external libraries such as x264, x265 and zimg; their own notices/licenses apply. FFmpeg's `-version` and `-buildconf` report the exact configuration.

## .NET and audio

- [NAudio](https://github.com/naudio/NAudio): MIT; 2.2.1 in the desktop app and NAudio.Wasapi 3.0.1 in the audio helper. License: https://github.com/naudio/NAudio/blob/master/license.txt
- [Microsoft .NET](https://github.com/dotnet/runtime): MIT and third-party notices; the self-contained audio helper contains its runtime. License: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT ; notices: https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT
- Microsoft System.Resources.Extensions, System.Memory, System.Buffers, System.Numerics.Vectors, System.Runtime.CompilerServices.Unsafe: MIT, https://github.com/dotnet/runtime . Exact dependency versions are pinned in the project files/NuGet resolution.
- [Bootstrap](https://github.com/twbs/bootstrap): MIT; the bundled stylesheet retains its license header.

NuGet packages retain their upstream license metadata. Build the project with `dotnet restore` to obtain original packages and their full notices.
