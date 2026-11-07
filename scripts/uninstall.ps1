# Uninstall claudia-pet for the current user.
#
#   pwsh -NoProfile -File scripts\uninstall.ps1 [-KeepData]
#
# Removes both Claude integrations (only claudia-pet's own entries, including the old 0.1.0 "Claudia" ones;
# backups are made first), stops the pet, and deletes %LOCALAPPDATA%\Programs\claudia-pet
# (and the 0.1.0 location %LOCALAPPDATA%\Programs\Claudia).
# Settings and logs in %USERPROFILE%\.claudia (and the 0.1.0 location %LOCALAPPDATA%\Claudia)
# are deleted too unless -KeepData is given.

param([switch]$KeepData)
$ErrorActionPreference = 'Stop'
$dst = Join-Path $env:LOCALAPPDATA 'Programs\claudia-pet'
$oldDst = Join-Path $env:LOCALAPPDATA 'Programs\Claudia'
$data = Join-Path $env:USERPROFILE '.claudia'
$oldData = Join-Path $env:LOCALAPPDATA 'Claudia'
$exe = Join-Path $dst 'claudia-pet.exe'

if (Test-Path $exe) {
    $p = Start-Process -FilePath $exe -ArgumentList '--disconnect', 'all' -WindowStyle Hidden -PassThru -Wait
    if (Test-Path -LiteralPath (Join-Path $dst '.claudia-pet-build-output') -PathType Leaf) {
        if ($p.ExitCode -ne 0) { throw "Claude integration could not be fully removed (exit $($p.ExitCode)). The installed files and data were kept." }
        Write-Host 'Claude integrations removed (restart Claude to apply).'
    } else {
        # Older installs (0.2.0 and before) always exit 0, so the result cannot be checked; uninstall anyway.
        Write-Warning "This older install cannot report whether the integrations were removed. Check that ~\.claude\settings.json (env.CLAUDE_CODE_PLUGIN_DIRS) and Claude's claude_desktop_config.json (mcpServers) no longer point to claudia-pet."
    }
}
foreach ($name in 'claudia-pet', 'Claudia') {
    Get-Process $name -ErrorAction SilentlyContinue | Where-Object { try { $_.Path -like "$dst\*" -or $_.Path -like "$oldDst\*" } catch { $false } } | ForEach-Object { Stop-Process -Id $_.Id -Force }
}
Start-Sleep -Milliseconds 500
foreach ($d in $dst, $oldDst) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force; Write-Host "removed $d" } }
if (-not $KeepData) {
    foreach ($d in $data, $oldData) { if (Test-Path $d) { Remove-Item -LiteralPath $d -Recurse -Force; Write-Host "removed $d" } }
}
