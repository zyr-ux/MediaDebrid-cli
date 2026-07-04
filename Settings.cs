using System.Text.Json;
using DotNetEnv;
using MediaDebrid_cli.Models;
using MediaDebrid_cli.SecretsManager;

namespace MediaDebrid_cli;

/// <summary>
/// Application-wide configuration loaded from config.json or environment variables.
/// </summary>
public static class Settings
{
    private static readonly string AppDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MediaDebrid");
    private static readonly string ConfigFilePath = Path.Combine(AppDataFolder, "config.json");

    public static AppSettings Instance { get; private set; } = new AppSettings();

    // Default path: {User}/Downloads/MediaDebrid
    public static string DefaultBaseRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
        "Downloads", 
        "MediaDebrid"
    );

    // Properties for backward compatibility with the rest of the application
    public static string DebridService => Instance.DebridService;
    public static string RealDebridApiToken => Instance.RealDebridApiToken;
    public static string TorBoxApiToken => Instance.TorBoxApiToken;
    public static string MediaRoot => IsDefault(Instance.MediaRoot) ? DefaultBaseRoot : Instance.MediaRoot;
    public static string GamesRoot => IsDefault(Instance.GamesRoot) ? DefaultBaseRoot : Instance.GamesRoot;
    public static string OthersRoot => IsDefault(Instance.OthersRoot) ? DefaultBaseRoot : Instance.OthersRoot;

    public static bool IsDefault(string path) => string.IsNullOrWhiteSpace(path) || path.Equals("default", StringComparison.OrdinalIgnoreCase);

    public static bool ParallelDownloadEnabled => Instance.ParallelDownloadEnabled;
    public static int ConnectionsPerFile => Instance.ConnectionsPerFile;

    public static void Load()
    {
        // 1. Initialize with defaults or load from JSON if it exists
        if (File.Exists(ConfigFilePath))
        {
            try
            {
                var json = File.ReadAllText(ConfigFilePath);
                Instance = JsonSerializer.Deserialize(json, Serialization.AppSettingsJsonContext.Default.AppSettings) ?? new AppSettings();
            }
            catch { /* Fall back to defaults */ }
        }

        // 2. Always load .env file to ensure environment variables are available
        try { Env.TraversePath().Load(); } catch { }

        // 3. Try to load tokens from secure storage (Primary Source)
        string? secureToken = null;
        try
        {
            secureToken = SecretsManagerFactory.GetStorage().LoadAsync("RealDebridToken").GetAwaiter().GetResult();
        }
        catch { /* Secure storage unavailable or empty */ }

        if (!string.IsNullOrWhiteSpace(secureToken))
        {
            Instance.RealDebridApiToken = secureToken;
        }
        else
        {
            // 4. AUTOMATIC MIGRATION: If vault is empty, check environment variables
            var envToken = Environment.GetEnvironmentVariable("REAL_DEBRID_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(envToken))
            {
                Instance.RealDebridApiToken = envToken;
                
                // Automatically persist the discovered token to the secure vault
                try
                {
                    SecretsManagerFactory.GetStorage().SaveAsync("RealDebridToken", envToken).GetAwaiter().GetResult();
                }
                catch { /* Failed to migrate, but we can still use the env token for this session */ }
            }
        }

        // Load TorBox token
        string? secureTorBoxToken = null;
        try
        {
            secureTorBoxToken = SecretsManagerFactory.GetStorage().LoadAsync("TorBoxToken").GetAwaiter().GetResult();
        }
        catch { /* Secure storage unavailable or empty */ }

        if (!string.IsNullOrWhiteSpace(secureTorBoxToken))
        {
            Instance.TorBoxApiToken = secureTorBoxToken;
        }
        else
        {
            // AUTOMATIC MIGRATION: Check environment variables
            var envTorBoxToken = Environment.GetEnvironmentVariable("TORBOX_API_TOKEN");
            if (!string.IsNullOrWhiteSpace(envTorBoxToken))
            {
                Instance.TorBoxApiToken = envTorBoxToken;
                try
                {
                    SecretsManagerFactory.GetStorage().SaveAsync("TorBoxToken", envTorBoxToken).GetAwaiter().GetResult();
                }
                catch { }
            }
        }

        // 5. Fallback for other settings from environment if they were missing in JSON
        if (string.IsNullOrWhiteSpace(Instance.MediaRoot)) Instance.MediaRoot = Environment.GetEnvironmentVariable("MEDIA_ROOT") ?? "";
        if (string.IsNullOrWhiteSpace(Instance.GamesRoot)) Instance.GamesRoot = Environment.GetEnvironmentVariable("GAMES_ROOT") ?? "";
        if (string.IsNullOrWhiteSpace(Instance.OthersRoot)) Instance.OthersRoot = Environment.GetEnvironmentVariable("OTHERS_ROOT") ?? "";
        
        // Load numeric flags from env if still at defaults (legacy support)
        if (Instance.ConnectionsPerFile == 8 && int.TryParse(Environment.GetEnvironmentVariable("CONNECTIONS_PER_FILE"), out int cpf)) 
            Instance.ConnectionsPerFile = cpf;

        // Ensure config file always exists after first successful load
        if (!File.Exists(ConfigFilePath))
        {
            Save();
        }
    }

    public static void Save()
    {
        if (!Directory.Exists(AppDataFolder))
        {
            Directory.CreateDirectory(AppDataFolder);
        }

        // Save non-sensitive settings to JSON
        var json = JsonSerializer.Serialize(Instance, Serialization.AppSettingsJsonContext.Default.AppSettings);
        File.WriteAllText(ConfigFilePath, json);
        
        // Save sensitive API tokens to secure vault
        if (!string.IsNullOrWhiteSpace(Instance.RealDebridApiToken))
        {
            try
            {
                SecretsManagerFactory.GetStorage().SaveAsync("RealDebridToken", Instance.RealDebridApiToken).GetAwaiter().GetResult();
            }
            catch
            {
                // If vault fails during a manual save, we might want to log it or warn the user,
                // but we shouldn't crash the JSON save.
            }
        }

        if (!string.IsNullOrWhiteSpace(Instance.TorBoxApiToken))
        {
            try
            {
                SecretsManagerFactory.GetStorage().SaveAsync("TorBoxToken", Instance.TorBoxApiToken).GetAwaiter().GetResult();
            }
            catch
            {
            }
        }
    }

    public static string GetRootPathForType(string? type)
    {
        if (string.IsNullOrEmpty(type)) return MediaRoot;
        return type.ToLower() switch
        {
            "game" => GamesRoot,
            "other" => OthersRoot,
            _ => MediaRoot
        };
    }

    public static bool IsConfigured()
    {
        if (Instance.DebridService == "torbox")
        {
            return !string.IsNullOrWhiteSpace(Instance.TorBoxApiToken);
        }
        return !string.IsNullOrWhiteSpace(Instance.RealDebridApiToken);
    }
}
