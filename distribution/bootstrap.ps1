param([ValidateSet('Launch','Update','Rollback','Export','Connect','Check')][string]$Action='Launch', [switch]$Offline)
$ErrorActionPreference = 'Stop'
try {
    $root = $PSScriptRoot
    $state = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    if ($state.Current -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Invalid installed version.' }
    $client = Join-Path $root ('versions\' + $state.Current + '\Client.ps1')
    & $client -Root $root -Action $Action -Offline:$Offline
} catch {
    Write-Host "AutoAimmy: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
