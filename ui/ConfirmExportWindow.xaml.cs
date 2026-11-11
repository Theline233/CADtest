using System;
using System.Windows;

namespace CadReviewUI;

public partial class ConfirmExportWindow : Window
{
    public ConfirmExportWindow(int itemCount, int fileCount, int excludedCount, string scopeSummary)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfNegative(fileCount);
        ArgumentOutOfRangeException.ThrowIfNegative(excludedCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeSummary);

        InitializeComponent();
        CountText.Text = $"本次纳入明细：{itemCount:N0} 条    纳入图纸：{fileCount:N0} 份";
        ExcludedCountText.Text = $"已排除明细：{excludedCount:N0} 条（不会导出）";
        ScopeSummaryText.Text = scopeSummary;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ReturnButton.Focus();
    }

    private void ConfirmExport_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }
}
