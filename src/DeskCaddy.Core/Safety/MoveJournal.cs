using System.Text;
using System.Text.Json;

namespace DeskCaddy.Core.Safety;

public sealed record MoveRecord(
    string SourcePath,
    string DestinationPath,
    string Sha256,
    long Size,
    DateTimeOffset Timestamp);

/// <summary>
/// 收纳操作流水账，追加写入 jsonl。
/// 这是「还原到桌面」的唯一依据：即使 state.json 丢了，也能按原始路径精确还原。
/// </summary>
public static class MoveJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    /// <summary>测试隔离用；生产环境保持 null，写入 %LocalAppData%\DeskCaddy\logs。</summary>
    public static string? OverrideDirectory { get; set; }

    public static string DirectoryPath => OverrideDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskCaddy",
        "logs");

    public static string FilePath => Path.Combine(DirectoryPath, $"moves-{DateTime.Now:yyyyMM}.jsonl");

    public static void Append(MoveRecord record)
    {
        System.IO.Directory.CreateDirectory(DirectoryPath);

        var line = JsonSerializer.Serialize(record, JsonOptions);
        using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine(line);
    }

    public static IReadOnlyList<MoveRecord> ReadMonth(DateTime month)
    {
        var path = Path.Combine(DirectoryPath, $"moves-{month:yyyyMM}.jsonl");
        if (!File.Exists(path))
        {
            return [];
        }

        var result = new List<MoveRecord>();

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var record = JsonSerializer.Deserialize<MoveRecord>(line, JsonOptions);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
                // 单行损坏不影响其余记录。
            }
        }

        return result;
    }
}
