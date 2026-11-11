using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

/// <summary>批量导出前勾选要导出的已打开图纸。</summary>
public partial class BatchExportWindow : Window
{
    public sealed record Choice(int Number, string Name, string Tip, object Key);

    public IReadOnlyList<Choice> Chosen { get; private set; } = [];

    public BatchExportWindow(IReadOnlyList<Choice> choices, object? preselect = null)
    {
        InitializeComponent();
        ItemsList.ItemsSource = choices;
        if (preselect != null)
        {
            var current = choices.FirstOrDefault(c => ReferenceEquals(c.Key, preselect));
            if (current != null) ItemsList.SelectedItems.Add(current);
        }
        UpdateCount();
    }

    void UpdateCount()
    {
        int count = ItemsList.SelectedItems.Count;
        CountText.Text = $"已选 {count} / {ItemsList.Items.Count} 张";
        ExportSelectedButton.IsEnabled = count > 0;
        ExportSelectedButton.Content = count > 0 ? $"导出所选（{count} 张）" : "导出所选";
    }

    void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCount();

    void SelectAll_Click(object sender, RoutedEventArgs e) => ItemsList.SelectAll();

    void SelectNone_Click(object sender, RoutedEventArgs e) => ItemsList.UnselectAll();

    void ExportSelected_Click(object sender, RoutedEventArgs e)
    {
        // 按列表顺序（与侧栏序号一致）导出。
        var selected = ItemsList.SelectedItems.Cast<Choice>().ToHashSet();
        Chosen = ItemsList.Items.Cast<Choice>().Where(selected.Contains).ToList();
        DialogResult = Chosen.Count > 0;
    }
}
