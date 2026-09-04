using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Win32;
using GMDCManager.Models;

namespace GMDCManager.Services
{
    public class RevitDetectorService
    {
        private static readonly string[] SupportedYears = { "2021", "2022", "2023", "2024", "2025", "2026" };

        [SupportedOSPlatform("windows")]
        public List<RevitVersionInfo> DetectRevitInstallations()
        {
            var versions = new List<RevitVersionInfo>();
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

            foreach (var year in SupportedYears)
            {
                var info = new RevitVersionInfo
                {
                    Year = year,
                    TargetFramework = int.Parse(year) <= 2024 ? "net48" : "net8.0-windows",
                    AddinPath = Path.Combine(appData, "Autodesk", "Revit", "Addins", year, "GMDC.addin"),
                    DllPath = Path.Combine(appData, "Autodesk", "Revit", "Addins", year, "GMDC", "GMDC.dll")
                };

                // Detect Revit installation
                string installPath = GetRevitInstallPathFromRegistry(year);
                if (string.IsNullOrEmpty(installPath))
                {
                    string standardPath = $@"C:\Program Files\Autodesk\Revit {year}";
                    if (Directory.Exists(standardPath))
                    {
                        installPath = standardPath;
                    }
                }

                string addinDir = Path.GetDirectoryName(info.AddinPath) ?? string.Empty;
                bool addinDirExists = Directory.Exists(addinDir);

                if (!string.IsNullOrEmpty(installPath) || addinDirExists)
                {
                    info.IsRevitInstalled = true;
                    info.RevitInstallPath = !string.IsNullOrEmpty(installPath) ? installPath : addinDir;
                }
                else
                {
                    info.IsRevitInstalled = false;
                    info.StatusDescription = "Revit не установлен";
                }

                // Detect Plugin installation
                if (info.IsRevitInstalled)
                {
                    if (File.Exists(info.AddinPath) && File.Exists(info.DllPath))
                    {
                        info.IsPluginInstalled = true;
                        info.InstalledVersion = GetDllVersion(info.DllPath);
                        info.StatusDescription = $"Установлен (v{info.InstalledVersion})";
                    }
                    else
                    {
                        info.IsPluginInstalled = false;
                        info.InstalledVersion = "—";
                        info.StatusDescription = "Не установлен";
                    }
                }

                versions.Add(info);
            }

            return versions;
        }

        [SupportedOSPlatform("windows")]
        private string GetRevitInstallPathFromRegistry(string year)
        {
            try
            {
                string keyPath = $@"SOFTWARE\Autodesk\Revit\{year}";
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using var key = baseKey.OpenSubKey(keyPath);
                
                if (key != null)
                {
                    var installPath = key.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath) && Directory.Exists(installPath))
                    {
                        return installPath;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error checking registry for Revit {year}: {ex.Message}");
            }
            return string.Empty;
        }

        private string GetDllVersion(string dllPath)
        {
            try
            {
                var versionInfo = FileVersionInfo.GetVersionInfo(dllPath);
                string? version = versionInfo.ProductVersion ?? versionInfo.FileVersion;
                if (!string.IsNullOrEmpty(version))
                {
                    int spaceIndex = version.IndexOf(' ');
                    if (spaceIndex > 0) version = version.Substring(0, spaceIndex);
                    int plusIndex = version.IndexOf('+');
                    if (plusIndex > 0) version = version.Substring(0, plusIndex);
                    return version.Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error reading DLL version from {dllPath}: {ex.Message}");
            }
            return "1.0.0";
        }
    }
}
