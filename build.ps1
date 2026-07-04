$gameDir = "F:\SteamLibrary\steamapps\common\The Bazaar"
$managedDir = "$gameDir\TheBazaar_Data\Managed"
$bepInExDir = "$gameDir\BepInEx\core"
$pluginDir = "$gameDir\BepInEx\plugins"
$projectDir = "$gameDir\BazaarBoardReader"
$outputDll = "$projectDir\BazaarBoardReader.dll"
$csc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$refs = @()
$refs += "$managedDir\netstandard.dll"
$refs += "$managedDir\System.Runtime.dll"
$refs += "$bepInExDir\BepInEx.dll"
$refs += "$bepInExDir\0Harmony.dll"
$refs += "$managedDir\UnityEngine.dll"
$refs += "$managedDir\UnityEngine.CoreModule.dll"
$refs += "$managedDir\UnityEngine.InputLegacyModule.dll"
$refs += "$managedDir\UnityEngine.IMGUIModule.dll"
$refs += "$managedDir\UnityEngine.TextRenderingModule.dll"
$refs += "$managedDir\UnityEngine.UI.dll"
$refs += "$managedDir\UnityEngine.JSONSerializeModule.dll"
$refs += "$managedDir\BazaarBattleService.dll"
$refs += "$managedDir\BazaarGameClient.dll"
$refs += "$managedDir\BazaarGameShared.dll"
$refs += "$managedDir\TheBazaarRuntime.dll"
$refs += "$managedDir\Newtonsoft.Json.dll"

$refArgs = ""
foreach ($r in $refs) {
    $refArgs += "/r:`"$r`" "
}

$sourceFile = "$projectDir\BazaarBoardReaderPlugin.cs"
$cmd = "& `"$csc`" /target:library /out:`"$outputDll`" $refArgs `"$sourceFile`" 2>&1"
Write-Host "Compiling..."
Write-Host $cmd
Invoke-Expression $cmd

if ($LASTEXITCODE -eq 0) {
    Write-Host "SUCCESS: $outputDll"
    Copy-Item $outputDll "$pluginDir\BazaarBoardReader.dll" -Force
    Write-Host "Copied to plugins folder."
} else {
    Write-Host "FAILED with code: $LASTEXITCODE"
}
