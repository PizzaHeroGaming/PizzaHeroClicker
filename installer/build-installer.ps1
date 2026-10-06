# Publishes the app and compiles the installer.
# Result: installer\output\PizzaHeroClicker-Setup-<version>.exe
# Requires the .NET SDK and Inno Setup 6.3+ (https://jrsoftware.org/isinfo.php).

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\PizzaHeroClicker\PizzaHeroClicker.csproj'

# The version comes from the project file, so the installer can never disagree with the exe.
$version = ([xml](Get-Content $project)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> found in $project" }

# Find the Inno Setup compiler.
$iscc = (Get-Command iscc -ErrorAction SilentlyContinue).Source
if (-not $iscc) {
    $iscc = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 was not found. Install it from https://jrsoftware.org/isinfo.php and run this again.' }

Write-Host "Publishing Pizza Hero Clicker $version..."
# A private staging folder, so this works even while a copy from publish\ is running.
$publish = Join-Path $PSScriptRoot 'staging'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
dotnet publish $project -c Release -o $publish -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

Write-Host 'Compiling the installer...'
& $iscc "/DAppVersion=$version" /Q (Join-Path $PSScriptRoot 'PizzaHeroClicker.iss')
if ($LASTEXITCODE -ne 0) { throw 'The Inno Setup compiler reported an error.' }

$setup = Join-Path $PSScriptRoot "output\PizzaHeroClicker-Setup-$version.exe"
Write-Host "Done: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)"
