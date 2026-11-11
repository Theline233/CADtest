using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace CadReviewUI;

public partial class ApiReviewWindow : Window
{
    private sealed class ProfileChoice(ApiProfile profile)
    {
        public ApiProfile Profile { get; } = profile;
        public string DisplayName { get; } = profile.Name + "（启用中）";
    }

    private ApiProfileStore? _store;
    private ApiProfile? _active;
    private ApiImageSelection? _selectedImage;
    private string? _resultDirectory;
    private readonly Func<Window, string?>? _chooseImage;
    private CancellationTokenSource? _operation;
    private bool _closing;

    // Display only. It is deliberately not an argument to either gateway method or the result writer.
    public string LocalContext
    {
        set => LocalContextBox.Text = string.IsNullOrWhiteSpace(value)
            ? "未提供本地记录，可独立复查截图。" : value;
    }

    public ApiReviewWindow() : this(null, null, null) { }

    // Isolated event tests can supply their own store, result directory and picker.
    internal ApiReviewWindow(ApiProfileStore? store, string? resultDirectory, Func<Window, string?>? chooseImage)
    {
        InitializeComponent();
        _chooseImage = chooseImage;
        try
        {
            _store = store ?? new ApiProfileStore();
            _resultDirectory = resultDirectory ?? ApiPrivateStorage.DefaultResultDirectory();
            RefreshActiveProfile();
        }
        catch
        {
            TargetText.Text = "无法读取 API 配置。";
            StatusText.Text = "请检查本机配置存储。";
            SubmitButton.IsEnabled = false;
        }
    }

    private static ApiProfile Copy(ApiProfile profile) => new()
    {
        Id = profile.Id, Name = profile.Name, Protocol = profile.Protocol,
        BaseUrl = profile.BaseUrl, Model = profile.Model, HasKey = profile.HasKey
    };

    private ApiProfile? ReadActiveProfile()
    {
        if (_store == null) return null;
        var activeId = _store.ActiveId;
        var profile = _store.List().FirstOrDefault(p => p.Id == activeId);
        return profile == null ? null : Copy(profile);
    }

    private static bool SameProfile(ApiProfile? a, ApiProfile? b) =>
        a == null ? b == null : b != null && a.Id == b.Id && a.Name == b.Name
            && a.Protocol == b.Protocol && a.BaseUrl == b.BaseUrl && a.Model == b.Model;

    private void RefreshActiveProfile()
    {
        var previous = _active;
        _active = ReadActiveProfile();
        if (!SameProfile(previous, _active)) ClearResult();
        ProfileChoice[] choices = _active == null ? [] : [new ProfileChoice(_active)];
        ActiveProfileInput.ItemsSource = choices;
        ActiveProfileInput.SelectedIndex = choices.Length == 0 ? -1 : 0;
        ActiveProfileInput.IsEnabled = choices.Length != 0;
        TargetText.Text = _active == null
            ? "尚未启用配置，请在 API 设置中保存并启用。"
            : $"图片发送目标：{_active.BaseUrl}\n接口：{ProtocolName(_active.Protocol)}；请求模型：{_active.Model}";
        SendTargetText.Text = _active == null ? "发送目标：尚未启用配置。" : "发送目标：" + _active.BaseUrl;
        SubmitButton.IsEnabled = _active != null && _selectedImage != null;
        StatusText.Text = _active == null ? "请先启用 API 配置。" : "请确认配置，并手动选择截图。";
    }

