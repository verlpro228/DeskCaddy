namespace DeskCaddy.Core.Library;

/// <summary>
/// 收纳库中的一个条目。主键是收纳库内的相对文件名，绝不存绝对路径 —— 
/// 用户可能整体迁移收纳库，绝对路径会全部失效。
/// </summary>
public sealed class CollectedItem
{
    public string FileName { get; set; } = string.Empty;

    /// <summary>被收纳前所在的目录，还原时优先回这里，不存在则回桌面。</summary>
    public string OriginDirectory { get; set; } = string.Empty;

    public DateTimeOffset CollectedAt { get; set; }

    public bool Pinned { get; set; }

    public string? Group { get; set; }

    /// <summary>排序键，间隔 1024，插入时取相邻中点。</summary>
    public long Sort { get; set; }
}

public sealed class UiSettings
{
    public double Opacity { get; set; } = 0.8;

    public int IconSize { get; set; } = 48;

    public string? Hotkey { get; set; }
}

public sealed class CollectionState
{
    /// <summary>状态文件结构版本，加载时按此字段做迁移。</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<CollectedItem> Items { get; set; } = [];

    public UiSettings Ui { get; set; } = new();
}
