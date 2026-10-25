namespace DeskCaddy.Core.Shell;

/// <summary>
/// 从 Shell 取回的图标位图。像素格式为 32bpp 直通 alpha BGRA（Bgra32），
/// 与 WPF 的 PixelFormats.Bgra32 一致，Core 层因此不依赖任何 UI 类型。
/// </summary>
public sealed class IconImage
{
    public IconImage(int width, int height, byte[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public byte[] Pixels { get; }
}
