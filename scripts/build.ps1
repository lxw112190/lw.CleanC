param([switch]$SkipTests)
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
# A fresh staging directory prevents packaging personal Data or stale runtime files.
$staging = Join-Path (Get-Location).Path ('artifacts/package/' + [Guid]::NewGuid().ToString('N'))
dotnet publish src/CleanC.App -c Release -r win-x64 --self-contained true -o $staging -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath README.md,CHANGELOG.md -Destination $staging -Force
$config = Get-Content -LiteralPath (Join-Path $staging 'CleanC.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw 'Release still depends on an installed .NET runtime' }
foreach ($required in @('CleanC.exe', 'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'System.Private.CoreLib.dll', 'PresentationFramework.dll', 'PresentationNative_cor3.dll', 'Rules/default.rules.json')) {
    if (-not (Test-Path -LiteralPath (Join-Path $staging $required))) { throw "Missing portable dependency: $required" }
}
New-Item -ItemType Directory -Force -Path dist/CleanC-portable-win-x64 | Out-Null
Get-ChildItem -LiteralPath $staging -Force | Copy-Item -Destination dist/CleanC-portable-win-x64 -Recurse -Force
& (Join-Path $PSScriptRoot 'verify-portable.ps1') -ReleasePath $staging
if (Test-Path -LiteralPath (Join-Path $staging 'Data')) { throw 'Startup verification unexpectedly created personal Data' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path (Get-Location).Path 'dist/CleanC-win-x64-portable.zip'
$temporaryArchive = Join-Path (Get-Location).Path ('artifacts/package/' + [Guid]::NewGuid().ToString('N') + '.zip')
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
Get-FileHash -LiteralPath $archive -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLower())  CleanC-win-x64-portable.zip" } |
    Set-Content -LiteralPath dist/SHA256SUMS.txt -Encoding ascii
Get-Item dist/CleanC-portable-win-x64/CleanC.exe, $archive, dist/SHA256SUMS.txt | Select-Object FullName,Length
