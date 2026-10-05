Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-ProfileNumber {
    param([string]$Prompt, [double]$Minimum, [double]$Maximum)
    while ($true) {
        $text = Read-Host ($Prompt + ' (Entree = inconnu)')
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        $number = 0.0
        if ([double]::TryParse($text.Replace(',','.'), [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and
            ![double]::IsNaN($number) -and ![double]::IsInfinity($number) -and $number -ge $Minimum -and $number -le $Maximum) { return $number }
        Write-Host "Valeur attendue entre $Minimum et $Maximum."
    }
}

function Read-ProfileText {
    param([string]$Prompt)
    $text = Read-Host ($Prompt + ' (Entree = inconnu)')
    if ([string]::IsNullOrWhiteSpace($text)) { return $null }
    if ($text.Length -gt 120) { throw 'Texte trop long (120 caracteres maximum).' }
    return $text.Trim()
}

function Set-ActivePlayerProfile {
    param([string]$Root, $Profile)
    $data = Join-Path $Root 'data'
    $null = New-Item -ItemType Directory -Path $data -Force
    $temporary = Join-Path $data ('profile-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $path = Join-Path $data 'active-profile.json'
    $Profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    if (Test-Path -LiteralPath $path) {
        [IO.File]::Replace($temporary, $path, (Join-Path $data ('profile-backup-' + [guid]::NewGuid().ToString('N') + '.json')))
    } else { [IO.File]::Move($temporary, $path) }
}

function Invoke-PlayerProfileMenu {
    param([string]$Root)
    $directory = Join-Path $Root 'data\profiles'
    $null = New-Item -ItemType Directory -Path $directory -Force
    $saved = @(Get-ChildItem -LiteralPath $directory -Filter '*.json' -File | Sort-Object Name)
    Write-Host 'PROFIL JOUEUR : renseigner une fois, puis reutilisation automatique dans chaque rapport.'
    Write-Host 'Les valeurs sont declarees par vous. Elles ne sont pas lues dans le jeu.'
    for ($i=0; $i -lt $saved.Count; $i++) {
        $value = Get-Content -LiteralPath $saved[$i].FullName -Raw | ConvertFrom-Json
        Write-Host ("{0} : {1} / {2} / {3}" -f ($i+1), $value.Label, $value.Weapon, $value.Scope)
    }
    $activePath = Join-Path $Root 'data\active-profile.json'
    $active = $null
    if (Test-Path -LiteralPath $activePath) { $active = Get-Content -LiteralPath $activePath -Raw | ConvertFrom-Json }
    if ($active -and $active.Schema -eq 1) { Write-Host 'C : copier le profil actif et changer seulement arme / lunette / ADS.' }
    Write-Host 'N : nouveau profil (ou nouveaux reglages) ; S : continuer avec des informations inconnues.'
    $choice = Read-Host 'Choix'
    if ($choice -match '^\d+$' -and [long]$choice -ge 1 -and [long]$choice -le $saved.Count) {
        $profile = Get-Content -LiteralPath $saved[[int]$choice-1].FullName -Raw | ConvertFrom-Json
        if ($profile.Schema -ne 1) { throw 'Profil non reconnu.' }
        Set-ActivePlayerProfile $Root $profile
        return $profile
    }
    $profile = [ordered]@{Schema=1; Id=[guid]::NewGuid().ToString('N'); Label='Non renseigne';
        Source='UserDeclared'; UpdatedUtc=[DateTime]::UtcNow.ToString('o'); Game=$null; Weapon=$null;
        Dpi=$null; HorizontalSensitivity=$null; VerticalSensitivity=$null; Scope=$null; AdsSensitivity=$null;
        Fov=$null; AspectRatio=$null; Resolution=$null; AdsUsageDeclared=$null}
    if ($choice -match '^[cC]$' -and $active -and $active.Schema -eq 1) {
        foreach ($key in @($profile.Keys)) {
            if ($key -notin @('Id','UpdatedUtc','Source') -and $active.PSObject.Properties[$key]) { $profile[$key] = $active.$key }
        }
        $profile.Label = Read-ProfileText 'Nom du nouveau profil'
        if (!$profile.Label) { $profile.Label = 'Copie du profil' }
        $profile.Weapon = Read-ProfileText 'Nouvelle arme'
        $profile.Scope = Read-ProfileText 'Nouvelle lunette'
        $profile.AdsSensitivity = Read-ProfileNumber 'Sensibilite ADS de cette lunette' 0 1000
        $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory ($profile.Id + '.json')) -Encoding UTF8
    } elseif ($choice -match '^[nN]$') {
        $profile.Label = Read-ProfileText 'Nom du profil (exemple : M4 2.5x)'
        if (!$profile.Label) { $profile.Label = 'Mon profil' }
        $profile.Game = Read-ProfileText 'Jeu / trainer'
        $profile.Weapon = Read-ProfileText 'Arme'
        $profile.Dpi = Read-ProfileNumber 'DPI' 1 64000
        $profile.HorizontalSensitivity = Read-ProfileNumber 'Sensibilite horizontale' 0 1000
        $profile.VerticalSensitivity = Read-ProfileNumber 'Sensibilite verticale' 0 1000
        $profile.Scope = Read-ProfileText 'Lunette (exemple : 2.5x)'
        $profile.AdsSensitivity = Read-ProfileNumber 'Sensibilite ADS de cette lunette' 0 1000
        $profile.Fov = Read-ProfileNumber 'FOV affiche dans les reglages' 1 180
        $profile.AspectRatio = Read-ProfileText 'Ratio (exemple : 3:2)'
        $profile.Resolution = Read-ProfileText 'Resolution (exemple : 1920x1080)'
        $profile.AdsUsageDeclared = Read-ProfileText 'Usage prevu : ADS / sans ADS / melange'
        $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory ($profile.Id + '.json')) -Encoding UTF8
    } elseif ($choice -notmatch '^[sS]$') { throw 'Choix invalide. Utiliser un numero, N, C ou S.' }
    Set-ActivePlayerProfile $Root $profile
    return [pscustomobject]$profile
}

function Initialize-PlayerProfile {
    param([string]$Root)
    Import-Module (Join-Path $PSScriptRoot 'AutomaticSettings.psm1') -Force
    $profile = Sync-AutomaticPlayerProfile $Root
    Write-Host ("Reglages R6 lus automatiquement : {0} | sensi {1}/{2} | FOV {3} | resolution {4}" -f
        $profile.ImportStatus, $profile.HorizontalSensitivity, $profile.VerticalSensitivity, $profile.Fov, $profile.Resolution)
    Write-Host 'Source : configuration enregistree sur disque, actualisee a chaque lancement. Aucun questionnaire.'
    if ($profile.AdsSensitivityByScope.Count -gt 0) {
        Write-Host ('Reglages ADS enregistres : ' + (($profile.AdsSensitivityByScope.GetEnumerator() | ForEach-Object { '{0}={1}' -f $_.Key.Replace('ADSMouseSensitivity',''),$_.Value }) -join ' | '))
    }
    Write-Host 'DPI, arme et lunette equipee : inconnus. Aucune valeur ancienne reprise pour les deviner.'
    return $profile
}

function Install-ProfileShortcut {
    param([string]$Root)
    # Calls the current client directly; works with older root bootstraps too.
    $script = '$r=$env:AUTOAIMMY_PROFILE_ROOT; $s=Get-Content -LiteralPath (Join-Path $r current.json) -Raw | ConvertFrom-Json; if($s.Current -notmatch ''^\d+\.\d+\.\d+(\.\d+)?$''){throw ''Invalid version''}; & (Join-Path $r (''versions\''+$s.Current+''\Client.ps1'')) -Root $r -Action Profile -Offline'
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
    $command = '@echo off' + "`r`nsetlocal`r`n" + 'set "AUTOAIMMY_PROFILE_ROOT=%~dp0"' + "`r`n" +
        'powershell.exe -NoProfile -ExecutionPolicy Bypass -EncodedCommand ' + $encoded + "`r`npause`r`n"
    [IO.File]::WriteAllText((Join-Path $Root 'Profil-joueur.cmd'), $command, [Text.Encoding]::ASCII)
}

Export-ModuleMember -Function Invoke-PlayerProfileMenu, Initialize-PlayerProfile, Set-ActivePlayerProfile, Install-ProfileShortcut
