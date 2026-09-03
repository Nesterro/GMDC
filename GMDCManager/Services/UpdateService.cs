using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using GMDCManager.Models;

namespace GMDCManager.Services
{
    public class UpdateService
    {
        private readonly HttpClient _httpClient;

        public UpdateService()
        {
            _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("GMDCManager/1.0 (Windows; Revit Plugin Update Manager)");
        }

        public class UpdateManifest
        {
            [JsonPropertyName("latestVersion")]
            public string LatestVersion { get; set; } = string.Empty;

            [JsonPropertyName("releaseDate")]
            public string ReleaseDate { get; set; } = string.Empty;

            [JsonPropertyName("changelog")]
            public string[] Changelog { get; set; } = Array.Empty<string>();

            [JsonPropertyName("packages")]
            public PackageInfo Packages { get; set; } = new();
        }

        public class PackageInfo
        {
            [JsonPropertyName("net48")]
            public string Net48Url { get; set; } = string.Empty;

            [JsonPropertyName("net8")]
            public string Net8Url { get; set; } = string.Empty;
        }

        public async Task<UpdateManifest?> FetchManifestAsync(string sourcePath)
        {
            try
            {
                string jsonContent;
                if (sourcePath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    sourcePath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    string url = sourcePath;
                    url += (url.Contains("?") ? "&" : "?") + $"_nocache={DateTime.UtcNow.Ticks}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue
                    {
                        NoCache = true,
                        NoStore = true,
                        MustRevalidate = true
                    };
                    using var response = await _httpClient.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    jsonContent = await response.Content.ReadAsStringAsync();
                }
                else
                {
                    string manifestPath = Path.Combine(sourcePath, "update_manifest.json");
                    if (!File.Exists(manifestPath)) return null;
                    jsonContent = await File.ReadAllTextAsync(manifestPath);
                }

                return JsonSerializer.Deserialize<UpdateManifest>(jsonContent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error fetching update manifest: {ex.Message}");
                return null;
            }
        }

        public bool IsRevitRunning()
        {
            var processes = Process.GetProcessesByName("Revit");
            return processes.Length > 0;
        }

        public void TerminateRevit()
        {
            var processes = Process.GetProcessesByName("Revit");
            foreach (var process in processes)
            {
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to terminate Revit process: {ex.Message}");
                }
            }
        }

