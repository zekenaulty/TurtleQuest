$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$lockPath = Join-Path $root "third_party/mods.lock.json"
$modsDir = Join-Path $root "run/mods"

New-Item -ItemType Directory -Force -Path $modsDir | Out-Null

$lock = Get-Content -Raw -Path $lockPath | ConvertFrom-Json

foreach ($mod in $lock.mods) {
    $target = Join-Path $modsDir $mod.filename
    if (-not (Test-Path $target)) {
        Write-Host "Downloading $($mod.filename)"
        Invoke-WebRequest -Uri $mod.url -OutFile $target
    } else {
        Write-Host "Found $($mod.filename)"
    }

    $actual = (Get-FileHash -Path $target -Algorithm SHA512).Hash.ToLowerInvariant()
    if ($actual -ne $mod.sha512.ToLowerInvariant()) {
        Remove-Item -LiteralPath $target -Force
        throw "Hash mismatch for $($mod.filename). Removed downloaded file."
    }

    Write-Host "Verified $($mod.filename)"
}

