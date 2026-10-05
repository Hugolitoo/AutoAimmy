$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'distribution\PlayerProfiles.psm1') -Force
$root = Join-Path $env:TEMP ('AutoAimmy-profile-checks-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $root
$result = & (Get-Module PlayerProfiles) {
    param($fixture)
    $script:answers = [Collections.Generic.Queue[string]]::new()
    function script:Read-Host { param($Prompt); if($script:answers.Count -eq 0){throw 'Unexpected repeated question'}; return $script:answers.Dequeue() }
    try {
        foreach ($value in @('N','Test M4','Sandbox','M4','incorrect','NaN','1600','3','3','2.5x','55','90','3:2','1920x1080','melange')) { $script:answers.Enqueue($value) }
        $created = Invoke-PlayerProfileMenu $fixture
        if ($script:answers.Count -ne 0) { throw 'Unconsumed wizard answers' }
        if ($created.Dpi -ne 1600 -or $created.AdsSensitivity -ne 55) { throw 'Profile persistence mismatch' }
        $script:answers.Enqueue('S')
        $unknown = Invoke-PlayerProfileMenu $fixture
        if ($null -ne $unknown.Dpi) { throw 'Unknown DPI fabricated' }
        $script:answers.Enqueue('1')
        $selected = Invoke-PlayerProfileMenu $fixture
        if ($selected.Id -ne $created.Id) { throw 'Saved profile selection failed' }
        Install-ProfileShortcut $fixture
        if (!(Test-Path -LiteralPath (Join-Path $fixture 'Profil-joueur.cmd'))) { throw 'Shortcut missing' }
        if (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'data\profiles') -Filter '*.json').Count -ne 1) { throw 'Saved profile overwritten' }
        foreach ($value in @('C','Test other scope','Other weapon','1x','34')) { $script:answers.Enqueue($value) }
        $copied = Invoke-PlayerProfileMenu $fixture
        if ($copied.Id -eq $created.Id -or $copied.Dpi -ne 1600 -or $copied.HorizontalSensitivity -ne 3 -or $copied.Resolution -ne '1920x1080' -or $copied.Scope -ne '1x' -or $copied.AdsSensitivity -ne 34) { throw 'Profile copy lost shared settings or reused identity' }
        if ((Get-Content (Join-Path $fixture ('data\profiles\' + $created.Id + '.json')) -Raw | ConvertFrom-Json).Scope -ne '2.5x') { throw 'Profile copy changed original' }
        $automatic = Initialize-PlayerProfile $fixture
        if ($automatic.Source -ne 'SettingsFile' -or $null -ne $automatic.Dpi -or $null -ne $automatic.Weapon) { throw 'Automatic import reused unverified manual context' }
        'PASS: optional legacy profiles and automatic launch without questions or stale declared values.'
    } finally { Remove-Item -LiteralPath Function:script:Read-Host }
} $root
Write-Host $result
