param([Parameter(Mandatory=$true)][string]$Root, [ValidateSet('Launch','Update','Rollback','Export','Connect','Check','Configure','Profile')][string]$Action = 'Launch', [switch]$Offline)
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'Updater.psm1') -Force
$Root = [IO.Path]::GetFullPath($Root)
$lock = $null
try {
    $lock = [IO.File]::Open((Join-Path $Root 'launcher.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    Import-Module (Join-Path $PSScriptRoot 'PlayerProfiles.psm1') -Force
    Install-ProfileShortcut $Root
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
        'Update' {
            Invoke-UpdateCheck $Root -AcceptUpdate
            $installed = Get-InstalledState $Root
            Import-Module (Join-Path $Root ('versions\' + $installed.Current + '\PlayerProfiles.psm1')) -Force
            Install-ProfileShortcut $Root
            return
        }
        'Check' { Write-Host "Installed: $((Get-InstalledState $Root).Current)"; return }
    }
    Assert-AppStopped $Root
    if ($Action -eq 'Profile') {
        Import-Module (Join-Path $PSScriptRoot 'PlayerProfiles.psm1') -Force
        Install-ProfileShortcut $Root
        $null = Invoke-PlayerProfileMenu $Root
        return
    }
    if (!$Offline) {
        try { Invoke-UpdateCheck $Root -AcceptUpdate } catch { Write-Warning "Update unavailable: $($_.Exception.Message). Using installed version." }
    }
    $state = Get-InstalledState $Root
    $versionDirectory = Join-Path $Root ('versions\' + $state.Current)
    $options = Get-Content -LiteralPath (Join-Path $Root 'data\adaptive.json') -Raw | ConvertFrom-Json
    if ($Action -eq 'Configure' -or !$options.PSObject.Properties['AimReference']) {
        Write-Host 'Reference du viseur dans votre trainer : 1 = curseur mobile ; 2 = centre de l ecran (plein ecran).'
        $choice = Read-Host 'Choix (1 ou 2, Entree = 2)'
        if ($choice -notin @('','1','2')) { throw 'Choix invalide : utilisez 1 ou 2.' }
        $reference = if ($choice -eq '1') { 'Cursor' } else { 'ScreenCenter' }
        $options | Add-Member -NotePropertyName AimReference -NotePropertyValue $reference -Force
        $options | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $Root 'data\adaptive.json') -Encoding UTF8
        Write-Host "Reference enregistree : $reference. Selectionnez le bon moniteur dans Aimmy."
    }
    if ($Action -eq 'Configure') { return }
    if (!$options.Enabled -or !$options.OfflineTrainerConfirmed) { throw 'This distribution requires observation mode and offline trainer confirmation.' }
    Import-Module (Join-Path $versionDirectory 'PlayerProfiles.psm1') -Force
    Install-ProfileShortcut $Root
    $null = Initialize-PlayerProfile $Root
    $env:AUTOAIMMY_DATA_DIR = Join-Path $Root 'data'
    $env:AUTOAIMMY_VERSION = $state.Current
    # Self-contained build does not require a developer SDK or a system runtime.
    $app = Start-Process -FilePath (Join-Path $versionDirectory 'YmmiaV2.exe') -WorkingDirectory $env:AUTOAIMMY_DATA_DIR -WindowStyle Hidden -PassThru
    Write-Host "AutoAimmy $($state.Current) started. PID: $($app.Id). Observation only."
} finally { if ($lock) { $lock.Dispose() } }
