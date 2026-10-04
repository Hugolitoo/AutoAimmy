$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($env:AUTOAIMMY_REPAIR_ROOT)
$state = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
if ($state.Current -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw 'Invalid installed version.' }
$module = Join-Path $root ('versions\' + $state.Current + '\Updater.psm1')
Import-Module $module -Force
Assert-AppStopped $root
$old = '$releases = @(Invoke-RestMethod -Uri $uri -Headers (Get-UpdateHeaders $Root) -TimeoutSec 15)'
$replacement = '$releases = Invoke-RestMethod -Uri $uri -Headers (Get-UpdateHeaders $Root) -TimeoutSec 15'
$content = [IO.File]::ReadAllText($module)
if ($content.Contains($old)) {
    $temporary = $module + '.repair-' + [guid]::NewGuid().ToString('N')
    $backup = $module + '.backup-' + [guid]::NewGuid().ToString('N')
    [IO.File]::WriteAllText($temporary, $content.Replace($old, $replacement), [Text.UTF8Encoding]::new($false))
    [IO.File]::Replace($temporary, $module, $backup)
    Write-Host 'Updater repare. Sauvegarde conservee. Les donnees du joueur sont intactes.'
} elseif ($content.Contains($replacement)) {
    Write-Host 'Updater deja corrige.'
} else {
    throw 'Updater non reconnu. Aucun fichier modifie.'
}
