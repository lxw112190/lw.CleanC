param([string]$ReleasePath = 'dist/CleanC-portable-win-x64')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$root = (Resolve-Path -LiteralPath $ReleasePath).Path
$config = Get-Content -LiteralPath (Join-Path $root 'CleanC.runtimeconfig.json') -Raw | ConvertFrom-Json
if ($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks) { throw 'Framework-dependent package detected' }
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $root 'CleanC.exe'
$start.Arguments = '--verify-runtime'
$start.WorkingDirectory = $root
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
# Remove the usual host hints, then assert where the actual CLR and WPF were loaded from.
$start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $root 'nonexistent-runtime'
$start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $root 'nonexistent-runtime'
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
try {
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Portable startup probe timed out' }
    $output = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    if ($process.ExitCode -ne 0) { throw "Portable startup failed ($($process.ExitCode)): $output $errors" }
    $report = $output | ConvertFrom-Json
    if (-not $report.bundled -or $report.architecture -ne 'X64') { throw 'Runtime did not load from the portable directory' }
    $report | ConvertTo-Json
    Write-Output 'PASS: CLR and WPF loaded from the portable package; no application window or cleanup was started.'
} finally { $process.Dispose() }
