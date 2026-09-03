# PowerShell script to deploy the GMDC Revit plugin
# Run this script to copy the compiled plugin to the Revit 2025 add-ins directory.

$ErrorActionPreference = "Stop"

# 1. Target directory for Revit 2025 Addins
$addinFolder = "$env:APPDATA\Autodesk\Revit\Addins\2025"
$sourceDir = "d:\Revit\Plugins\GMDC\bin\Release\net8.0-windows"
if (!(Test-Path $sourceDir)) {
    $sourceDir = "d:\Revit\Plugins\GMDC\bin\Debug\net8.0-windows"
}

# 2. Check if Revit is running (Revit locks assemblies in memory, blocking deployment)
$revitProcess = Get-Process -Name Revit -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Warning "====================================================================="
    Write-Warning "Autodesk Revit IS CURRENTLY RUNNING!"
    Write-Warning "Revit locks the GMDC.dll assembly in memory when loaded."
    Write-Warning "You CANNOT update the plugin while Revit is running."
    Write-Warning "Please CLOSE Revit and then run this deploy script again."
    Write-Warning "====================================================================="
    Read-Host "Press Enter to exit..."
    exit 1
}

Write-Host "Revit is not running. Proceeding with deployment..."

# 3. Copy add-in manifest
if (!(Test-Path $addinFolder)) {
    New-Item -ItemType Directory -Path $addinFolder | Out-Null
}
Copy-Item "d:\Revit\Plugins\GMDC\GMDC.addin" -Destination $addinFolder -Force
Write-Host "Copied GMDC.addin to $addinFolder"

# 4. Copy DLL and dependencies
$targetDir = Join-Path $addinFolder "GMDC"
if (!(Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir | Out-Null
}

# Remove old files if any
Remove-Item "$targetDir\*" -Recurse -Force -ErrorAction SilentlyContinue

# Copy new build files
Copy-Item "$sourceDir\*" -Destination $targetDir -Recurse -Force
Write-Host "Copied build outputs to $targetDir"
Write-Host "---------------------------------------------------------"
Write-Host "Deployment completed successfully! You can now start Revit 2025." -ForegroundColor Green
Write-Host "---------------------------------------------------------"
Read-Host "Press Enter to close..."
