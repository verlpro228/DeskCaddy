using System.ComponentModel;
using System.Windows.Media.Imaging;

namespace DeskCaddy.Models;

public sealed class LauncherItem : INotifyPropertyChanged
{
    private bool _isSelected;

    public required string Path { get; init; }

    public required string Name { get; init; }

    public BitmapSource? Icon { get; init; }

    public double CellWidth { get; init; } = 118;

    public double CellHeight { get; init; } = 126;

    public double IconSize { get; init; } = 54;

    public double LabelWidth { get; init; } = 106;

    /// <summary>选中描边环的边长：比图标大一圈，只围住图标不盖住内容。</summary>
    public double RingWidth => IconSize + 14;

    /// <summary>
    /// 双击模式下第一次点击的「选中」状态，用于给用户一个明确的反馈：
    /// 第一次点选中，第二次点启动。
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
