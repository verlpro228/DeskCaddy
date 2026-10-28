using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskCaddy.Core.Library;

/// <summary>
/// state.json 的读写。写入必须原子化：临时文件 → fsync → 替换，
/// 否则中途断电会留下半个 JSON，索引全毁。
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };

    public StateStore(string stateFile)
    {
        FilePath = stateFile;
    }

    public static StateStore Default { get; } = new(LibraryPaths.StateFile);

    public string FilePath { get; }

    public string BackupPath => FilePath + ".bak";

    public string TempPath => FilePath + ".tmp";

    /// <summary>
    /// 读取状态。主文件损坏时退回 .bak；两者都不可用则返回空状态，
    /// 后续由 Reconcile 从收纳库目录重建索引。
    /// </summary>
    public CollectionState Load(out string? warning)
    {
        warning = null;

        if (!File.Exists(FilePath))
        {
            return new CollectionState();
        }

        try
        {
            return ReadFile(FilePath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning = $"state.json 读取失败（{ex.Message}），尝试备份";
        }

        if (File.Exists(BackupPath))
        {
            try
            {
                var backup = ReadFile(BackupPath);
                warning += "；已从 .bak 恢复";
                return backup;
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                warning += $"；备份也失败（{ex.Message}）";
            }
        }

        return new CollectionState();
    }

    /// <summary>
    /// 写入状态。带重试：杀毒软件会在文件刚写完时短暂持有句柄，
    /// 一次失败就放弃会让用户的收纳操作「看起来成功、实际没落盘」。
    /// </summary>
    public void Save(CollectionState state)
    {
        const int maxAttempts = 4;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                SaveOnce(state);
                return;
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(80 * (attempt + 1));
            }
            catch (UnauthorizedAccessException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(80 * (attempt + 1));
            }
        }
    }

    private void SaveOnce(CollectionState state)
    {
        var directory = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        state.Version = CollectionState.CurrentVersion;

        using (var stream = new FileStream(TempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(FilePath))
        {
            // 替换成功时，旧文件会被移动到 .bak
            File.Replace(TempPath, FilePath, BackupPath, ignoreMetadataErrors: true);
        }
        else
        {
            File.Move(TempPath, FilePath);
        }
    }

    private static CollectionState ReadFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);

        var state = JsonSerializer.Deserialize<CollectionState>(stream, JsonOptions);
        if (state is null)
        {
            throw new JsonException("反序列化结果为空");
        }

        return Migrate(state);
    }

    private static CollectionState Migrate(CollectionState state)
    {
        // 目前只有 v1；后续新增字段时在这里逐版升级，不要直接改老字段语义。
        if (state.Version < 1)
        {
            state.Version = 1;
        }

        state.Items ??= [];
        state.Groups ??= [];

        return state;
    }
}
