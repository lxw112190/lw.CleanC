param([string]$ReleasePath = 'dist/CleanC-portable-win-x64', [ValidateSet('Portable','FrameworkDependent')][string]$Mode = 'Portable')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
$root = (Resolve-Path -LiteralPath $ReleasePath).Path
$config = Get-Content -LiteralPath (Join-Path $root 'CleanC.runtimeconfig.json') -Raw | ConvertFrom-Json
$portable = $Mode -eq 'Portable'
$externalFramework = [bool]($config.runtimeOptions.framework -or $config.runtimeOptions.frameworks)
if ($externalFramework -eq $portable) { throw "Unexpected runtime dependency mode: $Mode" }
$start = New-Object System.Diagnostics.ProcessStartInfo
$start.FileName = Join-Path $root 'CleanC.exe'
$start.Arguments = '--verify-runtime'
$start.WorkingDirectory = $root
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
# Remove the usual host hints, then assert where the actual CLR and WPF were loaded from.
if ($portable) {
    $start.EnvironmentVariables['DOTNET_ROOT'] = Join-Path $root 'nonexistent-runtime'
    $start.EnvironmentVariables['DOTNET_ROOT_X64'] = Join-Path $root 'nonexistent-runtime'
    $start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP'] = '0'
}
$process = New-Object System.Diagnostics.Process
$process.StartInfo = $start
try {
    [void]$process.Start()
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Portable startup probe timed out' }
    $output = $stdout.GetAwaiter().GetResult()
    $errors = $stderr.GetAwaiter().GetResult()
    $expectedExit = if ($portable) { 0 } else { 2 }
    if ($process.ExitCode -ne $expectedExit) { throw "Startup failed ($($process.ExitCode)): $output $errors" }
    $report = $output | ConvertFrom-Json
    if ([bool]$report.bundled -ne $portable -or $report.architecture -ne 'X64') { throw "Unexpected runtime source: $Mode" }
    $report | ConvertTo-Json
    Write-Output "PASS: $Mode runtime source verified; no application window or cleanup was started."
} finally { $process.Dispose() }
