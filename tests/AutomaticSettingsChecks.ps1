$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) 'distribution\AutomaticSettings.psm1') -Force
$fixture = Join-Path $env:TEMP ('AutoAimmy-automatic-checks-' + [guid]::NewGuid().ToString('N'))
$root = Join-Path $fixture 'install'
$game = Join-Path $fixture 'game'
$null = New-Item -ItemType Directory -Path (Join-Path $game 'account1') -Force
$settings = Join-Path $game 'account1\GameSettings.ini'
$text = @'
[DISPLAY_SETTINGS]
ResolutionWidth=1920
ResolutionHeight=1080
DefaultFOV=90.000000
AspectRatio=4
[INPUT]
MouseYawSensitivity=3
MousePitchSensitivity=3
MouseSensitivityMultiplierUnit=0.02
ADSMouseUseSpecific=1
ADSMouseSensitivityGlobal=50
ADSMouseSensitivity2xHalf=55
ADSMouseSensitivity1x=34
ADSMouseMultiplierUnit=0.02
[ONLINE]
Secret=NEVER EXPORT
'@
Set-Content -LiteralPath $settings -Value $text
$originalHash = (Get-FileHash $settings).Hash
$profile = Sync-AutomaticPlayerProfile $root -SearchDirectories @($game)
if ($profile.ImportStatus -ne 'Imported' -or $profile.HorizontalSensitivity -ne 3 -or $profile.Resolution -ne '1920x1080' -or $profile.AdsSensitivityByScope.ADSMouseSensitivity2xHalf -ne 55) { throw 'Automatic settings import mismatch' }
if ($null -ne $profile.Dpi -or $null -ne $profile.Scope -or $null -ne $profile.Weapon -or $null -ne $profile.AdsSensitivity -or $null -ne $profile.AspectRatio) { throw 'Unobserved context fabricated' }
if ((Get-FileHash $settings).Hash -ne $originalHash) { throw 'Importer changed game settings' }
$exported = Get-Content (Join-Path $root 'data\active-profile.json') -Raw
if ($exported.Contains('NEVER EXPORT') -or $exported.Contains('account1') -or $exported.Contains($fixture)) { throw 'Imported private fields or account path' }
Set-Content $settings ($text.Replace('MouseYawSensitivity=3','MouseYawSensitivity=7'))
$updated = Sync-AutomaticPlayerProfile $root -SearchDirectories @($game)
if ($updated.HorizontalSensitivity -ne 7) { throw 'Settings were not refreshed' }
$missing = Sync-AutomaticPlayerProfile $root -SearchDirectories @()
if ($missing.ImportStatus -ne 'NotFound' -or $null -ne $missing.HorizontalSensitivity) { throw 'Missing settings reused stale values' }
$null = New-Item -ItemType Directory -Path (Join-Path $game 'account2') -Force
Set-Content (Join-Path $game 'account2\GameSettings.ini') $text
$ambiguous = Sync-AutomaticPlayerProfile $root -SearchDirectories @($game)
if ($ambiguous.ImportStatus -ne 'ImportedCandidate' -or $null -eq $ambiguous.HorizontalSensitivity) { throw 'Multiple accounts did not expose a clearly provisional candidate' }
$choices = Get-Content (Join-Path $root 'data\settings-candidates.json') -Raw | ConvertFrom-Json
$selected = Sync-AutomaticPlayerProfile $root -SearchDirectories @($game) -SelectedFileKey $choices[0].Key
if ($selected.ImportStatus -ne 'Imported' -or $selected.HorizontalSensitivity -ne 7) { throw 'One-click account selection failed' }
$remembered = Sync-AutomaticPlayerProfile $root -SearchDirectories @($game)
if ($remembered.ImportStatus -ne 'Imported' -or $remembered.HorizontalSensitivity -ne 7) { throw 'Account selection not remembered' }
Set-Content $settings ($text.Replace('MouseYawSensitivity=3','MouseYawSensitivity=NaN').Replace('DefaultFOV=90.000000','DefaultFOV=999'))
$invalid = Sync-AutomaticPlayerProfile $root -SearchDirectories @((Join-Path $game 'account1'))
if ($null -ne $invalid.HorizontalSensitivity -or $null -ne $invalid.Fov -or $invalid.VerticalSensitivity -ne 3) { throw 'Invalid numbers accepted or valid fields discarded' }
Write-Host 'PASS: import, source privacy, read-only access, refresh, missing/ambiguous settings and invalid numbers.'
