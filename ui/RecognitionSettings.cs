using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

// Kept separate from API profiles: these settings never contain credentials.
internal sealed class RecognitionSettingsStore
{
    readonly string directory, path, mutexName;
    sealed class Data
    {
        [JsonRequired, JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonRequired, JsonPropertyName("maxParallel")] public int MaxParallel { get; set; }
    }

    internal RecognitionSettingsStore(string? directory = null)
    {
        this.directory = Path.GetFullPath(directory ?? Path.Combine(Engine.OutputsRoot, "ui-settings"));
        path = Path.Combine(this.directory, "recognition.json");
        mutexName = @"Local\CADtest.RecognitionSettings." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path.ToUpperInvariant())));
    }

    T Locked<T>(Func<T> action)
    {
        using var mutex = new Mutex(false, mutexName);
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(TimeSpan.FromSeconds(3)); }
            catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("识别设置正被其他窗口使用，请稍后重试。");
            return action();
        }
        finally { if (held) mutex.ReleaseMutex(); }
    }

    Data Read()
    {
        if (!File.Exists(path)) return new Data();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 4096) throw new InvalidDataException("识别设置文件过大。");
        try
        {
            var data = JsonSerializer.Deserialize<Data>(stream);
            if (data == null || data.Version != 1 || data.MaxParallel is < 0 or > 6)
                throw new InvalidDataException("识别设置的版本或并发数量无效。");
            return data;
        }
        catch (JsonException ex) { throw new InvalidDataException("识别设置文件无法解析。", ex); }
    }

    internal int Load() => Locked(() => Read().MaxParallel);

    internal void Save(int maxParallel)
    {
        if (maxParallel is < 0 or > 6) throw new ArgumentOutOfRangeException(nameof(maxParallel));
        Locked(() =>
        {
            // Do not replace an unreadable or newer settings file with defaults.
            _ = Read();
            Directory.CreateDirectory(directory);
            string temporary = Path.Combine(directory, ".recognition-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new Data { MaxParallel = maxParallel },
                    new JsonSerializerOptions { WriteIndented = true });
                using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    file.Write(bytes);
                    file.Flush(true);
                }
                File.Move(temporary, path, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        });
    }
}

internal sealed class RecognitionSettingsWindow : Window
{
    internal RecognitionSettingsWindow(RecognitionSettingsStore store)
    {
        Title = "识别设置";
        Width = 390; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "多张图纸同时识别", FontSize = 16, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "自动由识别引擎根据资源决定；也可以限制同时运行的张数。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        var choices = new ComboBox { MinWidth = 230, HorizontalAlignment = HorizontalAlignment.Left };
        choices.Items.Add("自动");
        for (int i = 1; i <= 6; i++) choices.Items.Add($"最多 {i} 张");
        choices.SelectedIndex = store.Load();
        panel.Children.Add(choices);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        var save = new Button { Content = "保存", MinWidth = 72, Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            try { store.Save(choices.SelectedIndex); DialogResult = true; }
            catch (Exception ex) { MessageBox.Show(this, "无法保存识别设置：" + ex.Message, "识别设置", MessageBoxButton.OK, MessageBoxImage.Warning); }
        };
        var cancel = new Button { Content = "取消", MinWidth = 72, Padding = new Thickness(10, 5, 10, 5), IsCancel = true };
        buttons.Children.Add(save); buttons.Children.Add(cancel); panel.Children.Add(buttons); Content = panel;
    }
}
