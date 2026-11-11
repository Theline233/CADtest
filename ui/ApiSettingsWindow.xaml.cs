using System.ComponentModel;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace CadReviewUI;

public partial class ApiSettingsWindow : Window
{
    private sealed class ProfileChoice(ApiProfile profile, bool active)
    {
        public ApiProfile Profile { get; } = profile;
        public string DisplayName { get; } = active ? $"{profile.Name}（启用中）" : profile.Name;
    }

    private ApiProfileStore? _store;
    private ApiProfile? _current;
    private ProfileChoice? _selectedChoice;
    private bool _isNew, _settingEditor = true, _suppressSelection = true, _closing;
    private CancellationTokenSource? _operation;

    public ApiSettingsWindow() : this(null) { }

    internal ApiSettingsWindow(ApiProfileStore? store)
    {
        InitializeComponent();
        ModelInput.TextChanged += (_, _) => InvalidateTestResult();
        KeyInput.PasswordChanged += (_, _) => InvalidateTestResult();
        ClearKeyInput.Unchecked += (_, _) => InvalidateTestResult();
        _settingEditor = false;
        _suppressSelection = false;
        try
        {
            _store = store ?? new ApiProfileStore();
            ReloadProfiles();
        }
        catch
        {
            SettingsBody.IsEnabled = false;
            StatusText.Text = "无法读取 API 配置，请检查本机配置存储。";
        }
    }

    private static ApiProfile Copy(ApiProfile profile) => new()
    {
        Id = profile.Id, Name = profile.Name, Protocol = profile.Protocol,
        BaseUrl = profile.BaseUrl, Model = profile.Model, HasKey = profile.HasKey
    };

    private void ReloadProfiles(string? preferredId = null)
    {
        if (_store == null) return;
        var activeId = _store.ActiveId;
        var choices = _store.List().Select(p => new ProfileChoice(Copy(p), p.Id == activeId)).ToList();
        var choice = choices.FirstOrDefault(p => p.Profile.Id == preferredId)
            ?? choices.FirstOrDefault(p => p.Profile.Id == activeId)
            ?? choices.FirstOrDefault();
        _suppressSelection = true;
        try
        {
            ProfileList.ItemsSource = choices;
            ProfileList.SelectedItem = choice;
            _selectedChoice = choice;
            LoadEditor(choice?.Profile, false);
        }
        finally { _suppressSelection = false; }
        StatusText.Text = choices.Count == 0 ? "尚无配置，请先新增。" : "选择配置后可编辑或启用。";
    }

    private void LoadEditor(ApiProfile? profile, bool isNew)
    {
        _settingEditor = true;
        try
        {
            _current = profile == null ? null : Copy(profile);
            _isNew = isNew;
            EditorPanel.IsEnabled = profile != null;
            DeleteButton.IsEnabled = profile != null && !isNew;
            ActivateButton.IsEnabled = profile != null && !isNew;
            EditorTitle.Text = profile == null ? "请选择或新增配置" : isNew ? "新增配置" : "编辑配置";
            ProfileNameInput.Text = profile?.Name ?? "";
            ProtocolInput.SelectedValue = profile?.Protocol ?? "responses";
            BaseUrlInput.Text = profile?.BaseUrl ?? "";
            ModelInput.Text = profile?.Model ?? "";
            KeyInput.Clear();
            ClearKeyInput.IsChecked = false;
            TestResultText.Text = "";
            UpdateKeyState();
        }
        finally { _settingEditor = false; }
    }

    private void UpdateKeyState()
    {
        KeyStateText.Text = _current?.HasKey == true && !_isNew
            ? "已保存密钥，不会显示。同一地址及类型留空保留；变更时请重新输入或明确清除。"
            : "未显示任何已保存密钥。输入新密钥，或留空使用无密钥配置。";
    }

    private bool IsDirty()
    {
        if (_current == null) return false;
        return _isNew
            || ProfileNameInput.Text.Trim() != _current.Name
            || (ProtocolInput.SelectedValue as string ?? "") != _current.Protocol
            || !SameEndpoint(BaseUrlInput.Text, _current.BaseUrl)
            || ModelInput.Text.Trim() != _current.Model
            || KeyInput.Password.Length != 0
            || ClearKeyInput.IsChecked == true;
    }

    private bool ConfirmDiscard()
    {
        return !IsDirty() || MessageBox.Show(this, "有未保存的配置或密钥输入。继续将放弃这些更改，是否继续？",
            "API 设置", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private static bool SameEndpoint(string? left, string? right)
    {
        try { return ApiSecurity.ValidateBaseUrl(left) == ApiSecurity.ValidateBaseUrl(right); }
        catch (ArgumentException) { return false; }
    }

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        var next = ProfileList.SelectedItem as ProfileChoice;
        if (!ConfirmDiscard())
        {
            _suppressSelection = true;
            ProfileList.SelectedItem = _selectedChoice;
            _suppressSelection = false;
            return;
        }
        _selectedChoice = next;
        LoadEditor(next?.Profile, false);
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDiscard()) return;
        _suppressSelection = true;
        ProfileList.SelectedItem = null;
        _selectedChoice = null;
        _suppressSelection = false;
        LoadEditor(new ApiProfile(), true);
        StatusText.Text = "填写新配置并保存。";
        ProfileNameInput.Focus();
    }

