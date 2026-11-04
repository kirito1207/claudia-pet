$ErrorActionPreference = 'Stop'
& node (Join-Path $PSScriptRoot 'test-physics.cjs')
if ($LASTEXITCODE -ne 0) { throw 'DSH physics/controller regression failed' }
