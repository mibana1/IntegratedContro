using System.IO;
using System.Text.Json;
using IntegratedContro.Core;

namespace IntegratedContro.App;

/// <summary>Local appearance is independent of connection settings and their recovery/backups.</summary>
public sealed record AppearancePreferences(bool IsDark = false)
{
    public static string SettingsPath => Path.Combine(Path.GetDirectoryName(ClientPreferences.ProfilePath)!, "appearance.json");
    public static AppearancePreferences Load(string? path = null)
    {
        path ??= SettingsPath;
        try
        {
            using var access = ClientProfileEnvironment.AcquireProfile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length > 4096) return new();
            return JsonSerializer.Deserialize<AppearancePreferences>(stream, JsonDefaults.Options) ?? new();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return new(); }
    }

    public void Save(string? path = null)
    {
        path = Path.GetFullPath(path ?? SettingsPath);
        using var access = ClientProfileEnvironment.AcquireProfile(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(this, JsonDefaults.Options);
                stream.Write(bytes); stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}
