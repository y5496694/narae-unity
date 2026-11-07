param([ValidateSet('Release','Debug')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$dist=Join-Path $root 'dist'
$app=Join-Path $dist 'NaraeUnity\App'
$artifacts=Join-Path $root '.build'
& dotnet publish (Join-Path $root 'Source\NaraeUnity.csproj') -c $Configuration -r win-x64 --self-contained true -o $app --artifacts-path $artifacts
if($LASTEXITCODE -ne 0){throw '앱 빌드가 실패했습니다.'}
$example=Join-Path $root 'examples\LastGripTower'
Copy-Item -LiteralPath $example -Destination $dist -Recurse -Force
$evidence=Join-Path $dist 'NaraeUnity\Evidence'
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'Docs\analysis-summary.json') -Destination (Join-Path $evidence 'findings.json')
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $dist 'NaraeUnity\README.md')
Write-Output "빌드 완료: $app"
