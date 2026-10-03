<#
.SYNOPSIS
    Bumps grzyClothTool's version, commits and tags it.

.DESCRIPTION
    The single source of truth is <FileVersion> in grzyClothTool/grzyClothTool.csproj: the in-app updater compares
    the running exe's FileVersion with that element on master, then downloads
    releases/download/v<version>/grzyClothTool.zip. Pushing the tag triggers .github/workflows/release.yml, which
    builds and publishes that release.

.EXAMPLE
    ./scripts/bump-version.ps1 patch          # 1.4.1 -> 1.4.2, commit + tag v1.4.2 (local only)
    ./scripts/bump-version.ps1 minor -Push    # 1.4.1 -> 1.5.0, commit + tag, push branch and tag
#>
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Part,

    [switch]$Push
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$csproj = Join-Path $repoRoot 'grzyClothTool/grzyClothTool.csproj'

function Invoke-Git {
    git -C $repoRoot @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit $LASTEXITCODE)" }
}

if (git -C $repoRoot status --porcelain) {
    throw 'Working tree is not clean. Commit or stash your changes first.'
}

$content = [IO.File]::ReadAllText($csproj)
$pattern = '<FileVersion>(\d+)\.(\d+)\.(\d+)</FileVersion>'
$match = [regex]::Match($content, $pattern)
if (-not $match.Success) {
    throw "No <FileVersion>X.Y.Z</FileVersion> found in $csproj"
}

$major = [int]$match.Groups[1].Value
$minor = [int]$match.Groups[2].Value
$patch = [int]$match.Groups[3].Value
$current = "$major.$minor.$patch"

switch ($Part) {
    'major' { $major++; $minor = 0; $patch = 0 }
    'minor' { $minor++; $patch = 0 }
    'patch' { $patch++ }
}
$next = "$major.$minor.$patch"
$tag = "v$next"

if (git -C $repoRoot tag --list $tag) {
    throw "Tag $tag already exists."
}

# Keep the file's BOM / line endings: only the version text changes.
$hasBom = [IO.File]::ReadAllBytes($csproj)[0..2] -join ',' -eq '239,187,191'
$updated = [regex]::Replace($content, $pattern, "<FileVersion>$next</FileVersion>", 1)
[IO.File]::WriteAllText($csproj, $updated, [Text.UTF8Encoding]::new($hasBom))

Invoke-Git add -- $csproj
Invoke-Git commit -m ":bookmark: $tag"
Invoke-Git tag -a $tag -m $tag

Write-Host "Bumped $current -> $next (tag $tag)" -ForegroundColor Green

$branch = git -C $repoRoot rev-parse --abbrev-ref HEAD
if ($branch -ne 'master') {
    Write-Warning "You are on '$branch'. The in-app updater only sees versions that reach master."
}

if ($Push) {
    Invoke-Git push origin $branch
    Invoke-Git push origin $tag
    Write-Host "Pushed $branch and $tag. The Release workflow will build and publish the release." -ForegroundColor Green
}
else {
    Write-Host "Not pushed. When ready: git push origin $branch; git push origin $tag"
}
