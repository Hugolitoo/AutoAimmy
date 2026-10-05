Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Sync-AutomaticPlayerProfile {
    param([string]$Root, [string[]]$SearchDirectories, [string]$SelectedFileKey)
    if (!$PSBoundParameters.ContainsKey('SearchDirectories')) {
        $SearchDirectories = @(
            (Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'My Games\Rainbow Six - Siege'),
            (Join-Path $env:USERPROFILE 'Documents\My Games\Rainbow Six - Siege')
        ) | Select-Object -Unique
    }
    $profile = [ordered]@{ Schema=1; Id='automatic-r6'; Label='Reglages automatiques';
        Source='SettingsFile'; ImportStatus='NotFound'; UpdatedUtc=[DateTime]::UtcNow.ToString('o');
        SettingsFileLastWriteUtc=$null; SettingsFileKey=$null; Game=$null; Weapon=$null; Dpi=$null;
        HorizontalSensitivity=$null; VerticalSensitivity=$null; Scope=$null; AdsSensitivity=$null;
        AdsSensitivityByScope=[ordered]@{}; AdsUseSpecific=$null; AdsGlobalSensitivity=$null;
        MouseSensitivityMultiplier=$null; AdsMouseMultiplier=$null;
        Fov=$null; AspectRatio=$null; AspectRatioSetting=$null; Resolution=$null; AdsUsageDeclared=$null }
    $files = @(foreach ($directory in $SearchDirectories) {
        if (Test-Path -LiteralPath $directory -PathType Container) {
            $direct = Join-Path $directory 'GameSettings.ini'
            if (Test-Path -LiteralPath $direct -PathType Leaf) { Get-Item -LiteralPath $direct }
            foreach ($child in @(Get-ChildItem -LiteralPath $directory -Directory)) {
                $path = Join-Path $child.FullName 'GameSettings.ini'
                if (Test-Path -LiteralPath $path -PathType Leaf) { Get-Item -LiteralPath $path }
            }
        }
    }) | Sort-Object FullName -Unique
    $data = Join-Path $Root 'data'
    $null = New-Item -ItemType Directory -Path $data -Force
    function File-Key([string]$Path) {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($Path.ToLowerInvariant())))).Replace('-','').ToLowerInvariant() }
        finally { $sha.Dispose() }
    }
    $candidates = @(foreach ($candidate in @($files)) {
        $preview = ''
        try {
            if ($candidate.Length -le 262144) {
                $preview = (([IO.File]::ReadAllLines($candidate.FullName) | Where-Object { $_ -match '^(MouseYawSensitivity|MousePitchSensitivity|DefaultFOV|ResolutionWidth|ResolutionHeight)=\d+(\.\d+)?$' }) -join ' / ')
            }
        } catch { $preview = 'fichier inaccessible' }
        [pscustomobject]@{Key=(File-Key $candidate.FullName); Label=('Configuration du ' + $candidate.LastWriteTime.ToString('g') + ' : ' + $preview)}
    })
    ConvertTo-Json -InputObject $candidates -Depth 4 | Set-Content (Join-Path $data 'settings-candidates.json') -Encoding UTF8
    $selectionPath = Join-Path $data 'settings-selection.json'
    if (!$SelectedFileKey -and (Test-Path -LiteralPath $selectionPath)) {
        try { $SelectedFileKey = (Get-Content $selectionPath -Raw | ConvertFrom-Json).Key } catch { }
    }
    $selected = @($files | Where-Object { (File-Key $_.FullName) -eq $SelectedFileKey })
    $provisional = $false
    if ($selected.Count -eq 1) {
        @{Key=$SelectedFileKey} | ConvertTo-Json | Set-Content $selectionPath -Encoding UTF8
        $files = $selected
    } elseif (@($files).Count -gt 1) {
        # Display the newest saved configuration as a candidate, never as a verified active account.
        $files = @($files | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1)
        $provisional = $true
    }
    if (@($files).Count -eq 1) {
        try {
            $file = @($files)[0]
            $profile.SettingsFileKey = File-Key $file.FullName
            if ($file.Length -gt 262144) { throw 'Settings file too large' }
            $settings = @{}
            $section = ''
            foreach ($line in [IO.File]::ReadAllLines($file.FullName)) {
                if ($line -match '^\s*\[([^\]]+)\]\s*$') { $section = $Matches[1]; continue }
                if ($section -in @('INPUT','DISPLAY_SETTINGS','DISPLAY') -and $line -match '^\s*([A-Za-z0-9]+)\s*=\s*([^;#]+)') {
                    $settings[$section + '/' + $Matches[1]] = $Matches[2].Trim()
                }
            }
            function Read-IniNumber([string]$Section, [string]$Key, [double]$Min, [double]$Max) {
                $raw = $settings[$Section + '/' + $Key]
                $number = 0.0
                if ($null -ne $raw -and [double]::TryParse($raw, [Globalization.NumberStyles]::Float,
                    [Globalization.CultureInfo]::InvariantCulture, [ref]$number) -and
                    ![double]::IsNaN($number) -and ![double]::IsInfinity($number) -and $number -ge $Min -and $number -le $Max) { return $number }
                return $null
            }
            $displaySection = if ($settings.ContainsKey('DISPLAY_SETTINGS/ResolutionWidth')) { 'DISPLAY_SETTINGS' } else { 'DISPLAY' }
            $profile.HorizontalSensitivity = Read-IniNumber 'INPUT' 'MouseYawSensitivity' 0 1000
            $profile.VerticalSensitivity = Read-IniNumber 'INPUT' 'MousePitchSensitivity' 0 1000
            $profile.MouseSensitivityMultiplier = Read-IniNumber 'INPUT' 'MouseSensitivityMultiplierUnit' 0 10
            $profile.AdsMouseMultiplier = Read-IniNumber 'INPUT' 'ADSMouseMultiplierUnit' 0 10
            $profile.AdsGlobalSensitivity = Read-IniNumber 'INPUT' 'ADSMouseSensitivityGlobal' 0 1000
            $profile.AdsUseSpecific = Read-IniNumber 'INPUT' 'ADSMouseUseSpecific' 0 1
            foreach ($key in @('ADSMouseSensitivity1x','ADSMouseSensitivity1xHalf','ADSMouseSensitivity2x','ADSMouseSensitivity2xHalf','ADSMouseSensitivity3x','ADSMouseSensitivity4x','ADSMouseSensitivity5x','ADSMouseSensitivity8x','ADSMouseSensitivity12x')) {
                $value = Read-IniNumber 'INPUT' $key 0 1000
                if ($null -ne $value) { $profile.AdsSensitivityByScope[$key] = $value }
            }
            $profile.Fov = Read-IniNumber $displaySection 'DefaultFOV' 1 180
            $profile.AspectRatioSetting = Read-IniNumber $displaySection 'AspectRatio' 0 20
            # Store the game's enum value; no unverified mapping to a ratio label.
            $width = Read-IniNumber $displaySection 'ResolutionWidth' 320 16384
            $height = Read-IniNumber $displaySection 'ResolutionHeight' 200 16384
            if ($null -ne $width -and $null -ne $height -and $width -eq [int]$width -and $height -eq [int]$height) { $profile.Resolution = '{0}x{1}' -f [int]$width,[int]$height }
            $known = @($profile.HorizontalSensitivity,$profile.VerticalSensitivity,$profile.Fov,$profile.Resolution) | Where-Object { $null -ne $_ }
            if (@($known).Count -eq 0) { $profile.ImportStatus = 'UnsupportedSettings' }
            else {
                $profile.ImportStatus = if ($provisional) { 'ImportedCandidate' } else { 'Imported' }
                $profile.Game = 'Rainbow Six Siege'
                $profile.SettingsFileLastWriteUtc = $file.LastWriteTimeUtc.ToString('o')
            }
        } catch { $profile.ImportStatus = 'UnreadableSettings' }
    }
    $data = Join-Path $Root 'data'
    $null = New-Item -ItemType Directory -Path $data -Force
    $temporary = Join-Path $data ('automatic-' + [guid]::NewGuid().ToString('N') + '.tmp')
    $destination = Join-Path $data 'active-profile.json'
    $profile | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $temporary -Encoding UTF8
    if (Test-Path -LiteralPath $destination) {
        [IO.File]::Replace($temporary, $destination, (Join-Path $data ('profile-backup-' + [guid]::NewGuid().ToString('N') + '.json')))
    } else { [IO.File]::Move($temporary, $destination) }
    return [pscustomobject]$profile
}

Export-ModuleMember -Function Sync-AutomaticPlayerProfile
