param([switch]$SkipTests, [switch]$Versioned)
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet build CleanC.sln -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
if (-not $SkipTests) {
    dotnet run --project tests/CleanC.Tests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed' }
    dotnet run --project tests/CleanC.UiTests -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'UI render tests failed' }
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[xml]$project = Get-Content -LiteralPath src/CleanC.App/CleanC.App.csproj -Raw
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Invalid project version' }
$prefix = if ($Versioned) { "lw.CleanC-v$version" } else { 'CleanC' }
# All publish and ZIP staging directories are new, excluding user Data and stale outputs.
$packageRoot = Join-Path (Get-Location).Path ('artifacts/package/' + [Guid]::NewGuid().ToString('N'))
$assets = Join-Path $packageRoot 'assets'
New-Item -ItemType Directory -Force -Path $assets,dist | Out-Null
$hashes = @()
foreach ($kind in @('portable', 'framework-dependent')) {
    $selfContained = if ($kind -eq 'portable') { 'true' } else { 'false' }
    $mode = if ($kind -eq 'portable') { 'Portable' } else { 'FrameworkDependent' }
    $staging = Join-Path $packageRoot $kind
    dotnet publish src/CleanC.App -c Release -r win-x64 --self-contained $selfContained -o $staging -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $kind" }
    Copy-Item -LiteralPath README.md,CHANGELOG.md,LICENSE -Destination $staging -Force
    if (Test-Path -LiteralPath docs) { Copy-Item -LiteralPath docs -Destination $staging -Recurse -Force }
    foreach ($required in @('CleanC.exe', 'CleanC.dll', 'CleanC.Core.dll', 'CleanC.runtimeconfig.json', 'Rules/default.rules.json', 'LICENSE')) {
        if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing dependency: $required" }
    }
    if ($kind -eq 'portable') {
        foreach ($required in @('coreclr.dll','hostfxr.dll','hostpolicy.dll','System.Private.CoreLib.dll','PresentationFramework.dll','PresentationNative_cor3.dll')) {
            if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing runtime: $required" }
        }
    } elseif (Test-Path -LiteralPath (Join-Path $staging 'coreclr.dll')) { throw 'Slim package unexpectedly contains the CLR' }
    $name = "$prefix-win-x64-$kind.zip"
    $archive = Join-Path $assets $name
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        if ($zip.Entries.FullName -match '(^|[\\/])(Data|History|artifacts)([\\/]|$)') { throw 'Personal or test data in ZIP' }
    } finally { $zip.Dispose() }
    # Exercise the extracted artifact, not just the publish folder.
    $extracted = Join-Path $packageRoot ("verify-" + $kind)
    [IO.Compression.ZipFile]::ExtractToDirectory($archive, $extracted)
    & (Join-Path $PSScriptRoot 'verify-portable.ps1') -ReleasePath $extracted -Mode $mode
    if (Test-Path -LiteralPath (Join-Path $extracted 'Data')) { throw 'Startup probe created personal Data' }
    $destination = if ($Versioned) { "dist/$prefix-$kind-win-x64" } else { "dist/CleanC-$kind-win-x64" }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Get-ChildItem -LiteralPath $staging -Force | Copy-Item -Destination $destination -Recurse -Force
    $hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    $hashes += "$hash  $name"
}
$hashes | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding ascii
# Publish only this invocation's known artifacts; earlier versioned ZIPs are not selected.
Get-ChildItem -LiteralPath $assets -File | Copy-Item -Destination dist -Force
$hashes | ForEach-Object { Write-Output $_ }
Get-ChildItem -LiteralPath $assets -File | Select-Object Name,Length
