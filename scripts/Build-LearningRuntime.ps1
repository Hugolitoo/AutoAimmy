param([string]$PythonSource, [string]$SitePackages, [string]$Tag = 'v0.3.0')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$outputRoot = Join-Path $projectRoot 'artifacts\learning-runtime-package'
if (Test-Path -LiteralPath $outputRoot) { throw 'Learning runtime output already exists.' }
$runtime = Join-Path $outputRoot 'runtime'
$null = New-Item -ItemType Directory -Path $runtime -Force
foreach ($name in @('python.exe','python3.dll','python312.dll','vcruntime140.dll','vcruntime140_1.dll','LICENSE.txt','DLLs')) {
    Copy-Item -LiteralPath (Join-Path $PythonSource $name) -Destination $runtime -Recurse
}
$stdlibRoot = Join-Path $PythonSource 'Lib'
Get-ChildItem -LiteralPath $stdlibRoot -File -Recurse | Where-Object {
    $_.FullName -notmatch '\\(site-packages|__pycache__|test|tests)\\'
} | ForEach-Object {
    $relative = $_.FullName.Substring($stdlibRoot.Length + 1)
    $destination = Join-Path $runtime ('Lib\' + $relative)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}
$packagesRoot = [IO.Path]::GetFullPath($SitePackages)
Get-ChildItem -LiteralPath $packagesRoot -File -Recurse | Where-Object {
    $_.FullName -notmatch '\\__pycache__\\' -and $_.Extension -notin @('.pyc','.lib','.pdb')
} | ForEach-Object {
    $relative = $_.FullName.Substring($packagesRoot.Length + 1)
    $destination = Join-Path $runtime ('Lib\site-packages\' + $relative)
    $null = New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force
    Copy-Item -LiteralPath $_.FullName -Destination $destination
}
@'
AutoAimmy local learning runtime. CPU only. No player data or model weights.
Python 3.12.14, PyTorch 2.5.1+cpu, torchvision 0.20.1+cpu, Ultralytics 8.3.40,
ONNX 1.17.0, ONNX Runtime 1.20.1, NumPy 1.26.4, Pillow 11.0.0.
Python license: LICENSE.txt. Package licenses and notices: Lib/site-packages/*.dist-info.
Upstream source: https://github.com/python/cpython (Python), https://github.com/pytorch/pytorch
(PyTorch), https://github.com/ultralytics/ultralytics/tree/v8.3.40 (Ultralytics, AGPL-3.0).
Ultralytics is installed as an unmodified separately licensed runtime component.
'@ | Set-Content -LiteralPath (Join-Path $runtime 'THIRD-PARTY-NOTICES.txt') -Encoding UTF8
& (Join-Path $runtime 'python.exe') -I -c 'import torch,ultralytics,onnx,onnxruntime,numpy,PIL; print(torch.__version__,ultralytics.__version__)'
if ($LASTEXITCODE -ne 0) { throw 'Portable training runtime probe failed.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = Join-Path $outputRoot 'AutoAimmy-learning-win-x64.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($runtime, $archive, [IO.Compression.CompressionLevel]::Optimal, $false)
[ordered]@{ Schema=1; Version='1'; Runtime='win-x64'; Sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash;
    Size=(Get-Item -LiteralPath $archive).Length; Url="https://github.com/Hugolitoo/AutoAimmy/releases/download/$Tag/AutoAimmy-learning-win-x64.zip" } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'learning-runtime.json') -Encoding UTF8
Write-Host "Portable learning runtime ready: $archive"
