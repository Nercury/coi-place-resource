#!/usr/bin/env pwsh
# Release script for PlaceResourceMod.
#
# Usage:
#   ./release.ps1            Release the version currently in manifest.json.
#   ./release.ps1 -DryRun    Print every step without creating tags or releases.
#
# Prereqs:
#   - COI installed, COI_ROOT env var pointing at the install root.
#   - .NET SDK 8 or newer (`dotnet build` available).
#   - gh CLI installed (`winget install GitHub.cli`) and authed (`gh auth login`).
#   - Push access to the `main` branch on origin.
#   - Working tree clean, on `main`, no unpushed commits.
#   - manifest.json `version` and PlaceResourceMod.csproj `<Version>` match.
#   - CHANGELOG.md has a `## [<version>]` section for the version being released.

[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-Location -Path $PSScriptRoot

function Fail([string]$msg) {
    Write-Host "ERROR: $msg" -ForegroundColor Red
    exit 1
}

function Step([string]$msg) {
    Write-Host "==> $msg" -ForegroundColor Cyan
}

# 1. Read versions from manifest and csproj, verify they match.
Step 'Reading version from manifest.json and csproj'
$manifest = Get-Content -Raw -Path 'manifest.json' | ConvertFrom-Json
$manifestVersion = $manifest.version
if (-not $manifestVersion) { Fail 'manifest.json has no `version` field' }

$csprojXml = [xml](Get-Content -Raw -Path 'PlaceResourceMod.csproj')
$csprojVersion = $csprojXml.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $csprojVersion) { Fail 'PlaceResourceMod.csproj has no <Version> element' }

if ($manifestVersion -ne $csprojVersion) {
    Fail "Version mismatch: manifest.json=$manifestVersion, csproj=$csprojVersion. Bump both in lockstep."
}
$version = $manifestVersion
$tag = "v$version"
Write-Host "    version: $version  (tag: $tag)"

# 2. Verify gh CLI is installed and authed.
Step 'Checking gh CLI'
$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Fail "gh CLI not found. Install with: winget install GitHub.cli  then run: gh auth login"
}
& gh auth status *>&1 | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail "gh CLI not authenticated. Run: gh auth login"
}

# 3. Working tree clean.
Step 'Checking working tree is clean'
$status = git status --porcelain
if ($status) {
    Fail "Working tree is dirty. Commit or stash changes first.`n$status"
}

# 4. On main, no unpushed commits.
Step 'Checking branch is main and up to date with origin'
$branch = git rev-parse --abbrev-ref HEAD
if ($branch -ne 'main') {
    Fail "Current branch is '$branch', expected 'main'."
}
git fetch origin main *>&1 | Out-Null
$ahead = (git rev-list --count 'origin/main..HEAD').Trim()
$behind = (git rev-list --count 'HEAD..origin/main').Trim()
if ($ahead -ne '0') { Fail "Local main is $ahead commit(s) ahead of origin. Push first." }
if ($behind -ne '0') { Fail "Local main is $behind commit(s) behind origin. Pull first." }

# 5. Tag must not already exist locally or on origin.
Step "Checking tag $tag does not already exist"
$localTag = git tag --list $tag
if ($localTag) { Fail "Tag $tag already exists locally." }
$remoteTag = git ls-remote --tags origin $tag
if ($remoteTag) { Fail "Tag $tag already exists on origin." }

# 6. Extract release notes from CHANGELOG.md.
Step 'Extracting release notes from CHANGELOG.md'
if (-not (Test-Path 'CHANGELOG.md')) { Fail 'CHANGELOG.md not found.' }
$changelog = Get-Content -Raw -Path 'CHANGELOG.md'
# Format per version: `vX.Y.Z | YYYY-MM-DD` header, then bullet lines, until the next `vX...` header or EOF.
$pattern = "(?ms)^v$([regex]::Escape($version)) \|[^\n]*\n(.*?)(?=^v\d|\z)"
$match = [regex]::Match($changelog, $pattern)
if (-not $match.Success) {
    Fail "No 'v$version | ...' section in CHANGELOG.md. Add one before releasing."
}
$notes = $match.Groups[1].Value.Trim()
if (-not $notes) { Fail "CHANGELOG.md section for v$version is empty." }
Write-Host "    notes preview:"
$notes -split "`n" | Select-Object -First 6 | ForEach-Object { Write-Host "      $_" }

# 7. Build, verify zip exists.
Step 'Building Release configuration'
$zipPath = Join-Path -Path 'bin/Release/net48' -ChildPath "PlaceResourceMod-$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath }

if ($DryRun) {
    Write-Host "    [dry-run] would run: dotnet build -c Release"
} else {
    & dotnet build -c Release
    if ($LASTEXITCODE -ne 0) { Fail "dotnet build failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $zipPath)) { Fail "Expected zip at $zipPath, not produced by build." }
    $zipSize = (Get-Item $zipPath).Length
    Write-Host "    zip ready: $zipPath ($zipSize bytes)"
}

# 8. Create + push tag.
Step "Creating and pushing tag $tag"
if ($DryRun) {
    Write-Host "    [dry-run] would run: git tag -a $tag -m '$tag'"
    Write-Host "    [dry-run] would run: git push origin $tag"
} else {
    & git tag -a $tag -m $tag
    if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }
    & git push origin $tag
    if ($LASTEXITCODE -ne 0) { Fail "git push origin $tag failed" }
}

# 9. Create GitHub release with the zip attached.
Step "Creating GitHub release $tag"
if ($DryRun) {
    Write-Host "    [dry-run] would run: gh release create $tag '$zipPath' --title '$tag' --notes <CHANGELOG section>"
} else {
    $tmpNotesFile = New-TemporaryFile
    Set-Content -Path $tmpNotesFile -Value $notes -NoNewline
    try {
        & gh release create $tag $zipPath --title $tag --notes-file $tmpNotesFile
        if ($LASTEXITCODE -ne 0) { Fail "gh release create failed" }
    } finally {
        Remove-Item $tmpNotesFile -ErrorAction SilentlyContinue
    }
}

Step "Done. Release: https://github.com/Nercury/coi-place-resource/releases/tag/$tag"
