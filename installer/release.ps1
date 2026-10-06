# Builds the installer and publishes it as a GitHub release, which is what the app's
# "Check for updates" looks at. Run this after bumping <Version> in the .csproj and pushing.
#
#   .\installer\release.ps1                      # release notes written by GitHub from the commits
#   .\installer\release.ps1 -NotesFile notes.md  # your own release notes
#   .\installer\release.ps1 -Draft               # upload as a draft to look over first (the app ignores drafts)
#
# Requires the GitHub CLI, signed in:  gh auth login

param(
    [string]$NotesFile,
    [switch]$Draft
)

$ErrorActionPreference = 'Stop'
$repo = 'PizzaHeroGaming/PizzaHeroClicker'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\PizzaHeroClicker\PizzaHeroClicker.csproj'
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }
$tag = "v$version"

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'The GitHub CLI (gh) was not found. Install it from https://cli.github.com and run: gh auth login' }
gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The GitHub CLI is not signed in. Run: gh auth login' }

# The app compares version numbers, so a release must never be replaced under the same number.
$existing = gh release list --repo $repo --limit 200 --json tagName --jq '.[].tagName'
if ($LASTEXITCODE -ne 0) { throw 'Could not list the existing releases.' }
if ($existing -contains $tag) { throw "Release $tag already exists. Bump <Version> in the .csproj first." }

# What gets tagged is what is on GitHub, so everything has to be committed and pushed.
Push-Location $root
try {
    if (git status --porcelain) { throw 'There are uncommitted changes. Commit and push them first.' }
    git fetch origin --quiet
    if ((git rev-parse HEAD) -ne (git rev-parse '@{u}')) { throw 'This branch is not in step with GitHub. Push (or pull) first.' }
    $commit = git rev-parse HEAD
} finally {
    Pop-Location
}

& (Join-Path $PSScriptRoot 'build-installer.ps1')
$setup = Join-Path $PSScriptRoot "output\PizzaHeroClicker-Setup-$version.exe"
if (-not (Test-Path $setup)) { throw "The installer was not built: $setup" }
Write-Host "SHA-256: $((Get-FileHash $setup -Algorithm SHA256).Hash.ToLower())"

# A second copy under a name that never changes, so one link always downloads the newest version:
# https://github.com/PizzaHeroGaming/PizzaHeroClicker/releases/latest/download/PizzaHeroClicker-Setup.exe
# (The app's updater uses the versioned file and ignores this one.)
$stable = Join-Path $PSScriptRoot 'output\PizzaHeroClicker-Setup.exe'
Copy-Item $setup $stable -Force

$arguments = @('release', 'create', $tag, $setup, $stable, '--repo', $repo, '--target', $commit, '--title', "Pizza Hero Clicker $tag")
if ($NotesFile) { $arguments += @('--notes-file', $NotesFile) } else { $arguments += '--generate-notes' }
if ($Draft) { $arguments += '--draft' }
gh @arguments
if ($LASTEXITCODE -ne 0) { throw 'Creating the release failed.' }

Write-Host "Published $tag. Copies of the app will offer it the next time they check for updates."