    private void BaseUrlInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_settingEditor) return;
        // A key typed for an earlier address must never follow an address edit.
        KeyInput.Clear();
        InvalidateTestResult();
    }

    private void ProtocolInput_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingEditor) return;
        KeyInput.Clear();
        InvalidateTestResult();
    }

    private void ClearKeyInput_Checked(object sender, RoutedEventArgs e)
    {
        if (!_settingEditor) { KeyInput.Clear(); InvalidateTestResult(); }
    }

    private void InvalidateTestResult()
    {
        if (_settingEditor) return;
        TestResultText.Text = "";
        if (StatusText.Text == "连接测试已完成。") StatusText.Text = "配置已更改，请重新测试。";
    }

    private bool TryReadEditor(out ApiProfile? profile, out string? newKey, out bool clearKey)
    {
        profile = null;
        newKey = null;
        clearKey = ClearKeyInput.IsChecked == true;
        if (_current == null) return false;
        string name = ProfileNameInput.Text.Trim();
        string model = ModelInput.Text.Trim();
        string address = BaseUrlInput.Text.Trim();
        string protocol = ProtocolInput.SelectedValue as string ?? "";
        string entered = KeyInput.Password;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(model))
            return Error("请填写配置名称和模型。");
        if (protocol != "responses" && protocol != "chat_completions")
            return Error("请选择接口类型。");
        string normalizedAddress;
        try { normalizedAddress = ApiSecurity.ValidateBaseUrl(address); }
        catch (ArgumentException error) { return Error(error.Message); }
        if (entered.Length > 0 && string.IsNullOrWhiteSpace(entered))
            return Error("密钥不能只有空白字符。");
        if (clearKey && entered.Length != 0)
            return Error("请只选择输入新密钥或明确清除密钥中的一种。");
        bool endpointChanged = !SameEndpoint(normalizedAddress, _current.BaseUrl) || protocol != _current.Protocol;
        if (!_isNew && _current.HasKey && endpointChanged && entered.Length == 0 && !clearKey)
            return Error("服务地址或接口类型已更改；请重新输入密钥，或勾选明确清除后匿名使用。");
        profile = new ApiProfile
        {
            Id = _current.Id, Name = name, Protocol = protocol, BaseUrl = normalizedAddress,
            Model = model, HasKey = _current.HasKey
        };
        newKey = entered.Length == 0 ? null : entered;
        return true;
    }

    private bool CheckStoredAddress()
    {
        if (_store == null || _current == null) return false;
        if (_isNew) return true;
        var saved = _store.List().FirstOrDefault(p => p.Id == _current.Id);
        return saved != null && SameEndpoint(saved.BaseUrl, _current.BaseUrl)
            && saved.Protocol == _current.Protocol
            || Error("此配置的地址或类型已在其他位置改变，或配置已删除；请重新选择配置后再操作。");
    }

    private bool Error(string message)
    {
        StatusText.Text = message;
        return false;
    }

    private bool SaveCurrent(bool setActive)
    {
        if (_store == null || !TryReadEditor(out var profile, out var newKey, out var clearKey) || profile == null)
            return false;
        try
        {
            if (!CheckStoredAddress()) return false;
            _store.Save(profile, newKey, clearKey, setActive);
        }
        catch (ArgumentException)
        {
            return Error("配置保存失败，请检查名称是否重复、模型与密钥格式以及基础地址。");
        }
        catch
        {
            return Error("配置保存失败，请检查本机配置存储；密钥未显示。");
        }
        KeyInput.Clear();
        try
        {
            ReloadProfiles(profile.Id);
            StatusText.Text = setActive ? "配置已保存并启用。" : "配置已保存。";
        }
        catch
        {
            _current = null;
            SettingsBody.IsEnabled = false;
            StatusText.Text = "配置已保存，但重新读取失败；请关闭并重新打开设置以确认。";
        }
        return true;
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent(false);
    private void SaveAndActivate_Click(object sender, RoutedEventArgs e) => SaveCurrent(true);

    private void Activate_Click(object sender, RoutedEventArgs e)
    {
        if (_store == null || _current == null || _isNew) return;
        if (IsDirty()) { Error("请先保存或放弃未保存的更改，再启用配置。"); return; }
        try
        {
            if (!CheckStoredAddress()) return;
            _store.Select(_current.Id);
            ReloadProfiles(_current.Id);
            StatusText.Text = "已切换启用的配置。";
        }
        catch { Error("无法启用配置，请检查本机配置存储。"); }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_store == null || _current == null || _isNew || !ConfirmDiscard()) return;
        if (MessageBox.Show(this, $"确定删除配置“{_current.Name}”及其保存的密钥吗？", "删除配置",
            MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            _store.Delete(_current.Id);
            ReloadProfiles();
            StatusText.Text = "配置已删除。";
        }
        catch { Error("无法删除配置，请检查本机配置存储。"); }
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_operation != null || _store == null
            || !TryReadEditor(out var profile, out var newKey, out var clearKey) || profile == null) return;
        string? key;
        try
        {
            if (!CheckStoredAddress()) return;
            bool endpointChanged = !SameEndpoint(profile.BaseUrl, _current!.BaseUrl) || profile.Protocol != _current.Protocol;
            key = newKey ?? (!_isNew && !clearKey && !endpointChanged ? _store.GetKey(profile.Id) : null);
            if (newKey == null && !_isNew && !clearKey && !endpointChanged && !CheckStoredAddress()) return;
        }
        catch { Error("无法读取密钥，连接测试未开始。"); return; }

        using var cts = new CancellationTokenSource();
        _operation = cts;
        SettingsBody.IsEnabled = false;
        CancelOperationButton.IsEnabled = true;
        CancelOperationButton.Visibility = Visibility.Visible;
        StatusText.Text = "正在发送最小文字连接测试…";
        TestResultText.Text = "";
        try
        {
            var gateway = new ApiGateway();
            var result = newKey != null
                ? await gateway.TestWithExplicitKeyAsync(profile, newKey, cts.Token)
                : await gateway.TestAsync(profile, key, cts.Token);
            if (_closing) return;
            if (cts.IsCancellationRequested) { StatusText.Text = "已请求取消测试；服务商可能已收到请求。"; return; }
            string note = Redact(result.Note, key);
            string returnedModel = Redact(result.ReturnedModel, key);
            TestResultText.Text = $"测试完成。返回模型：{(returnedModel.Length == 0 ? "未提供" : returnedModel)}；"
                + $"输入 token：{TokenText(result.InputTokens)}；输出 token：{TokenText(result.OutputTokens)}。"
                + (note.Length == 0 ? "" : "\n" + note);
            StatusText.Text = "连接测试已完成。";
        }
        catch (OperationCanceledException)
        {
            if (!_closing) StatusText.Text = cts.IsCancellationRequested
                ? "测试已取消；服务商可能已收到请求。"
                : "连接测试中断，请检查连接；服务商可能已收到请求。";
        }
        catch (Exception error)
        {
            if (!_closing) StatusText.Text = "连接测试失败。" + Redact(ApiUiFailure.Describe(error), key);
        }
        finally
        {
            _operation = null;
            if (!_closing)
            {
                SettingsBody.IsEnabled = true;
                CancelOperationButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private static string TokenText(long? value) => value?.ToString() ?? "未提供";
    private static string Redact(string? text, string? key) => ApiSecurity.Redact(text, key);

    private void CancelOperation_Click(object sender, RoutedEventArgs e)
    {
        _operation?.Cancel();
        CancelOperationButton.IsEnabled = false;
        StatusText.Text = "已请求取消测试；服务商可能已收到请求。";
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!ConfirmDiscard()) { e.Cancel = true; return; }
        _closing = true;
        _operation?.Cancel();
        KeyInput.Clear();
    }
}

// Only fixed UI messages and codes from the gateway's exact formats are displayed.
// Never display or log the exception text or a service response body.
internal static class ApiUiFailure
{
    private const string NetworkFailure = "网络请求失败，请检查连接和服务地址。未自动重试。";

    public static string Describe(Exception error)
    {
        if (error is TimeoutException) return "请求超时；服务商可能已收到请求。";
        if (error is HttpRequestException) return "网络请求失败，请检查连接和服务地址。";
        if (error is InvalidOperationException)
        {
            string message = error.Message;
            if (message == NetworkFailure) return "网络请求失败，请检查连接和服务地址。";
            if (message.Length <= 200)
            {
                var match = Regex.Match(message,
                    @"\A(?:服务端返回 HTTP (?<code>3[0-9]{2}) 重定向，已停止请求。|服务端拒绝请求（HTTP (?<code>400|422)）；请检查模型、输入及接口是否支持 (?:max_output_tokens|max_tokens) 参数。|服务端请求失败（HTTP (?<code>[1-5][0-9]{2})）。)\z",
                    RegexOptions.CultureInvariant);
                if (match.Success && int.TryParse(match.Groups["code"].Value, out int code))
                {
                    string detail = code switch
                    {
                        >= 300 and < 400 => "服务返回重定向，已停止请求；请检查基础地址。",
                        400 or 422 => "服务拒绝请求，请检查接口类型、模型及输入是否受支持。",
                        401 or 403 => "服务拒绝访问，请检查密钥和访问权限。",
                        404 or 405 => "服务未接受该地址或方法，请检查基础地址、接口类型及模型。",
                        429 => "服务拒绝请求，请检查额度或限流。",
                        >= 500 => "服务未能完成请求，请检查服务状态。",
                        _ => "服务未接受请求，请检查接口配置。"
                    };
                    return $"HTTP {code}：{detail}";
                }
            }
        }
        return "请求未完成，请检查配置和连接。";
    }
}
