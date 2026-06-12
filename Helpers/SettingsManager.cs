using System;
using System.IO;
using System.Text.Json;

namespace GMDC.Helpers
{
    public static class SettingsManager
    {
        private static readonly string AppDataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), 
            "GMDC"
        );

        static SettingsManager()
        {
            if (!Directory.Exists(AppDataFolder))
            {
                Directory.CreateDirectory(AppDataFolder);
            }
        }

        public static void SaveConfig<T>(string fileName, T settings)
        {
            try
            {
                string filePath = Path.Combine(AppDataFolder, fileName);
                var options = new JsonSerializerOptions { WriteIndented = true };
                string json = JsonSerializer.Serialize(settings, options);
                File.WriteAllText(filePath, json);
            }
            catch (Exception ex)
            {
                // We'll write to debug/console; in a real Revit context we could log this
                System.Diagnostics.Debug.WriteLine($"Failed to save config: {ex.Message}");
            }
        }

        public static T LoadConfig<T>(string fileName, T defaultSettings)
        {
            try
            {
                string filePath = Path.Combine(AppDataFolder, fileName);
                if (File.Exists(filePath))
                {
                    string json = File.ReadAllText(filePath);
                    T? deserialized = JsonSerializer.Deserialize<T>(json);
                    if (deserialized != null)
                    {
                        return deserialized;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load config: {ex.Message}");
            }
            return defaultSettings;
        }
    }
}
