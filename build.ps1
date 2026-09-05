$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Stop-Kinolincopy {
    $names = @('kinolincopy', '复制档案')
    $stopped = @()
    Get-Process -ErrorAction SilentlyContinue |
        Where-Object {
            $names -contains $_.ProcessName -or
            ($_.Path -and (
                $_.Path -like "$root\release\*" -or
                $_.Path -like "$root\bin\*"
            ))
        } |
        ForEach-Object {
            $stopped += ('{0}({1})' -f $_.ProcessName, $_.Id)
            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
        }

    if ($stopped.Count -gt 0) {
        Write-Host ("Stopped: {0}" -f ($stopped -join ', '))
        Start-Sleep -Milliseconds 800
    }
}

powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\gen-version.ps1" `
    -OutFile "$root\src\VersionInfo.g.cs" `
    -PropsFile "$root\obj\GitVersion.g.props"

Stop-Kinolincopy

if (Test-Path "$root\release") {
    Remove-Item "$root\release" -Recurse -Force
}

dotnet publish -c Release -o "$root\release"

Get-ChildItem "$root\release" -File |
    Where-Object { $_.Extension -ieq '.config' } |
    Remove-Item -Force

Get-ChildItem "$root\release" -File | ForEach-Object {
    '{0} = {1:N1} KB' -f $_.Name, ($_.Length / 1KB)
}
