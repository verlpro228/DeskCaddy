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

    public const double MinIslandWidth = 260;
    public const double MaxIslandWidth = 560;
    public const double DefaultIslandWidth = 380;

    /// <summary>玻璃底色强度：以 100% 为当前默认观感，低于 100 更透，高于 100 更实。</summary>
    public const double MinGlassStrength = 20;
    public const double MaxGlassStrength = 140;

    /// <summary>毛玻璃模糊强度：100% 为默认半径，0 表示不模糊。</summary>
    public const double MinBlurStrength = 0;
    public const double MaxBlurStrength = 200;

    public const double MinCornerRadius = 0;
    public const double MaxCornerRadius = 40;

    public const double MinDockIconGap = 2;
    public const double MaxDockIconGap = 24;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>玻璃底色强度（%）。Dock、菜单栏、灵动岛共用，100 为默认观感。</summary>
    public double GlassStrength { get; set; } = 100;

    /// <summary>毛玻璃模糊强度（%）。只影响截屏模糊半径，不改变透明度。</summary>
    public double BlurStrength { get; set; } = 100;

    /// <summary>菜单栏与灵动岛的圆角半径（DIP）。Dock 始终是胶囊形。</summary>
    public double CornerRadius { get; set; } = 26;

    /// <summary>Dock 图标之间的间距（DIP）。</summary>
    public double DockIconGap { get; set; } = 8;

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

    /// <summary>顶部音乐灵动岛。默认关闭，功能还在打磨阶段。</summary>
    public bool IslandEnabled { get; set; }

    /// <summary>灵动岛展开后的宽度（DIP），收起时固定为 220。</summary>
    public double IslandWidth { get; set; } = DefaultIslandWidth;

    public AppSettings Clamp()
    {
        IconSize = Math.Clamp(IconSize, MinIconSize, MaxIconSize);
        HoverScale = Math.Clamp(HoverScale, MinHoverScale, MaxHoverScale);
        LauncherColumns = Math.Clamp(LauncherColumns, MinLauncherColumns, MaxLauncherColumns);
        DockMaxItems = Math.Clamp(DockMaxItems, MinDockItems, MaxDockItemsLimit);
        LauncherIconSize = Math.Clamp(LauncherIconSize, MinLauncherIconSize, MaxLauncherIconSize);
        IslandWidth = Math.Clamp(IslandWidth, MinIslandWidth, MaxIslandWidth);
        GlassStrength = Math.Clamp(GlassStrength, MinGlassStrength, MaxGlassStrength);
        BlurStrength = Math.Clamp(BlurStrength, MinBlurStrength, MaxBlurStrength);
        CornerRadius = Math.Clamp(CornerRadius, MinCornerRadius, MaxCornerRadius);
        DockIconGap = Math.Clamp(DockIconGap, MinDockIconGap, MaxDockIconGap);

        return this;
    }
}
