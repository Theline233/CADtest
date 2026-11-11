using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

/// <summary>简单的多行文字输入框（复核说明、排除原因）。取消返回 null。</summary>
public static class Prompt
{
    public static string? Ask(Window owner, string title, string message, bool required = true, string initial = "")
    {
        var box = new TextBox
        {
            Text = initial, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 96,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 12), Padding = new Thickness(4),
        };
        var ok = new Button { Content = "确定", Width = 84, Padding = new Thickness(0, 5, 0, 5), IsDefault = false, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = "取消", Width = 84, Padding = new Thickness(0, 5, 0, 5), IsCancel = true };
        var window = new Window
        {
            Title = title, Owner = owner, Width = 540, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, FontFamily = owner.FontFamily, FontSize = 13,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        window.Content = panel;

        void Refresh() => ok.IsEnabled = !required || box.Text.Trim().Length > 0;
        box.TextChanged += (_, _) => Refresh();
        Refresh();
        string? result = null;
        ok.Click += (_, _) => { result = box.Text.Trim(); window.DialogResult = true; };
        window.Loaded += (_, _) => { box.Focus(); box.CaretIndex = box.Text.Length; };
        return window.ShowDialog() == true ? result : null;
    }
}
