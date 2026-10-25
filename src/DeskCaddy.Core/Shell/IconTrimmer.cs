namespace DeskCaddy.Core.Shell;

/// <summary>
/// 裁掉图标四周的不透明白边。
///
/// 部分老程序把图标画在一张纯白方形画布上（如 SpaceSniffer），缩到 Dock 尺寸后
/// 白底就变成一圈方框，和四周透明、圆角的现代图标放在一起很突兀。
///
/// 判定很保守：只有四个角都是不透明近白色、且有效内容占比明显偏小时才裁，
/// 保证像 Typora 那种「本身就是白色圆角卡片」的图标不会被误伤（它的四角是透明的）。
/// </summary>
internal static class IconTrimmer
{
    private const byte OpaqueThreshold = 250;
    private const byte WhiteThreshold = 245;
    private const double MaxPaddingRatio = 0.88;

    public static IconImage Trim(IconImage image)
    {
        var width = image.Width;
        var height = image.Height;
        var pixels = image.Pixels;

        if (width < 8 || height < 8)
        {
            return image;
        }

        if (!IsOpaqueWhite(pixels, width, 0, 0)
            || !IsOpaqueWhite(pixels, width, width - 1, 0)
            || !IsOpaqueWhite(pixels, width, 0, height - 1)
            || !IsOpaqueWhite(pixels, width, width - 1, height - 1))
        {
            return image;
        }

        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (IsOpaqueWhite(pixels, width, x, y))
                {
                    continue;
                }

                if (x < minX)
                {
                    minX = x;
                }

                if (y < minY)
                {
                    minY = y;
                }

                if (x > maxX)
                {
                    maxX = x;
                }

                if (y > maxY)
                {
                    maxY = y;
                }
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return image;
        }

        var contentWidth = maxX - minX + 1;
        var contentHeight = maxY - minY + 1;

        if ((double)contentWidth * contentHeight / (width * height) > MaxPaddingRatio)
        {
            return image;
        }

        // 留 1px 边距，避免裁得太紧贴边。
        minX = Math.Max(0, minX - 1);
        minY = Math.Max(0, minY - 1);
        maxX = Math.Min(width - 1, maxX + 1);
        maxY = Math.Min(height - 1, maxY + 1);

        var croppedWidth = maxX - minX + 1;
        var croppedHeight = maxY - minY + 1;

        if (croppedWidth <= 0 || croppedHeight <= 0)
        {
            return image;
        }

        var cropped = new byte[croppedWidth * croppedHeight * 4];

        for (var y = 0; y < croppedHeight; y++)
        {
            var sourceOffset = (((minY + y) * width) + minX) * 4;
            Buffer.BlockCopy(pixels, sourceOffset, cropped, y * croppedWidth * 4, croppedWidth * 4);
        }

        return new IconImage(croppedWidth, croppedHeight, cropped);
    }

    private static bool IsOpaqueWhite(byte[] pixels, int width, int x, int y)
    {
        var offset = (((y * width) + x) * 4);

        // 预乘 alpha：全不透明时 BGR 才是原值。
        return pixels[offset + 3] >= OpaqueThreshold
            && pixels[offset] >= WhiteThreshold
            && pixels[offset + 1] >= WhiteThreshold
            && pixels[offset + 2] >= WhiteThreshold;
    }
}
