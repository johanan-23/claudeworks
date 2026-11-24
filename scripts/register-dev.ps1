<#
.SYNOPSIS
  Registers a developer build of the add-in with SOLIDWORKS (no installer).
.DESCRIPTION
  Writes the same COM + SOLIDWORKS add-in keys the installer writes, pointing at the given build
  folder. -Scope User needs no admin rights (COM class under HKCU\Software\Classes; the add-in key
  under HKCU\Software\SolidWorks\AddIns). -Scope Machine writes HKLM (run elevated).
  -Unregister removes everything again.
#>
param(
    [string]$BuildDir = (Join-Path $PSScriptRoot "..\src\ClaudeSW.AddIn\bin\Debug\net48"),
    [ValidateSet("User", "Machine")][string]$Scope = "User",
    [switch]$Unregister
)
$ErrorActionPreference = "Stop"
$guid = "{ABA6C017-84D2-41CD-BD49-E617BE823AB0}"
$progId = "ClaudeForSolidWorks.AddIn"
$class = "ClaudeSW.AddIn.SwAddin"
$root = if ($Scope -eq "User") { "HKCU:\Software" } else { "HKLM:\SOFTWARE" }
$classes = "$root\Classes"

if ($Unregister) {
    foreach ($k in "$classes\CLSID\$guid", "$classes\$progId", "$root\SolidWorks\AddIns\$guid", "HKCU:\Software\SolidWorks\AddInsStartup\$guid") {
        if (Test-Path $k) { Remove-Item $k -Recurse -Force; Write-Host "removed $k" }
    }
    return
}

$dll = (Resolve-Path (Join-Path $BuildDir "ClaudeSW.AddIn.dll")).Path
$asmName = [Reflection.AssemblyName]::GetAssemblyName($dll)
$codeBase = "file:///" + $dll.Replace("\", "/")
$version = $asmName.Version.ToString()

$clsid = "$classes\CLSID\$guid"
New-Item -Force $clsid | Out-Null
Set-ItemProperty $clsid "(default)" $class
New-Item -Force "$clsid\ProgId" | Out-Null
Set-ItemProperty "$clsid\ProgId" "(default)" $progId
New-Item -Force "$clsid\Implemented Categories\{62C8FE65-4EBB-45e7-B440-6E39B2CDBF29}" | Out-Null
foreach ($k in "$clsid\InprocServer32", "$clsid\InprocServer32\$version") {
    New-Item -Force $k | Out-Null
    if ($k -eq "$clsid\InprocServer32") {
        Set-ItemProperty $k "(default)" "mscoree.dll"
        Set-ItemProperty $k "ThreadingModel" "Both"
    }
    Set-ItemProperty $k "Class" $class
    Set-ItemProperty $k "Assembly" $asmName.FullName
    Set-ItemProperty $k "RuntimeVersion" "v4.0.30319"
    Set-ItemProperty $k "CodeBase" $codeBase
}
New-Item -Force "$classes\$progId\CLSID" | Out-Null
Set-ItemProperty "$classes\$progId" "(default)" $class
Set-ItemProperty "$classes\$progId\CLSID" "(default)" $guid

$addin = "$root\SolidWorks\AddIns\$guid"
New-Item -Force $addin | Out-Null
Set-ItemProperty $addin "(default)" 1 -Type DWord
Set-ItemProperty $addin "Title" "ClaudeWorks"
Set-ItemProperty $addin "Description" "Chat with Claude in the Task Pane to inspect, build and edit parts, assemblies and drawings."
New-Item -Force "HKCU:\Software\SolidWorks\AddInsStartup\$guid" | Out-Null
Set-ItemProperty "HKCU:\Software\SolidWorks\AddInsStartup\$guid" "(default)" 1 -Type DWord
Write-Host "Registered $dll ($Scope scope)"
