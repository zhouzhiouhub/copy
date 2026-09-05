$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

powershell -NoProfile -ExecutionPolicy Bypass -File "$root\tools\gen-version.ps1" `
    -OutFile "$root\src\VersionInfo.g.cs" `
    -PropsFile "$root\obj\GitVersion.g.props"

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
