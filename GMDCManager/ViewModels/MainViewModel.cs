using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using GMDCManager.Models;
using GMDCManager.Services;

namespace GMDCManager.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged
    {
        private readonly RevitDetectorService _detectorService;
        private readonly UpdateService _updateService;
        
        private bool _isLoading;
        private double _globalProgress;
        private string _progressText = string.Empty;
        private string _updateSource = string.Empty;
        private bool _autoCloseRevit = true;
        private string _currentTab = "plugins"; // plugins, settings, about
        private string _changelog = "Загрузка списка изменений...";
        private string _latestVersion = "—";
        private bool _showRevitWarning;
        private RevitVersionInfo? _pendingInstallVersion;

        public ObservableCollection<RevitVersionInfo> RevitVersions { get; } = new();

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public double GlobalProgress
        {
            get => _globalProgress;
            set { _globalProgress = value; OnPropertyChanged(); }
        }

        public string ProgressText
        {
            get => _progressText;
            set { _progressText = value; OnPropertyChanged(); }
        }

        public string UpdateSource
        {
            get => _updateSource;
            set { _updateSource = value; OnPropertyChanged(); OnPropertyChanged(nameof(GitHubRepo)); }
        }

        public string GitHubRepo
        {
            get => UpdateSource;
            set { UpdateSource = value; OnPropertyChanged(); }
        }

        public bool AutoCloseRevit
        {
            get => _autoCloseRevit;
            set { _autoCloseRevit = value; OnPropertyChanged(); }
        }

        public bool AutoCheckUpdates { get; set; } = true;
        public bool IncludePreReleases { get; set; } = false;

        public string CurrentTab
        {
            get => _currentTab;
            set 
            { 
                _currentTab = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(IsPluginsTabActive)); 
                OnPropertyChanged(nameof(IsSettingsTabActive)); 
                OnPropertyChanged(nameof(IsAboutTabActive)); 
            }
        }

        public bool IsPluginsTabActive => CurrentTab == "plugins";
        public bool IsSettingsTabActive => CurrentTab == "settings";
        public bool IsAboutTabActive => CurrentTab == "about";

        public string Changelog
        {
            get => _changelog;
            set 
            { 
                _changelog = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(LatestReleaseBody));
            }
        }

        public string LatestVersion
        {
            get => _latestVersion;
            set 
            { 
                _latestVersion = value; 
                OnPropertyChanged(); 
                OnPropertyChanged(nameof(LatestReleaseVersion));
                OnPropertyChanged(nameof(LatestReleaseName));
            }
        }

        public string LatestReleaseVersion => LatestVersion;
        public string LatestReleaseName => string.IsNullOrEmpty(LatestVersion) || LatestVersion == "—" ? "Список изменений" : $"Версия {LatestVersion}";
        public string LatestReleaseBody => Changelog;

        public bool ShowRevitWarning
        {
            get => _showRevitWarning;
            set { _showRevitWarning = value; OnPropertyChanged(); }
        }

        public ICommand NavigateCommand { get; }
        public ICommand CheckUpdatesCommand { get; }
        public ICommand CheckForUpdatesCommand => CheckUpdatesCommand;
        public ICommand InstallCommand { get; }
        public ICommand UninstallCommand { get; }
        public ICommand SaveSettingsCommand { get; }
        public ICommand CloseWarningCommand { get; }
        public ICommand ForceInstallCommand { get; }

        public MainViewModel()
        {
            _detectorService = new RevitDetectorService();
            _updateService = new UpdateService();

            NavigateCommand = new RelayCommand<string>(tab => CurrentTab = tab ?? "plugins");
            CheckUpdatesCommand = new RelayCommand(async () => await CheckUpdatesAsync());
            InstallCommand = new RelayCommand<RevitVersionInfo>(async ver => await StartInstallAsync(ver));
            UninstallCommand = new RelayCommand<RevitVersionInfo>(ver => Uninstall(ver));
            SaveSettingsCommand = new RelayCommand(SaveSettings);
            CloseWarningCommand = new RelayCommand(() => ShowRevitWarning = false);
            ForceInstallCommand = new RelayCommand(async () => await ForceInstallAsync());

            LoadSettings();
            
            if (OperatingSystem.IsWindows())
            {
                RefreshLocalVersions();
                _ = CheckUpdatesAsync();
            }
        }

        [SupportedOSPlatform("windows")]
        private void RefreshLocalVersions()
        {
            var versions = _detectorService.DetectRevitInstallations();
            RevitVersions.Clear();
            foreach (var ver in versions)
            {
                RevitVersions.Add(ver);
            }
            EvaluateUpdates();
        }

        private async Task CheckUpdatesAsync()
        {
            IsLoading = true;
            ProgressText = "Проверка обновлений...";
            GlobalProgress = 10;

            try
            {
                var manifest = await _updateService.FetchManifestAsync(UpdateSource);
                GlobalProgress = 70;
                
                if (manifest != null)
                {
                    LatestVersion = manifest.LatestVersion;
                    Changelog = $"Версия: {manifest.LatestVersion} ({manifest.ReleaseDate})\n\nИзменения:\n" + 
                                string.Join("\n", manifest.Changelog);
                    
                    if (OperatingSystem.IsWindows())
                    {
                        RefreshLocalVersions();
                    }
                }
                else
                {
                    LatestVersion = "Неизвестно";
                    Changelog = "Ошибка проверки обновлений. Проверьте путь к источнику обновлений в настройках.";
                }
            }
            catch (Exception ex)
            {
                Changelog = $"Ошибка: {ex.Message}";
            }
            finally
            {
                GlobalProgress = 100;
                await Task.Delay(300);
                IsLoading = false;
                GlobalProgress = 0;
            }
        }

        private void EvaluateUpdates()
        {
            if (string.IsNullOrEmpty(LatestVersion) || LatestVersion == "—" || LatestVersion == "Неизвестно")
                return;

            try
            {
                var latestVer = new Version(LatestVersion);
                foreach (var ver in RevitVersions)
                {
                    if (!ver.IsRevitInstalled) continue;

                    if (ver.IsPluginInstalled)
                    {
                        var installedVer = new Version(ver.InstalledVersion);
                        if (latestVer > installedVer)
                        {
                            ver.IsUpdateAvailable = true;
                            ver.AvailableVersion = LatestVersion;
                            ver.StatusDescription = $"Доступно обновление до v{LatestVersion}";
                        }
                        else
                        {
                            ver.IsUpdateAvailable = false;
                            ver.AvailableVersion = "—";
                            ver.StatusDescription = $"Установлен (v{ver.InstalledVersion})";
                        }
                    }
                    else
                    {
                        ver.IsUpdateAvailable = false;
                        ver.AvailableVersion = LatestVersion;
                        ver.StatusDescription = "Не установлен";
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error evaluating updates: {ex.Message}");
            }
        }

        private async Task StartInstallAsync(RevitVersionInfo? version)
        {
            if (version == null) return;

            if (_updateService.IsRevitRunning())
            {
                if (AutoCloseRevit)
                {
                    _updateService.TerminateRevit();
                }
                else
                {
                    _pendingInstallVersion = version;
                    ShowRevitWarning = true;
                    return;
                }
            }

            await ProceedInstallAsync(version);
        }

        private async Task ForceInstallAsync()
        {
            ShowRevitWarning = false;
            if (_pendingInstallVersion == null) return;

            IsLoading = true;
            ProgressText = "Закрытие Revit...";
            await Task.Run(() => _updateService.TerminateRevit());
            await ProceedInstallAsync(_pendingInstallVersion);
            _pendingInstallVersion = null;
        }

        private async Task ProceedInstallAsync(RevitVersionInfo version)
        {
            IsLoading = true;
            ProgressText = $"Установка GMDC для Revit {version.Year}...";
            GlobalProgress = 0;

            var progress = new Progress<double>(val =>
            {
                GlobalProgress = val;
                ProgressText = $"Установка GMDC ({val:F0}%)...";
            });

            try
            {
                var manifest = await _updateService.FetchManifestAsync(UpdateSource);
                if (manifest == null)
                {
                    throw new Exception("Не удалось загрузить манифест обновления.");
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                await _updateService.InstallUpdateAsync(version, manifest, UpdateSource, progress, cts.Token);

                MessageBox.Show($"Плагин GMDC успешно установлен для Revit {version.Year}!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);

                if (OperatingSystem.IsWindows())
                {
                    RefreshLocalVersions();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка установки: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
                GlobalProgress = 0;
            }
        }

        [SupportedOSPlatform("windows")]
        private void Uninstall(RevitVersionInfo? version)
        {
            if (version == null) return;

            var result = MessageBox.Show(
                $"Вы действительно хотите удалить плагин GMDC для Revit {version.Year}?", 
                "Подтверждение удаления", 
                MessageBoxButton.YesNo, 
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes) return;

            try
            {
                _updateService.UninstallPlugin(version);
                MessageBox.Show($"Плагин GMDC успешно удален для Revit {version.Year}.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshLocalVersions();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка при удалении: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private class UserSettings
        {
            public string UpdateSource { get; set; } = string.Empty;
            public bool AutoCloseRevit { get; set; } = true;
        }

        private void LoadSettings()
        {
            string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMDCManager");
            string configPath = Path.Combine(configDir, "config.json");

            string defaultManifestUrl = "https://raw.githubusercontent.com/Nesterro/GMDC/main/updates/update_manifest.json";

            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    var settings = JsonSerializer.Deserialize<UserSettings>(json);
                    if (settings != null)
                    {
                        UpdateSource = string.IsNullOrEmpty(settings.UpdateSource) ? defaultManifestUrl : settings.UpdateSource;
                        AutoCloseRevit = settings.AutoCloseRevit;
                        return;
                    }
                }
                catch { }
            }

            UpdateSource = defaultManifestUrl;
            AutoCloseRevit = true;
        }

        private void SaveSettings()
        {
            try
            {
                string configDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GMDCManager");
                if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);
                string configPath = Path.Combine(configDir, "config.json");

                var settings = new UserSettings
                {
                    UpdateSource = UpdateSource,
                    AutoCloseRevit = AutoCloseRevit
                };

                string json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(configPath, json);
                MessageBox.Show("Настройки сохранены!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);

                if (OperatingSystem.IsWindows())
                {
                    RefreshLocalVersions();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка сохранения настроек: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;
        public void Execute(object? parameter) => _execute();

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }

    public class RelayCommand<T> : ICommand
    {
        private readonly Action<T?> _execute;
        private readonly Func<T?, bool>? _canExecute;

        public RelayCommand(Action<T?> execute, Func<T?, bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter)
        {
            if (parameter == null && typeof(T).IsValueType) return _canExecute?.Invoke(default) ?? true;
            return _canExecute?.Invoke((T?)parameter) ?? true;
        }

        public void Execute(object? parameter) => _execute((T?)parameter);

        public event EventHandler? CanExecuteChanged
        {
            add => CommandManager.RequerySuggested += value;
            remove => CommandManager.RequerySuggested -= value;
        }
    }
}
