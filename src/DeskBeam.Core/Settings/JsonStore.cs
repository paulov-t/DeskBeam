using System.Text.Json;

namespace DeskBeam.Core.Settings;

public static class AppPaths
{
    public static string DataDirectory
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeskBeam");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string HostSettingsFile => Path.Combine(DataDirectory, "host.json");
    public static string ClientSettingsFile => Path.Combine(DataDirectory, "client.json");
    public static string LogFile => Path.Combine(DataDirectory, "host.log");
}

public static class JsonStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static T Load<T>(string path, Func<T> fallback) where T : class
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var value = JsonSerializer.Deserialize<T>(json, Options);
                if (value is not null)
                    return value;
            }
        }
        catch (Exception)
        {
            // Corrupt or inaccessible settings should never block startup.
        }

        return fallback();
    }

    public static void Save<T>(string path, T value)
    {
        try
        {
            var json = JsonSerializer.Serialize(value, Options);
            File.WriteAllText(path, json);
        }
        catch (Exception)
        {
            // Best effort.
        }
    }
}
