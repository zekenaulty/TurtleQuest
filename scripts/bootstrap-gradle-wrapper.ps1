$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$modDir = Join-Path $root "mod/agentica-turtlequest-neoforge"
$gradleVersion = "8.14.3"
$downloadUrl = "https://services.gradle.org/distributions/gradle-$gradleVersion-bin.zip"
$toolsDir = Join-Path $root ".tools"
$zipPath = Join-Path $toolsDir "gradle-$gradleVersion-bin.zip"
$extractDir = Join-Path $toolsDir "gradle-$gradleVersion"
$gradleBat = Join-Path $extractDir "bin/gradle.bat"

New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

if (-not (Test-Path $gradleBat)) {
    if (-not (Test-Path $zipPath)) {
        Write-Host "Downloading Gradle $gradleVersion"
        Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath
    }

    Write-Host "Extracting Gradle $gradleVersion"
    Expand-Archive -Path $zipPath -DestinationPath $toolsDir -Force
}

Push-Location $modDir
try {
    & $gradleBat wrapper --gradle-version $gradleVersion --distribution-type bin
} finally {
    Pop-Location
}

