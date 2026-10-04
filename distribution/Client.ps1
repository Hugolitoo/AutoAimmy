param([Parameter(Mandatory=$true)][string]$Root, [ValidateSet('Launch','Update','Rollback','Export','Connect','Check')][string]$Action = 'Launch', [switch]$Offline)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Updater.psm1') -Force
$Root = [IO.Path]::GetFullPath($Root)
$lock = $null
try {
    $lock = [IO.File]::Open((Join-Path $Root 'launcher.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    switch ($Action) {
        'Connect' {
            Write-Host 'For a PRIVATE repository: use your own GitHub fine-grained token, limited to this repository with Contents: Read.'
            $secret = Read-Host 'GitHub token (hidden)' -AsSecureString
            if ($secret.Length -eq 0) { throw 'Empty token.' }
            $credential = New-Object Management.Automation.PSCredential('github', $secret)
            $credential | Export-Clixml -LiteralPath (Join-Path $Root 'data\github-access.xml')
            Write-Host 'Credential encrypted for this Windows user. Never send this file.'
            return
        }
        'Export' { $null = Export-TestReport $Root; return }
        'Rollback' { Invoke-Rollback $Root; return }
        'Update' { Invoke-UpdateCheck $Root; return }
        'Check' { Write-Host "Installed: $((Get-InstalledState $Root).Current)"; return }
    }
    Assert-AppStopped $Root
    if (!$Offline) {
        try { Invoke-UpdateCheck $Root } catch { Write-Warning "Update unavailable: $($_.Exception.Message). Using installed version." }
    }
    $state = Get-InstalledState $Root
    $versionDirectory = Join-Path $Root ('versions\' + $state.Current)
    $options = Get-Content -LiteralPath (Join-Path $Root 'data\adaptive.json') -Raw | ConvertFrom-Json
    if (!$options.Enabled -or !$options.OfflineTrainerConfirmed) { throw 'This distribution requires observation mode and offline trainer confirmation.' }
    $env:AUTOAIMMY_DATA_DIR = Join-Path $Root 'data'
    $env:AUTOAIMMY_VERSION = $state.Current
    # Self-contained build does not require a developer SDK or a system runtime.
    $app = Start-Process -FilePath (Join-Path $versionDirectory 'YmmiaV2.exe') -WorkingDirectory $env:AUTOAIMMY_DATA_DIR -WindowStyle Hidden -PassThru
    Write-Host "AutoAimmy $($state.Current) started. PID: $($app.Id). Observation only."
} finally { if ($lock) { $lock.Dispose() } }
