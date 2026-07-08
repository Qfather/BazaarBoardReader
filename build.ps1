param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

function Copy-DllWithRetry {
    param(
        [string]$Source,
        [string]$Destination,
        [string]$Label
    )

    for ($attempt = 1; $attempt -le 3; $attempt++) {
        try {
            Copy-Item -LiteralPath $Source -Destination $Destination -Force
            Write-Host "Copied to: $Destination"
            return $true
        }
        catch [System.IO.IOException] {
            if ($attempt -lt 3) {
                Write-Host "$Label is busy, retrying... ($attempt/3)"
                Start-Sleep -Milliseconds 500
            }
            else {
                Write-Warning "$Label is locked: $Destination"
                Write-Warning "Close The Bazaar, then run build.ps1 again to deploy the new DLL."
                return $false
            }
        }
    }

    return $false
}

# Auto-detect game root by searching upward for TheBazaar.exe
$gameDir = $PSScriptRoot
while ($gameDir -and !(Test-Path (Join-Path $gameDir "TheBazaar.exe"))) {
    $gameDir = Split-Path -Parent $gameDir
}
if (!$gameDir) {
    Write-Error "Cannot find game directory (TheBazaar.exe not found). Ensure this script is inside a subfolder of the game directory."
    exit 1
}

$projectDir = $PSScriptRoot
$projectFile = Join-Path $projectDir "BazaarBoardReader.csproj"
$pluginDir = Join-Path $gameDir "BepInEx\plugins"
$buildDll = Join-Path $projectDir "bin\$Configuration\net472\BazaarBoardReader.dll"
$rootDll = Join-Path $projectDir "BazaarBoardReader.dll"
$pluginDll = Join-Path $pluginDir "BazaarBoardReader.dll"

Write-Host "Game directory: $gameDir"
Write-Host "Configuration: $Configuration"

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Error "dotnet SDK was not found in PATH. Install .NET SDK 8.0+ or open this from a shell where dotnet is available."
    exit 1
}

Write-Host "Building project..."
dotnet build $projectFile -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet build failed with code: $LASTEXITCODE"
    exit $LASTEXITCODE
}

if (!(Test-Path $buildDll)) {
    Write-Error "Build succeeded, but output DLL was not found: $buildDll"
    exit 1
}

if (!(Test-Path $pluginDir)) {
    New-Item -ItemType Directory -Path $pluginDir | Out-Null
}

$rootCopied = Copy-DllWithRetry -Source $buildDll -Destination $rootDll -Label "Root DLL"
$pluginCopied = Copy-DllWithRetry -Source $buildDll -Destination $pluginDll -Label "Plugin DLL"

Write-Host "SUCCESS: $buildDll"
if (!$rootCopied -or !$pluginCopied) {
    exit 2
}
