# Install claudia-pet for the current user (no admin rights needed).
#
#   pwsh -NoProfile -File scripts\install.ps1 [-Connect]
#
# Copies dist\claudia-pet to %LOCALAPPDATA%\Programs\claudia-pet (build first with scripts\build.ps1).
# Settings, logs and character files live in %USERPROFILE%\.claudia and are kept across reinstalls
# (not in AppData: Claude's Store/MSIX build redirects new AppData folders made by its child processes).
# -Connect also turns on both Claude integrations (Claude Code plugin + desktop MCP); without it,
# turn them on from the pet's right-click menu > 设置 > 接入 Claude.
# Nothing is added to Windows startup: Claude brings the pet up through those integrations.

param([switch]$Connect)
$ErrorActionPreference = 'Stop'
$src = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\dist\claudia-pet'))
$dst = Join-Path $env:LOCALAPPDATA 'Programs\claudia-pet'
if (-not (Test-Path (Join-Path $src 'claudia-pet.exe'))) { throw "dist\claudia-pet not found; run scripts\build.ps1 first" }

# stop a running copy from this location only
Get-Process 'claudia-pet' -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$dst\*" } catch { $false } } | ForEach-Object {
    Write-Host "stopping running claudia-pet ($($_.Id))"
    Stop-Process -Id $_.Id -Force
}
Start-Sleep -Milliseconds 500

New-Item -ItemType Directory -Force $dst | Out-Null
robocopy $src $dst /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "copy failed (robocopy $LASTEXITCODE)" }
Get-ChildItem -LiteralPath $dst -Recurse -File | Unblock-File -ErrorAction SilentlyContinue
Write-Host "installed to $dst"

$exe = Join-Path $dst 'claudia-pet.exe'
if ($Connect) {
    $p = Start-Process -FilePath $exe -ArgumentList '--connect', 'all' -PassThru -Wait
    Get-Content (Join-Path $env:USERPROFILE '.claudia\windows.log') -Tail 2 -Encoding UTF8
}
Start-Process -FilePath $exe
Write-Host 'claudia-pet started. Open Claude and she will sit in its window.'
