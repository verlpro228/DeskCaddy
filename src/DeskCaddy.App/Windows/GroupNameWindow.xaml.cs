using System.Windows;
using System.Windows.Input;

namespace DeskCaddy.Windows;

/// <summary>新建分组的小输入框。确定后通过 GroupName 返回。</summary>
public partial class GroupNameWindow : Window
{
    public GroupNameWindow(string caption = "新建分组", string initialName = "")
    {
        InitializeComponent();

        Title = caption;
        CaptionText.Text = caption;

        if (initialName.Length > 0)
        {
            NameBox.Text = initialName;
        }

        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    public string GroupName { get; private set; } = string.Empty;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();

        if (name.Length == 0)
        {
            NameBox.Focus();
            return;
        }

        GroupName = name;
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnOk(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancel(sender, e);
        }
    }
}
