using System.Text.Json;

namespace DeskCaddy.Core.Settings;

/// <summary>
/// settings.json 的读写，原子写入 + 备份，模式与收纳索引一致。
/// 读失败时回落到默认值，绝不让配置问题阻塞程序启动。
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public SettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public static SettingsStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskCaddy",
        "settings.json"));

    public string FilePath { get; }

    public AppSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read);

            var settings = JsonSerializer.Deserialize<AppSettings>(stream, JsonOptions);

            return (settings ?? new AppSettings()).Clamp();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = FilePath + ".tmp";

        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, settings.Clamp(), JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(FilePath))
        {
            File.Replace(temp, FilePath, FilePath + ".bak", ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(temp, FilePath);
        }
    }
}
