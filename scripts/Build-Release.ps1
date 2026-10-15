param(
    [string]$FfmpegPath,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\dist\release')
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath($OutputDirectory)
$package = Join-Path $output 'ScreenTask-win-x64'
if (Test-Path -LiteralPath $package) { throw "Output folder already exists: $package. Choose a new OutputDirectory." }
New-Item -ItemType Directory -Path $package -Force | Out-Null
Push-Location $root
try {
    dotnet build ScreenTask.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    $audio = Join-Path $output 'audio-publish'
    dotnet publish ScreenTask.AudioCaptureHelper/ScreenTask.AudioCaptureHelper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o $audio --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Audio helper publish failed.' }
    if (!$FfmpegPath) {
        $download = Join-Path $output 'ffmpeg-9.0.2-essentials_build.zip'
        Invoke-WebRequest 'https://github.com/GyanD/codexffmpeg/releases/download/9.0.2/ffmpeg-9.0.2-essentials_build.zip' -OutFile $download
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $zip = [IO.Compression.ZipFile]::OpenRead($download)
        try {
            $entry = $zip.GetEntry('ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe')
            if (!$entry) { throw 'Pinned FFmpeg archive does not contain the expected executable.' }
            $FfmpegPath = Join-Path $output 'ffmpeg.exe'
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $FfmpegPath)
        } finally { $zip.Dispose() }
    }
    if ((Get-FileHash -LiteralPath $FfmpegPath -Algorithm SHA256).Hash -ne '3256173F3F8BFFD7DF12227C68ADF68025EDB1832273A9530688A7BB1ED8EDEC') {
        throw 'FFmpeg SHA-256 does not match the tested 9.0.2 essentials build.'
    }
    Copy-Item ScreenTask/bin/Release/ScreenTask.exe, ScreenTask/bin/Release/ScreenTask.exe.config -Destination $package
    Get-ChildItem ScreenTask/bin/Release -Filter '*.dll' -File | Copy-Item -Destination $package
    Copy-Item ScreenTask/WebServer -Destination $package -Recurse
    Copy-Item ScreenTask.Updater/bin/Release/net48/ScreenTask.Updater.exe -Destination $package
    Copy-Item (Join-Path $audio 'ScreenTask.AudioCaptureHelper.exe') -Destination $package
    Copy-Item -LiteralPath $FfmpegPath -Destination (Join-Path $package 'ffmpeg.exe')
    Copy-Item README.md, COPYING, THIRD-PARTY.md -Destination $package
    Copy-Item COPYING -Destination (Join-Path $package 'FFMPEG-LICENSE.txt')
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = Join-Path $output 'ScreenTask-win-x64.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($package, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $digest = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS.txt') -Value "$digest  ScreenTask-win-x64.zip" -Encoding ascii
    dotnet build tests/UpdateTests -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Update test build failed.' }
    & tests/UpdateTests/bin/Release/net48/UpdateTests.exe $archive
    if ($LASTEXITCODE -ne 0) { throw 'Packaged update checks failed.' }
    Write-Output "Release ready: $archive"
} finally { Pop-Location }
