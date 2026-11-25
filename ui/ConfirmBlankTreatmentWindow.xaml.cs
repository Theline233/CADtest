using System.Windows;

namespace CadReviewUI;

public partial class ConfirmBlankTreatmentWindow : Window
{
    public ConfirmBlankTreatmentWindow(IReadOnlyList<string> rows)
    {
        InitializeComponent();
        Heading.Text = $"{rows.Count:N0} 条表面处理未确定";
        AffectedRows.ItemsSource = rows;
    }

    void Window_Loaded(object sender, RoutedEventArgs e) => ReturnButton.Focus();
    void Continue_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
