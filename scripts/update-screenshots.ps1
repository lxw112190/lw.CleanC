$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet run --project tests/CleanC.UiTests -c Release
if ($LASTEXITCODE -ne 0) { throw 'Screenshot generation or UI checks failed' }
New-Item -ItemType Directory -Force -Path docs/screenshots | Out-Null
foreach ($page in @('overview','cleanup','analysis','duplicates')) {
    Copy-Item -LiteralPath "artifacts/ui/$page-1320.png" -Destination "docs/screenshots/$page.png" -Force
}
Get-ChildItem -LiteralPath docs/screenshots | Select-Object Name,Length
