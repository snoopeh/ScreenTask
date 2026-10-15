param(
    [int]$Port = 17871,
    [switch]$Hdr,
    [switch]$Layout,
    [string]$NodeModules = $env:SCREENTASK_NODE_MODULES
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$harnessDirectory = Join-Path $PSScriptRoot 'ConnectionHarness\bin\Release\net48'
$stopFile = Join-Path $PSScriptRoot 'harness.stop'
$stdoutFile = Join-Path $PSScriptRoot 'harness.stdout.log'
$stderrFile = Join-Path $PSScriptRoot 'harness.stderr.log'
$harnessProcess = $null
Push-Location $projectRoot
try {
    dotnet build tests\ConnectionHarness -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Connection harness build failed.' }
    $bundledFfmpeg = Get-ChildItem -Path (Join-Path $projectRoot 'dist\*\ffmpeg.exe') -File | Sort-Object DirectoryName -Descending | Select-Object -First 1
    if (!$bundledFfmpeg) { throw 'No packaged ffmpeg.exe was found under dist.' }
    $ffmpegSource = $bundledFfmpeg.FullName
    Copy-Item -LiteralPath $ffmpegSource -Destination (Join-Path $harnessDirectory 'ffmpeg.exe')
    Remove-Item -LiteralPath $stopFile -ErrorAction SilentlyContinue
    $harnessArguments = @($Port, ('"' + $stopFile + '"'))
    if ($Hdr) { $harnessArguments += '--hdr' }
    $harnessProcess = Start-Process -FilePath (Join-Path $harnessDirectory 'ConnectionHarness.exe') -ArgumentList $harnessArguments -WindowStyle Hidden -RedirectStandardOutput $stdoutFile -RedirectStandardError $stderrFile -PassThru
    $origin = "http://localhost:$Port"
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($harnessProcess.HasExited) { throw (Get-Content -LiteralPath $stderrFile -Raw) }
        try { $null = Invoke-WebRequest "$origin/connection-info" -TimeoutSec 1; $ready = $true; break } catch { Start-Sleep -Milliseconds 100 }
    }
    if (!$ready) { throw 'Test listener did not start.' }
    $env:SCREENTASK_TEST_ORIGIN = $origin
    if ($NodeModules) { $env:SCREENTASK_NODE_MODULES = $NodeModules }
    if ($Hdr) { node tests\hdr-correction.cjs } elseif ($Layout) { node tests\viewer-layout.cjs } else { node tests\direct-connect.cjs }
    if ($LASTEXITCODE -ne 0) { throw 'Browser regression tests failed.' }
    Get-Content -LiteralPath $stdoutFile
    if ((Get-Item -LiteralPath $stderrFile).Length -gt 0) { throw (Get-Content -LiteralPath $stderrFile -Raw) }
} finally {
    if ($harnessProcess) {
        Set-Content -LiteralPath $stopFile -Value 'stop'
        if (!$harnessProcess.WaitForExit(5000)) { $harnessProcess.Kill() }
    }
    Pop-Location
}
