namespace DeskCaddy.Core.Settings;

/// <summary>
/// 用户可调的外观参数。与收纳索引分开存放，避免改一个图标大小就重写整个索引。
/// </summary>
public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public const double MinIconSize = 32;
    public const double MaxIconSize = 64;

    public const double MinHoverScale = 1.0;
    public const double MaxHoverScale = 1.30;

    public const int MinLauncherColumns = 4;

    /// <summary>菜单栏每行最多 10 个，固定值。</summary>
    public const int MaxLauncherColumns = 10;

    public const int MinDockItems = 3;
    public const int MaxDockItemsLimit = 16;

    public const double MinLauncherIconSize = 36;
    public const double MaxLauncherIconSize = 80;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>Dock 图标边长（DIP）。</summary>
    public double IconSize { get; set; } = 46;

    /// <summary>悬停放大倍数。</summary>
    public double HoverScale { get; set; } = 1.08;

    /// <summary>启动台每行列数。</summary>
    public int LauncherColumns { get; set; } = 7;

    /// <summary>Dock 上最多显示多少个图标（不含「更多」入口）。</summary>
    public int DockMaxItems { get; set; } = 10;

    /// <summary>启动台图标边长，越小一行能放越多。</summary>
    public double LauncherIconSize { get; set; } = 54;

    /// <summary>
    /// 菜单栏是否双击才打开应用。默认开启 —— 单击误触很常见，
    /// 桌面 Dock 是单击，菜单栏图标更密集，双击更稳。
    /// </summary>
    public bool LauncherOpenOnDoubleClick { get; set; } = true;

    public AppSettings Clamp()
    {
        IconSize = Math.Clamp(IconSize, MinIconSize, MaxIconSize);
        HoverScale = Math.Clamp(HoverScale, MinHoverScale, MaxHoverScale);
        LauncherColumns = Math.Clamp(LauncherColumns, MinLauncherColumns, MaxLauncherColumns);
        DockMaxItems = Math.Clamp(DockMaxItems, MinDockItems, MaxDockItemsLimit);
        LauncherIconSize = Math.Clamp(LauncherIconSize, MinLauncherIconSize, MaxLauncherIconSize);

        return this;
    }
}
