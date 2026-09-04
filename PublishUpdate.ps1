param (
    [Parameter(Mandatory=$true)]
    [string]$Version,
    
    [Parameter(Mandatory=$true)]
    [string[]]$Changelog,

    [switch]$NoManager
)

$ErrorActionPreference = "Stop"

# 1. Compile solution
Write-Host "--- 1. Compiling solution in Release configuration ---" -ForegroundColor Cyan
dotnet build GMDC.sln -c Release -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    Write-Error "Solution build failed!"
    exit 1
}

# 2. Package plugin into ZIP
Write-Host "--- 2. Packaging plugin into ZIP archive ---" -ForegroundColor Cyan

$candidatePaths = @(
    "bin\x64\Release\net8.0-windows",
    "bin\Release\net8.0-windows"
)

$sourceDir = $null
$latestTime = [DateTime]::MinValue

foreach ($p in $candidatePaths) {
    $dllPath = Join-Path $p "GMDC.dll"
    if (Test-Path $dllPath) {
        $writeTime = (Get-Item $dllPath).LastWriteTime
        if ($writeTime -gt $latestTime) {
            $latestTime = $writeTime
            $sourceDir = $p
        }
    }
}

if (-not $sourceDir) {
    Write-Error "Could not find built GMDC.dll!"
    exit 1
}

$dllVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $sourceDir "GMDC.dll")).ProductVersion
Write-Host "Packaging GMDC.dll (version $dllVer) from $sourceDir" -ForegroundColor Green

$tempNet8 = "$env:TEMP\GMDC_Net8_Build"
if (Test-Path $tempNet8) { Remove-Item $tempNet8 -Recurse -Force }
$null = New-Item -ItemType Directory -Path $tempNet8 -Force

# Copy plugin binaries and addin manifest
Copy-Item "$sourceDir\*" -Destination $tempNet8 -Recurse -Force
Copy-Item "GMDC.addin" -Destination $tempNet8 -Force

# Create ZIP packages
$packagesDir = "UpdateServerMock\packages"
$null = New-Item -ItemType Directory -Path $packagesDir -Force
$zipPath = "$packagesDir\gmdc_net8.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$tempNet8\*" -DestinationPath $zipPath -Force

# Also deploy directly to local Revit 2025 Addins folder
$localAddinFolder = "$env:APPDATA\Autodesk\Revit\Addins\2025"
if (Test-Path $localAddinFolder) {
    $localGmdcDir = Join-Path $localAddinFolder "GMDC"
    if (-not (Test-Path $localGmdcDir)) { New-Item -ItemType Directory -Path $localGmdcDir -Force | Out-Null }
    Copy-Item "$sourceDir\*" -Destination $localGmdcDir -Recurse -Force
    Copy-Item "GMDC.addin" -Destination $localAddinFolder -Force
    Write-Host "Updated local Revit 2025 installation at $localGmdcDir" -ForegroundColor Green
}

Remove-Item $tempNet8 -Recurse -Force

# 3. Publish standalone single-file compressed GMDCManager.exe
if (-not $NoManager) {
    Write-Host "--- 3. Publishing standalone single-file compressed GMDCManager.exe ---" -ForegroundColor Cyan
    dotnet publish GMDCManager\GMDCManager.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Manager publish failed!"
        exit 1
    }

    # Copy single EXE file to release output
    $singleExe = "GMDCManager\bin\Release\net8.0-windows\win-x64\publish\GMDCManager.exe"
    Copy-Item $singleExe -Destination "GMDCManager.exe" -Force

    # Copy to Yandex.Disk
    $yandexFolder = "D:\Yandex.Disk\GMDC\GMDC Plugin"
    if (Test-Path $yandexFolder) {
        Stop-Process -Name "GMDCManager" -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1
        Copy-Item $singleExe -Destination "$yandexFolder\GMDCManager.exe" -Force
        Copy-Item $zipPath -Destination "$yandexFolder\gmdc_net8.zip" -Force
    }
} else {
    Write-Host "--- 3. Skipping GMDCManager compile (-NoManager set) ---" -ForegroundColor Yellow
}

# 4. Update manifest and push to Git
Write-Host "--- 4. Updating manifest and pushing to repository ---" -ForegroundColor Cyan
$manifestPath = "updates/update_manifest.json"
$date = Get-Date -Format "yyyy-MM-dd"
$manifest = [ordered]@{
    latestVersion = $Version
    releaseDate = $date
    changelog = $Changelog
    packages = @{
        net48 = "https://github.com/Nesterro/GMDC/releases/download/v$Version/gmdc_net8.zip"
        net8 = "https://github.com/Nesterro/GMDC/releases/download/v$Version/gmdc_net8.zip"
    }
}
$json = ConvertTo-Json $manifest -Depth 4
[System.IO.File]::WriteAllText((Resolve-Path $manifestPath), $json, [System.Text.Encoding]::UTF8)

# Git commit and push
git add .
git commit -m "Release version v$Version"
git push origin main

# 5. Delete existing release tag if exists and recreate release on GitHub via GitHub CLI
Write-Host "--- 5. Creating Release and uploading assets to GitHub ---" -ForegroundColor Cyan
$notes = "GMDC Plugin version $Version released on $date`n`nChanges:`n" + (($Changelog | ForEach-Object { "- $_" }) -join "`n")

try {
    $prevEA = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    gh release delete "v$Version" -y --cleanup-tag 2>&1 | Out-Null
    $ErrorActionPreference = $prevEA
} catch {}

if ($NoManager) {
    gh release create "v$Version" "UpdateServerMock\packages\gmdc_net8.zip" --title "GMDC Plugin v$Version" --notes $notes
} else {
    gh release create "v$Version" "GMDCManager.exe" "UpdateServerMock\packages\gmdc_net8.zip" --title "GMDC Plugin v$Version" --notes $notes
}

Write-Host ""
Write-Host "==============================================" -ForegroundColor Green
Write-Host " Version v$Version successfully published!" -ForegroundColor Green
Write-Host "==============================================" -ForegroundColor Green
