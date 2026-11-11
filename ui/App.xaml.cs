using System.Windows;

namespace CadReviewUI;

public partial class App : Application
{
    // Demo sessions are always fresh, including when --sessions is supplied.
    internal static (string? Open, string? Screenshot, bool Demo) ConfigureWindowOptions(string[] args)
    {
        string? open = null, shot = null, sessions = null;
        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--open") open = args[i + 1];
            if (args[i] == "--screenshot") shot = args[i + 1];
            if (args[i] == "--sessions") sessions = args[i + 1];
        }
        bool demo = args.Contains("--demo");
        if (demo)
            Engine.SessionsRoot = Path.Combine(Engine.OutputsRoot, "ui-demo-sessions", Guid.NewGuid().ToString("N"));
        else if (sessions != null)
            Engine.SessionsRoot = Path.GetFullPath(sessions);
        return (open, shot, demo);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = e.Args;
        try { _ = Engine.Root; }
        catch (Exception ex)
        {
            MessageBox.Show(ex.InnerException?.Message ?? ex.Message, "工作区配置错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }

        if (args.Length == 5 && args[0] == "--frontend-integration-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await DesktopIntegrationSelfTest.RunAsync(args[1], args.Skip(2).ToList()); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }

        if (args.Length >= 2 && args[0] == "--api-selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await ApiServiceSelfTest.RunAsync(args[1]); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }

        if (args.Length >= 3 && args[0] == "--ui-validation-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await UiValidationGuardTest.RunAsync(args[1], args[2]); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }

        if (args.Length >= 3 && args[0] == "--interaction-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await InteractionSelfTest.RunAsync(args[1], args.Skip(2).ToList()); }
            catch { code = 2; }
            Shutdown(code);
            return;
        }

        // 无界面自测：CadReviewUI.exe --selftest <review目录> <新的输出目录>
        if (args.Length >= 3 && args[0] == "--selftest")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            int code;
            try { code = await SelfTest.RunAsync(args[1], args[2]); }
            catch (Exception ex) { File.WriteAllText(Path.Combine(args[2], "selftest-crash.txt"), ex.ToString()); code = 2; }
            Shutdown(code);
            return;
        }

        // 无界面识别测试：CadReviewUI.exe --recognize-test <日志文件> <新的输出目录> <dwg> [<dwg> ...]
        if (args.Length >= 4 && args[0] == "--recognize-test")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var lines = new List<string>();
            int code = 1;
            try
            {
                var drawings = args.Skip(3).ToList();
                var problems = Engine.CheckDrawings(drawings);
                lines.Add("入口检查：" + (problems.Count == 0 ? "通过" : string.Join(" | ", problems)));
                if (problems.Count == 0)
                {
                    var result = await Engine.RecognizeAsync(drawings, args[2], l => { lock (lines) lines.Add("  > " + l); }, CancellationToken.None);
                    lines.Add($"结果：ok={result.Ok}  review={result.ReviewFolder}  {result.Message}");
                    code = result.Ok ? 0 : 1;
                }
            }
            catch (Exception ex) { lines.Add("异常：" + ex); code = 2; }
            File.WriteAllLines(args[1], lines, new System.Text.UTF8Encoding(true));
            Shutdown(code);
            return;
        }

        // 开发/验收用：--open <review目录> [--sessions <存档目录>] [--demo] [--screenshot <png>]
        var (open, shot, demo) = ConfigureWindowOptions(args);
        var window = new MainWindow(demo);
        MainWindow = window;
        window.Show();
        if (open != null) window.OpenTask(open);
        if (demo) window.ApplyDemo();
        if (shot != null)
        {
            await window.RenderToPngAsync(shot);
            // Let the normal Closing handler block exit if decisions cannot be saved.
            window.Close();
        }
    }
}
