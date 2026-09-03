using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace GMDCManager.Models
{
    public class RevitVersionInfo : INotifyPropertyChanged
    {
        private string _installedVersion = "—";
        private string _availableVersion = "—";
        private bool _isRevitInstalled;
        private bool _isPluginInstalled;
        private bool _isUpdateAvailable;
        private string _statusDescription = "Не обнаружен";

        public string Year { get; set; } = string.Empty;
        public string DisplayName => $"Autodesk Revit {Year}";
        public string TargetFramework { get; set; } = "net8.0-windows"; // net8.0-windows or net48
        public string RevitInstallPath { get; set; } = string.Empty;
        public string AddinPath { get; set; } = string.Empty;
        public string DllPath { get; set; } = string.Empty;

        public bool IsRevitInstalled
        {
            get => _isRevitInstalled;
            set { _isRevitInstalled = value; OnPropertyChanged(); }
        }

        public bool IsPluginInstalled
        {
            get => _isPluginInstalled;
            set { _isPluginInstalled = value; OnPropertyChanged(); }
        }

        public string InstalledVersion
        {
            get => _installedVersion;
            set { _installedVersion = value; OnPropertyChanged(); }
        }

        public string AvailableVersion
        {
            get => _availableVersion;
            set { _availableVersion = value; OnPropertyChanged(); }
        }

        public bool IsUpdateAvailable
        {
            get => _isUpdateAvailable;
            set { _isUpdateAvailable = value; OnPropertyChanged(); }
        }

        public string StatusDescription
        {
            get => _statusDescription;
            set { _statusDescription = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
