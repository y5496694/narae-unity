$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$exe=Join-Path $root 'dist\NaraeUnity\App\NaraeUnity.exe'
if(-not (Test-Path -LiteralPath $exe -PathType Leaf)){throw '먼저 Tools/build.ps1을 실행하세요.'}
$results=Join-Path $root '.build\test-results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
$process=Start-Process -FilePath $exe -ArgumentList '--self-test' -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $results 'stdout.json') -RedirectStandardError (Join-Path $results 'stderr.txt')
if(-not $process.WaitForExit(45000)){$process.Kill();throw '앱 자체 검사 시간이 초과되었습니다.'}
if($process.ExitCode -ne 0){throw "앱 자체 검사 실패. .build/test-results를 확인하세요. 종료 코드: $($process.ExitCode)"}
$report=Get-Content -LiteralPath (Join-Path $root 'dist\NaraeUnity\App\self-test.json') -Raw | ConvertFrom-Json
if($report.status -ne 'passed'){throw '검사 보고서가 성공 상태가 아닙니다.'}
Write-Output "앱 자체 검사 통과: $($report.checks.Count)개"