        public async Task InstallUpdateAsync(
            RevitVersionInfo versionInfo, 
            UpdateManifest manifest, 
            string sourcePath, 
            IProgress<double> progress, 
            CancellationToken cancellationToken)
        {
            string packageUrlOrPath = versionInfo.TargetFramework == "net48" 
                ? (string.IsNullOrEmpty(manifest.Packages.Net48Url) ? manifest.Packages.Net8Url : manifest.Packages.Net48Url)
                : manifest.Packages.Net8Url;

            if (string.IsNullOrEmpty(packageUrlOrPath))
            {
                throw new Exception($"Не указан путь к пакету обновления для {versionInfo.TargetFramework}");
            }

            string tempZipPath = Path.Combine(Path.GetTempPath(), $"GMDC_{versionInfo.Year}_{Guid.NewGuid()}.zip");
            string tempExtractPath = Path.Combine(Path.GetTempPath(), $"GMDC_Extract_{versionInfo.Year}_{Guid.NewGuid()}");

            try
            {
                progress.Report(10);

                if (packageUrlOrPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || 
                    packageUrlOrPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    await DownloadFileAsync(packageUrlOrPath, tempZipPath, progress, cancellationToken);
                }
                else
                {
                    string localZipPath = Path.IsPathRooted(packageUrlOrPath)
                        ? packageUrlOrPath
                        : Path.Combine(sourcePath, packageUrlOrPath);

                    if (!File.Exists(localZipPath))
                    {
                        throw new FileNotFoundException($"Пакет обновления не найден по пути: {localZipPath}");
                    }

                    progress.Report(40);
                    File.Copy(localZipPath, tempZipPath, true);
                    progress.Report(50);
                }

                progress.Report(60);
                if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
                Directory.CreateDirectory(tempExtractPath);
                ZipFile.ExtractToDirectory(tempZipPath, tempExtractPath);

                progress.Report(70);

                string addinFolder = Path.GetDirectoryName(versionInfo.AddinPath) ?? 
                    throw new Exception("Невозможно определить путь установки.");

                if (!Directory.Exists(addinFolder)) Directory.CreateDirectory(addinFolder);

                string targetPluginDir = Path.Combine(addinFolder, "GMDC");
                if (!Directory.Exists(targetPluginDir)) Directory.CreateDirectory(targetPluginDir);

                // Copy files
                var files = Directory.GetFiles(tempExtractPath, "*.*", SearchOption.AllDirectories);
                int fileCount = files.Length;
                for (int i = 0; i < fileCount; i++)
                {
                    string file = files[i];
                    string fileName = Path.GetFileName(file);

                    // If file is GMDC.addin, put it into addinFolder
                    if (fileName.Equals("GMDC.addin", StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(file, versionInfo.AddinPath, true);
                    }
                    else
                    {
                        // Other files go into targetPluginDir (GMDC)
                        string relativePath = Path.GetRelativePath(tempExtractPath, file);
                        string destFile = Path.Combine(targetPluginDir, relativePath);
                        string? destFolder = Path.GetDirectoryName(destFile);
                        if (destFolder != null && !Directory.Exists(destFolder))
                        {
                            Directory.CreateDirectory(destFolder);
                        }
                        File.Copy(file, destFile, true);
                    }

                    double copyProgress = 70.0 + (25.0 * (i + 1) / fileCount);
                    progress.Report(copyProgress);
                }

                // Ensure Addin Manifest exists
                WriteAddinManifest(versionInfo.AddinPath);
                progress.Report(100);
            }
            finally
            {
                try
                {
                    if (File.Exists(tempZipPath)) File.Delete(tempZipPath);
                    if (Directory.Exists(tempExtractPath)) Directory.Delete(tempExtractPath, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to clean up temp files: {ex.Message}");
                }
            }
        }

        public void UninstallPlugin(RevitVersionInfo versionInfo)
        {
            string addinFolder = Path.GetDirectoryName(versionInfo.AddinPath) ?? 
                throw new Exception("Невозможно определить путь установки.");

            if (!Directory.Exists(addinFolder)) return;

            DeleteFileIfExists(versionInfo.AddinPath);

            string targetPluginDir = Path.Combine(addinFolder, "GMDC");
            if (Directory.Exists(targetPluginDir))
            {
                try
                {
                    Directory.Delete(targetPluginDir, true);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to delete plugin folder {targetPluginDir}: {ex.Message}");
                }
            }
        }

        private void DeleteFileIfExists(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to delete file {path}: {ex.Message}");
            }
        }

        private async Task DownloadFileAsync(string url, string destinationPath, IProgress<double> progress, CancellationToken cancellationToken)
        {
            using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[8192];
            var totalReadBytes = 0L;
            var bytesRead = 0;

            while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
            {
                await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                totalReadBytes += bytesRead;

                if (totalBytes != -1)
                {
                    double downloadProgress = 10.0 + (30.0 * totalReadBytes / totalBytes);
                    progress.Report(downloadProgress);
                }
            }
        }

        private void WriteAddinManifest(string path)
        {
            string xmlContent = @"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Name>GMDC Plugin</Name>
    <Assembly>GMDC\GMDC.dll</Assembly>
    <ClientId>B84F0BD8-B4C1-4D56-A833-F9632822B7E1</ClientId>
    <FullClassName>GMDC.App</FullClassName>
    <VendorId>GMDC</VendorId>
    <VendorDescription>GMDC Revit Plugin</VendorDescription>
  </AddIn>
</RevitAddIns>";

            string? dir = Path.GetDirectoryName(path);
            if (dir != null && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, xmlContent, System.Text.Encoding.UTF8);
        }
    }
}
