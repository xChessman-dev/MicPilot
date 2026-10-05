$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Set-StrictMode -Version Latest
$taskRoot = if ($env:MICPILOT_NATIVE_DIR) { [IO.Path]::GetFullPath($env:MICPILOT_NATIVE_DIR) } else { [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../native/rnnoise')) }
$taskCommit = '70f1d256acd4b34a572f999a05c87bf00b67730d'
$taskSource = Join-Path $taskRoot "rnnoise-$taskCommit"
$taskArchive = Join-Path $taskRoot 'rnnoise-source.zip'
[IO.Directory]::CreateDirectory($taskRoot) | Out-Null
if (-not (Test-Path -LiteralPath $taskSource)) {
    Invoke-WebRequest "https://codeload.github.com/xiph/rnnoise/zip/$taskCommit" -OutFile $taskArchive
    Expand-Archive -LiteralPath $taskArchive -DestinationPath $taskRoot
}
$taskHash = (Get-Content -LiteralPath (Join-Path $taskSource 'model_version') -Raw).Trim()
if ($taskHash -notmatch '^[a-f0-9]{64}$') { throw 'Invalid upstream model checksum.' }
$taskModelArchive = Join-Path $taskRoot "rnnoise_data-$taskHash.tar.gz"
if (-not (Test-Path -LiteralPath (Join-Path $taskSource 'src\rnnoise_data.c'))) {
    if (-not (Test-Path -LiteralPath $taskModelArchive) -or (Get-FileHash -LiteralPath $taskModelArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskHash) {
        & curl.exe --fail --silent --show-error --location --max-time 15 "https://media.xiph.org/rnnoise/models/rnnoise_data-$taskHash.tar.gz" --output $taskModelArchive
        if ($LASTEXITCODE -ne 0) {
            # Gentoo distfiles mirror: identical archive, accepted only with the pinned upstream SHA256.
            & curl.exe --fail --silent --show-error --location --max-time 180 "https://distfiles.gentoo.org/distfiles/dd/rnnoise_data-$taskHash.tar.gz" --output $taskModelArchive
            if ($LASTEXITCODE -ne 0) { throw 'Cannot download RNNoise model.' }
        }
    }
    if ((Get-FileHash -LiteralPath $taskModelArchive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $taskHash) { throw 'RNNoise model checksum mismatch.' }
    $taskMembers = & tar -tzf $taskModelArchive
    if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect model archive.' }
    foreach ($taskMember in @('src/rnnoise_data.c','src/rnnoise_data.h')) {
        if ($taskMembers -notcontains $taskMember) { throw "Required model source missing: $taskMember" }
    }
    # Extract only the two compiled weight sources. Training checkpoints are never extracted or loaded.
    & tar -xzf $taskModelArchive -C $taskSource src/rnnoise_data.c src/rnnoise_data.h
    if ($LASTEXITCODE -ne 0) { throw 'Cannot extract model source.' }
}
$taskCompilerCommand = Get-Command cl.exe -ErrorAction SilentlyContinue
if (-not $taskCompilerCommand) { throw 'Run this script from x64 Developer PowerShell with MSVC and Windows SDK available.' }
$taskCompiler = $taskCompilerCommand.Source
$taskBuild = Join-Path $taskRoot 'build'
[IO.Directory]::CreateDirectory($taskBuild) | Out-Null
& {
    $taskSources = @('denoise.c','rnn.c','pitch.c','kiss_fft.c','celt_lpc.c','nnet.c','nnet_default.c','parse_lpcnet_weights.c','rnnoise_data.c','rnnoise_tables.c') | ForEach-Object { Join-Path $taskSource "src\$_" }
    Push-Location $taskBuild
    try {
        & $taskCompiler /nologo /LD /O2 /MT /arch:AVX2 /std:c11 /DWIN32 /DRNNOISE_BUILD /DDLL_EXPORT /D_USE_MATH_DEFINES /D_CRT_SECURE_NO_WARNINGS "/I$(Join-Path $taskSource 'include')" "/I$(Join-Path $taskSource 'src')" $taskSources "/Fe$(Join-Path $taskRoot 'rnnoise.dll')"
        if ($LASTEXITCODE -ne 0) { throw 'RNNoise compilation failed.' }
    } finally { Pop-Location }
}
Copy-Item -LiteralPath (Join-Path $taskSource 'COPYING') -Destination (Join-Path $taskRoot 'RNNoise-LICENSE.txt') -Force
Write-Output "RNNoise ready in $taskRoot. Source $taskCommit; model SHA256 $taskHash"