    private static string ProtocolName(string protocol) => protocol switch
    {
        "responses" => "OpenAI Responses",
        "chat_completions" => "OpenAI 兼容 Chat Completions",
        _ => "未知类型"
    };

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        var window = new ApiSettingsWindow(_store) { Owner = this };
        window.ShowDialog();
        try { RefreshActiveProfile(); }
        catch
        {
            _active = null;
            ActiveProfileInput.ItemsSource = null;
            TargetText.Text = "无法读取当前启用的配置。";
            SendTargetText.Text = "发送目标：配置读取失败。";
            SubmitButton.IsEnabled = false;
            ClearResult();
            StatusText.Text = "请检查本机配置存储。";
        }
    }

    private async void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null) return;
        string? path;
        if (_chooseImage != null) path = _chooseImage(this);
        else
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择一张图框截图",
                Filter = "图框截图 (*.png;*.jpg;*.jpeg;*.webp)|*.png;*.jpg;*.jpeg;*.webp",
                CheckFileExists = true,
                Multiselect = false
            };
            path = dialog.ShowDialog(this) == true ? dialog.FileName : null;
        }
        if (string.IsNullOrEmpty(path)) return;
        _selectedImage = null;
        SubmitButton.IsEnabled = false;
        ImagePreview.Source = null;
        ImagePathText.Text = "正在验证所选图片…";
        ClearResult();
        using var cts = new CancellationTokenSource();
        _operation = cts;
        ReviewBody.IsEnabled = false;
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        StatusText.Text = "正在验证本机图片…";
        try
        {
            var selected = await ApiGateway.SelectImageAsync(path, cts.Token);
            if (_closing) return;
            if (!LoadPreview(selected))
            {
                ImagePathText.Text = "图片无法预览，请重新选择。";
                StatusText.Text = "无法确认图片预览，未选择或发送。";
                return;
            }
            _selectedImage = selected;
            ImagePathText.Text = "已选择：" + selected.Path;
            SubmitButton.IsEnabled = _active != null;
            StatusText.Text = "请核对截图与发送目标，点击“发送截图并复查”即发送。";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) StatusText.Text = "图片选择已取消，未发送。";
        }
        catch (InvalidDataException error)
        {
            if (!_closing) { ImagePathText.Text = "图片未选择。"; StatusText.Text = error.Message; }
        }
        catch
        {
            if (!_closing) { ImagePathText.Text = "图片未选择。"; StatusText.Text = "无法安全读取所选图片，请重新选择。"; }
        }
        finally
        {
            _operation = null;
            if (!_closing) { ReviewBody.IsEnabled = true; CancelOperationButton.Visibility = Visibility.Collapsed; }
        }
    }

    private bool LoadPreview(ApiImageSelection selected)
    {
        ImagePreview.Source = null;
        try
        {
            using var stream = selected.OpenPreviewStream();
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 640;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            ImagePreview.Source = image;
            PreviewHint.Text = "";
            return true;
        }
        catch
        {
            PreviewHint.Text = "本机无法预览该图片，不能发送。";
            return false;
        }
    }

    private void ClearResult()
    {
        SuggestionBox.Text = "";
        SourceText.Text = "";
        UsageText.Text = "";
        RecordText.Text = "尚无复查记录。";
    }

    private static bool IsSafeProfile(ApiProfile profile)
    {
        return (profile.Protocol == "responses" || profile.Protocol == "chat_completions")
            && !string.IsNullOrWhiteSpace(profile.Model)
            && Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrEmpty(uri.Host) && uri.UserInfo.Length == 0
            && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    private async void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null || _store == null || _active == null || _selectedImage == null || _resultDirectory == null) return;
        var profile = Copy(_active);
        var selected = _selectedImage;
        var imagePath = selected.Path;
        if (!IsSafeProfile(profile))
        {
            StatusText.Text = "当前配置无效，请在 API 设置中检查地址、接口类型和模型。";
            return;
        }
        try
        {
            if (!SameProfile(profile, ReadActiveProfile()))
            {
                RefreshActiveProfile();
                StatusText.Text = "启用的配置已改变，请核对目标后重新发起复查。";
                return;
            }
        }
        catch { StatusText.Text = "无法读取启用的配置，未发送截图。"; return; }

        string? key;
        try
        {
            if (!SameProfile(profile, ReadActiveProfile()))
            {
                RefreshActiveProfile();
                StatusText.Text = "启用的配置已改变，请核对目标后重新发起复查。";
                return;
            }
            key = _store.GetKey(profile.Id);
            if (!SameProfile(profile, ReadActiveProfile()))
            {
                RefreshActiveProfile();
                StatusText.Text = "启用的配置已改变，请核对目标后重新发起复查。";
                return;
            }
        }
        catch { StatusText.Text = "无法读取配置或密钥，未发送截图。"; return; }

        using var cts = new CancellationTokenSource();
        _operation = cts;
        ReviewBody.IsEnabled = false;
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        ClearResult();
        StatusText.Text = "正在核对所选图片并提交复查…";
        try
        {
            var result = await new ApiGateway().ReviewSelectedAsync(profile, key, selected, cts.Token);
            if (_closing) return;
            if (cts.IsCancellationRequested)
            {
                StatusText.Text = "已请求取消复查，未生成成功记录；服务商可能已收到请求。";
                return;
            }

            CancelOperationButton.IsEnabled = false;
            string suggestion = Redact(result.Text, key);
            string returnedModel = Redact(result.ReturnedModel, key);
            string note = Redact(result.Note, key);
            SuggestionBox.Text = suggestion;
            SourceText.Text = $"来源：{Redact(profile.Name, key)}；{ProtocolName(profile.Protocol)}；"
                + $"{Redact(profile.BaseUrl, key)}；截图：{Redact(imagePath, key)}\n"
                + $"请求模型：{Redact(profile.Model, key)}；返回模型：{(returnedModel.Length == 0 ? "未提供" : returnedModel)}"
                + (note.Length == 0 ? "" : "\n" + note);
            UsageText.Text = $"输入 token：{TokenText(result.InputTokens)}；输出 token：{TokenText(result.OutputTokens)}。"
                + " 建议仅供人工核对，不会写回记录。";

            var time = DateTimeOffset.Now;
            string recordPath = Path.Combine(_resultDirectory, $"review-{time:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            try
            {
                await WriteRecordAsync(recordPath, selected.Sha256, imagePath, profile, key, suggestion, result, time);
                if (!_closing)
                {
                    RecordText.Text = "独立记录：" + recordPath;
                    StatusText.Text = "复查完成，已保存独立记录。";
                }
            }
            catch
            {
                if (!_closing)
                {
                    RecordText.Text = "记录写入失败；建议仍可在上方复制。可能存在未完成文件：" + recordPath;
                    StatusText.Text = "复查已返回，但独立记录未能完整保存。";
                }
            }
        }
        catch (ApiImageChangedException)
        {
            _selectedImage = null;
            if (!_closing)
            {
                SubmitButton.IsEnabled = false;
                ImagePreview.Source = null;
                ImagePathText.Text = "图片已变化，请重新选择。";
                StatusText.Text = "图片已变化或无法安全读取，未发送；请重新选择。";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing) StatusText.Text = cts.IsCancellationRequested
                ? "复查已取消，未生成成功记录；服务商可能已收到请求。"
                : "复查中断，未生成成功记录；请检查连接。";
        }
        catch (Exception error)
        {
            if (!_closing) StatusText.Text = "复查失败，未生成成功记录。" + Redact(ApiUiFailure.Describe(error), key);
        }
        finally
        {
            _operation = null;
            if (!_closing)
            {
                ReviewBody.IsEnabled = true;
                CancelOperationButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static async Task WriteRecordAsync(string path, string hash, string imagePath,
        ApiProfile profile, string? key, string suggestion, ApiCallResult result, DateTimeOffset time)
    {
        var record = new
        {
            imageSha256 = hash,
            imageFileName = Redact(Path.GetFileName(imagePath), key),
            configuration = new
            {
                name = Redact(profile.Name, key),
                protocol = Redact(profile.Protocol, key),
                model = Redact(profile.Model, key),
                baseUrl = Redact(profile.BaseUrl, key)
            },
            returnedModel = Redact(result.ReturnedModel, key),
            suggestion,
            usage = new { inputTokens = result.InputTokens, outputTokens = result.OutputTokens },
            time = time.ToString("O")
        };
        using var directoryLease = ApiPrivateStorage.OpenDirectory(Path.GetDirectoryName(path) ?? throw new IOException("复查记录目录无效。"));
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.Asynchronous);
        await JsonSerializer.SerializeAsync(output, record, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        await output.FlushAsync();
    }

    private static string TokenText(long? value) => value?.ToString() ?? "未提供";
    private static string Redact(string? text, string? key)
    {
        return ApiSecurity.Redact(text, key);
    }

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        CancelOperationButton.IsEnabled = false;
        StatusText.Text = "已请求取消复查；服务商可能已收到请求。";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _closing = true;
        _operation?.Cancel();
    }
}
