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
        $reused = Initialize-PlayerProfile $fixture
        if ($script:answers.Count -ne 0) { throw 'Unconsumed wizard answers' }
        if ($created.Dpi -ne 1600 -or $created.AdsSensitivity -ne 55 -or $reused.Id -ne $created.Id) { throw 'Profile persistence mismatch' }
        $script:answers.Enqueue('S')
        $unknown = Invoke-PlayerProfileMenu $fixture
        if ($null -ne $unknown.Dpi) { throw 'Unknown DPI fabricated' }
        $script:answers.Enqueue('1')
        $selected = Invoke-PlayerProfileMenu $fixture
        if ($selected.Id -ne $created.Id) { throw 'Saved profile selection failed' }
        Install-ProfileShortcut $fixture
        if (!(Test-Path -LiteralPath (Join-Path $fixture 'Profil-joueur.cmd'))) { throw 'Shortcut missing' }
        if (@(Get-ChildItem -LiteralPath (Join-Path $fixture 'data\profiles') -Filter '*.json').Count -ne 1) { throw 'Saved profile overwritten' }
        'PASS: first setup, numeric validation, launch reuse without questions, unknown values, saved selection and shortcut.'
    } finally { Remove-Item -LiteralPath Function:script:Read-Host }
} $root
Write-Host $result
