$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    $releaseDir = Join-Path $PSScriptRoot 'release'
    $exePath = Join-Path $releaseDir 'TouchPadAdvanced.exe'

    Get-Process TouchPadAdvanced -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 300

    dotnet publish TouchPadAdvanced.csproj -c Release --nologo -o $releaseDir
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not (Test-Path $runKey)) { New-Item -Path $runKey -Force | Out-Null }
    Set-ItemProperty -Path $runKey -Name 'TouchPadAdvanced' -Value "`"$exePath`" --startup" -Type String

    Start-Process -FilePath $exePath -WorkingDirectory $releaseDir
}
finally { Pop-Location }
