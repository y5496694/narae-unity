param(
    [ValidateSet('Validate','Export','Render')][string]$Action = 'Validate',
    [string]$EditorPath = '',
    [int]$TimeoutSeconds = 900
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$workspaceRoot = Split-Path -Parent (Split-Path -Parent $projectRoot)
$buildRoot = Join-Path $projectRoot 'Build'
New-Item -ItemType Directory -Path $buildRoot -Force | Out-Null
$logPath = Join-Path $buildRoot ('unity-' + $Action + '.log')
$independentExporter = Join-Path $PSScriptRoot 'IndependentExporter\export_tower.py'
if ($Action -eq 'Export' -and (Test-Path -LiteralPath $independentExporter -PathType Leaf)) {
    # The original serializer emits our authored scene directly. It does not
    # start Unity Editor or change any license or original game executable.
    $pythonCommand = Get-Command python -ErrorAction Stop
    $targetPath = Join-Path $buildRoot 'LastGrip'
    & $pythonCommand.Source $independentExporter $targetPath *> $logPath
    if ($LASTEXITCODE -ne 0) { throw "독립 맵 내보내기가 실패했습니다. 로그: $logPath" }
    foreach ($requiredName in @('data','metadata.json','thumbnail.png','export-report.json')) {
        $requiredPath = Join-Path $targetPath $requiredName
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf) -or (Get-Item -LiteralPath $requiredPath).Length -eq 0) {
            throw "내보내기 결과가 없습니다: $requiredPath"
        }
    }
    Write-Output "LAST_GRIP_EXPORT_PASSED: $targetPath"
    return
}
if (-not $EditorPath) {
    $candidates = @(
        (Join-Path $workspaceRoot 'work\Unity2017Portable\Editor\Unity.exe'),
        'C:\Program Files\Unity\Hub\Editor\2017.4.13f1\Editor\Unity.exe',
        'C:\Program Files\Unity 2017.4.13f1\Editor\Unity.exe'
    )
    $EditorPath = $candidates | Where-Object {Test-Path -LiteralPath $_ -PathType Leaf} | Select-Object -First 1
}
if (-not $EditorPath -or -not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
    throw 'Unity 2017.4.13f1 에디터가 필요합니다. -EditorPath 인수로 Unity.exe 경로를 지정하세요.'
}
$method = @{Validate='Validate'; Export='ExportLocal'; Render='RenderPreviews'}[$Action]
$unityArguments = @('-batchmode','-quit','-projectPath',('"'+$projectRoot+'"'),'-logFile',('"'+$logPath+'"'),'-executeMethod',('LastGripCli.'+$method))
if ($Action -ne 'Render') { $unityArguments += '-nographics' }
# This launches no visible editor window and sends no desktop input.
$process = Start-Process -FilePath $EditorPath -ArgumentList $unityArguments -WorkingDirectory $projectRoot -WindowStyle Hidden -PassThru
$timer = [Diagnostics.Stopwatch]::StartNew()
while (-not $process.WaitForExit(1000)) {
    if (Test-Path -LiteralPath $logPath) {
        $tail = Get-Content -LiteralPath $logPath -Tail 60 -ErrorAction SilentlyContinue
        if ($tail -match 'Unity has not been activated with a valid License') {
            $process.Kill()
            $process.WaitForExit()
            throw "Unity 라이선스가 활성화되어 있지 않아 실행을 중단했습니다. 로그: $logPath"
        }
    }
    if ($timer.Elapsed.TotalSeconds -gt $TimeoutSeconds) {
        $process.Kill()
        $process.WaitForExit()
        throw "Unity 실행 시간이 제한을 넘었습니다. 로그: $logPath"
    }
}
if ($process.ExitCode -ne 0) { throw "Unity 종료 코드 $($process.ExitCode). 로그: $logPath" }
$expected = @{Validate='LAST_GRIP_VALIDATION_PASSED'; Export='LAST_GRIP_EXPORT_PASSED'; Render='LAST_GRIP_PREVIEW_PASSED'}[$Action]
if (-not (Select-String -LiteralPath $logPath -SimpleMatch $expected -Quiet)) {
    throw "완료 표시를 확인하지 못했습니다. 로그: $logPath"
}
Write-Output "$Action 완료. 로그: $logPath"
