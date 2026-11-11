using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using ClosedXML.Excel;

namespace CadReviewUI;

// Exercises real WPF editing and the actual batch-export method. Dialog answers
// and destinations are synthetic; all decisions live in a new isolated session.
public static class DesktopIntegrationSelfTest
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task<int> RunAsync(string output, IReadOnlyList<string> reviews)
    {
        if (Directory.Exists(output) || reviews.Count != 3) throw new ArgumentException("Use a new output and exactly three synthetic reviews.");
        Directory.CreateDirectory(output);
        Engine.SessionsRoot = Path.Combine(output, "sessions");
        Directory.CreateDirectory(Engine.SessionsRoot);
        File.WriteAllText(Path.Combine(Engine.SessionsRoot,"ui-layout.json"), "{\"Sidebar\":480,\"Inspector\":680,\"InspectorVisible\":true}");
        var checks = new List<string>();
        var dialogs = new List<string>();
        int confirmations = 0;
        bool confirm = true;
        void Check(string label, bool condition)
        {
            checks.Add((condition ? "PASS " : "FAIL ") + label);
            if (!condition) throw new InvalidOperationException(label);
        }
        MainWindow? window = null;
        try
        {
            window = new MainWindow(false, (text,title,_,_) => { dialogs.Add(title+": "+text); return MessageBoxResult.No; }, null,
                (_,_,_,_) => { confirmations++; return confirm; })
            { Left=-20000, Top=-20000, ShowActivated=false, ShowInTaskbar=false, WindowStartupLocation=WindowStartupLocation.Manual };
            window.OpenExportSelection = _ => { };
            window.OpenBatchExportFolder = _ => { };
            window.Show();
            async Task Idle() => await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ApplicationIdle);
            TaskSession Current() => (TaskSession)typeof(MainWindow).GetField("_task",Private)!.GetValue(window)!;
            string Decision(ItemRow row) => JsonSerializer.Serialize(row.ToDecision(),Json.Options);
            window.OpenTask(reviews[0]); await Idle();
            Check("synthetic fixture only",Current().Data.TaskId.StartsWith("SYNTHETIC-UI-",StringComparison.Ordinal));
            var grid=(DataGrid)window.FindName("ItemsGrid");
            var row=Current().Items.First(r=>r.IsCrossedOut);
            var column=grid.Columns.OfType<DataGridTextColumn>().Single(c=>(c.Binding as Binding)?.Path.Path==nameof(ItemRow.EditName));
            async Task<TextBox> Edit()
            {
                grid.SelectedItem=row; grid.CurrentCell=new DataGridCellInfo(row,column);
                grid.ScrollIntoView(row,column);await Idle();grid.Focus();
                Check("begin real grid edit",grid.BeginEdit());await Idle();
                return column.GetCellContent(row) as TextBox ?? throw new InvalidOperationException("No editing textbox");
            }
            string before=Decision(row);
            await Edit(); grid.CancelEdit(DataGridEditingUnit.Cell);grid.CancelEdit(DataGridEditingUnit.Row);await Idle();
            Check("enter then Escape preserves excluded decision",Decision(row)==before);
            var editor=await Edit();editor.Text="CANCELLED SYNTHETIC NAME";
            grid.CancelEdit(DataGridEditingUnit.Cell);grid.CancelEdit(DataGridEditingUnit.Row);await Idle();
            Check("typed text then Escape preserves all fields",Decision(row)==before);
            await Edit();Check("unchanged commit succeeds",grid.CommitEdit(DataGridEditingUnit.Cell,true));grid.CommitEdit(DataGridEditingUnit.Row,true);await Idle();
            Check("unchanged commit does not include X row",Decision(row)==before);
            editor=await Edit();editor.Text="COMMITTED SYNTHETIC NAME";
            Check("changed commit succeeds",grid.CommitEdit(DataGridEditingUnit.Cell,true));grid.CommitEdit(DataGridEditingUnit.Row,true);await Idle();
            Check("committed change records inclusion",row.Mode==Rules.Revise && row.ConfName=="COMMITTED SYNTHETIC NAME" && row.HasCrossedOutInclusionReason);

            var workspace=(Grid)window.FindName("Workspace");
            var sidebar=(ColumnDefinition)window.FindName("SidebarColumn");
            var inspector=(Border)window.FindName("InspectorPanel");
            Check("restored panes fit default width",sidebar.ActualWidth+inspector.ActualWidth+460<=workspace.ActualWidth+1);
            window.Width=1100;await Idle();
            Check("panes fit minimum window width",sidebar.ActualWidth+inspector.ActualWidth+460<=workspace.ActualWidth+1);
            var toolbar=(Grid)window.FindName("TableToolbar");
            foreach(string name in new[]{"BulkResetButton","RowMoreButton"})
            {
                var button=(Button)window.FindName(name);
                var position=button.TransformToAncestor(toolbar).Transform(new Point(0,0));
                Check(name+" stays reachable at minimum width",position.X>=0 && position.X+button.ActualWidth<=toolbar.ActualWidth+1);
            }
            Check("header actions wrap at minimum width",Grid.GetRow((StackPanel)window.FindName("HeaderActions"))==1);
            await window.RenderToPngAsync(Path.Combine(output,"minimum-window.png"));
            window.Width=1800;await Idle();
            typeof(MainWindow).GetMethod("SidebarSplitter_Reset",Private)!.Invoke(window,[window,null]);
            typeof(MainWindow).GetMethod("InspectorSplitter_Reset",Private)!.Invoke(window,[window,null]);await Idle();
            Check("pane defaults restored",Math.Abs(sidebar.ActualWidth-260)<1&&Math.Abs(inspector.ActualWidth-330)<1);

            // All revisions are explicitly synthetic, including crossed-out rows.
            for(int i=0;i<reviews.Count;i++)
            {
                window.OpenTask(reviews[i]);await Idle();var task=Current();
                Check("each task is synthetic",task.Data.TaskId.StartsWith("SYNTHETIC-UI-",StringComparison.Ordinal));
                window.ApplyMode(task.Items,Rules.Revise);
                foreach(var item in task.Items)
                {
                    item.Note="合成测试，非实际业务核查；测试纳入与导出";
                    item.ConfName="SYNTHETIC ONLY";item.ConfMaterial="TEST";item.ConfTreatment="TEST";
                }
                foreach(var file in task.Files){file.State="已核查";file.Note="合成测试，非实际核查";file.ParentChildNote="合成测试范围";}
            }
            ((TextBox)window.FindName("ReviewerBox")).Text="SYNTHETIC TEST ONLY";
            var destination=Path.Combine(output,"selected-exports");Directory.CreateDirectory(destination);
            window.BatchFolderSelector=_=>destination;
            window.BatchChooser=(choices,_)=>choices.Take(2).ToList();
            await window.BatchExportAsync();
            var files=Directory.GetFiles(destination,"*.xlsx");
            Check("only selected tasks exported",files.Length==2);
            Check("each selected task confirmed",confirmations==2);
            Check("duplicate filenames remain distinct",files.Select(Path.GetFileName).Distinct().Count()==2 && files.Any(f=>Path.GetFileName(f).Contains("(2)",StringComparison.Ordinal)));
            foreach(var file in files)
            {
                using var book=new XLWorkbook(file);
                var sheet=book.Worksheet("报价单样式");
                Check("styled export has synthetic name",sheet.Cell(6,4).GetString()=="SYNTHETIC ONLY");
                Check("styled export keeps blank unit price",sheet.Cell(6,8).IsEmpty());
                Check("styled export keeps amount formula",sheet.Cell(6,9).HasFormula);
            }
            var cancelled=Path.Combine(output,"cancelled-exports");Directory.CreateDirectory(cancelled);
            window.BatchFolderSelector=_=>cancelled;confirm=false;confirmations=0;
            await window.BatchExportAsync();
            Check("cancelled confirmations produce no files",Directory.GetFiles(cancelled,"*.xlsx").Length==0&&confirmations==2);
            grid.SelectedItem=Current().Items[0];await Idle();
            ScrollViewer? FindScroll(DependencyObject parent)
            {
                for(int i=0;i<System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);i++)
                {
                    var child=System.Windows.Media.VisualTreeHelper.GetChild(parent,i);
                    if(child is ScrollViewer scroll) return scroll;
                    if(FindScroll(child) is {} nested) return nested;
                }
                return null;
            }
            FindScroll(grid)?.ScrollToHorizontalOffset(0);await Idle();
            await window.RenderToPngAsync(Path.Combine(output,"review-window.png"));
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {status="passed",checks=checks.Count,exported=files},Json.Options));
            return 0;
        }
        catch(Exception ex){checks.Add("ERROR "+ex);return 1;}
        finally
        {
            window?.Close();
            File.WriteAllLines(Path.Combine(output,"checks.txt"),checks.Concat(dialogs.Select(x=>"DIALOG "+x)),new UTF8Encoding(false));
        }
    }
}
