using AnimaAIProxy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace AnimaAIProxy;

/// <summary>
/// 主窗口：代理的启停、模型提供商与模型表、令牌管理、词库状态、实时日志。
///
/// **这里刻意不放任何"聪明"的逻辑。** 全部判定（工具怎么合并、令牌怎么校验、
/// 上游出错回什么状态码、某个模型该发给哪个站点）都在 <c>Core/</c> 里，
/// 而且都被离线回归钉住了。界面只做三件事：把配置读进来、把配置写出去、
/// 把状态显示出来。界面里多一条判断，就多一条回归覆盖不到的路径——
/// 而"设置界面上的行为"恰恰是最容易在改别处时被悄悄弄坏的地方。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ProxyConfig _cfg;
    private readonly TokenStore _tokens;
    private readonly VocabService _vocab = new();
    private ProxyServer? _server;

    private readonly ObservableCollection<string> _logLines = new();
    private readonly ObservableCollection<TokenRow> _tokenRows = new();
    private readonly ObservableCollection<ProviderRow> _providerRows = new();
    private readonly ObservableCollection<ModelRow> _modelRows = new();

    /// <summary>抑制事件：在代码里回填控件值时，别让 Changed 事件又去写配置。</summary>
    private bool _loading;

    /// <summary>
    /// 重建列表时抑制事件。**必须有**：重建会把每个复选框重新创建一次，
    /// 那会触发 Checked/Unchecked，于是每次刷新列表都会写一遍配置、
    /// 并在日志里留下"已启用"这种什么都没发生的记录。
    /// </summary>
    private bool _rebuilding;

    public MainWindow()
    {
        InitializeComponent();

        _cfg = ProxyConfig.Load();
        _tokens = TokenStore.Load();

        LogList.ItemsSource = _logLines;
        TokenList.ItemsSource = _tokenRows;
        ProviderList.ItemsSource = _providerRows;
        ModelList.ItemsSource = _modelRows;

        // 先把历史日志灌进面板：用户打开界面时想看到"刚才发生了什么"，
        // 而不是从打开这一刻开始的一片空白。
        foreach (var line in ProxyLog.Snapshot()) _logLines.Add(line);
        ProxyLog.Written += OnLogWritten;
        // 按需拉取是接入方调 /v1/models 时触发的，改的是内存里的模型表；
        // 界面列表是快照，不刷新就会一直显示"尚未拉取模型"（见 ProxyServer.ModelsChanged）。
        ProxyServer.ModelsChanged += OnModelsChanged;

        LogPathText.Text = ProxyLog.FilePath;

        // 托盘菜单项的接线必须在这里做：菜单项只认 Command（见 WireTrayCommands）。
        WireTrayCommands();

        // 下拉框的选项从 Core 的档位表填进来（**单一来源**：界面不自己列一遍秒数）。
        InitCleanupOptions();
        LoadIntoUi();
        RefreshStatus();

        // 关窗口 = 收进托盘，**不是退出**。
        // 这个程序的正事是在后台持续提供 HTTP 服务；关掉窗口就把服务一起停掉，
        // 等于"我瞄一眼状态"这个动作会把接入方的 agent 打断。
        // 真正退出走托盘菜单里的"退出"。
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        // 词库载入要几秒（113 MB），放到界面出来之后再做，
        // 否则窗口会白屏等它——用户会以为程序卡死了。
        if (_cfg.PreloadVocab)
            _ = WarmUpVocabAsync();

        if (_cfg.AutoStart)
            _ = StartServerAsync();
    }

    /// <summary>用户明确点了托盘里的"退出"才会置位。</summary>
    private bool _exiting;

    // ---------------- 配置 ↔ 界面 ----------------

    private void LoadIntoUi()
    {
        _loading = true;
        try
        {
            TimeoutBox.Value = _cfg.TimeoutSeconds;
            ModelMapBox.Text = string.Join(Environment.NewLine,
                _cfg.ModelMap.Select(kv => kv.Key + "=" + kv.Value));

            ToolsEnabledSwitch.IsOn = _cfg.ToolsEnabled;
            AnnounceToolsSwitch.IsOn = _cfg.AnnounceTools;
            CarryFindingsSwitch.IsOn = _cfg.CarryFindings;
            MaxRoundsBox.Value = _cfg.MaxToolRounds;
            MaxCallsBox.Value = _cfg.MaxToolCallsPerRequest;

            PortBox.Value = _cfg.Port;
            RequireTokenSwitch.IsOn = _cfg.RequireToken;
            LogBodiesSwitch.IsOn = _cfg.LogBodies;
            SelectScope(_cfg.BindScope);

            VocabPathBox.Text = _cfg.VocabDbPath;
            SeedPathBox.Text = _cfg.SeedPath;

            SearchEnabledSwitch.IsOn = _cfg.SearchEnabled;
            SelectProvider(_cfg.SearchProviderKey);
            SearchBaseUrlBox.Text = _cfg.SearchBaseUrl;
            SearchSniBox.Text = _cfg.SearchSniHost;
            SearchMaxResultsBox.Value = _cfg.SearchMaxResults;
            SearchTimeoutBox.Value = _cfg.SearchTimeoutSeconds;
            // 密钥框永远不回填明文（界面上没有明文可回填），"空"一律理解成"不改动"。
            SearchKeyBox.Password = "";
            RefreshSearchStatus();

            FetchEnabledSwitch.IsOn = _cfg.FetchEnabled;
            SelectFetchEngine(_cfg.FetchEngineKey);
            FetchProxyBox.Text = _cfg.FetchProxy;
            FetchBrowserPathBox.Text = _cfg.FetchBrowserPath;
            FetchMaxCharsBox.Value = _cfg.FetchMaxChars;
            FetchTimeoutBox.Value = _cfg.FetchTimeoutSeconds;
            SelectCleanup(_cfg.FetchCleanupSeconds);
            FetchPrivateSwitch.IsOn = _cfg.FetchAllowPrivateHosts;
            if ((FetchTestUrlBox.Text ?? "").Trim().Length == 0)
                FetchTestUrlBox.Text = "https://example.com/";
            RefreshFetchStatus();

            RefreshProviderList();
            RefreshModelList();
            RefreshTokenList();
            RefreshVocabStatus();
        }
        finally
        {
            _loading = false;
        }
    }

    private void SelectScope(string scope)
    {
        for (var i = 0; i < ScopeBox.Items.Count; i++)
        {
            if (ScopeBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, scope, StringComparison.Ordinal))
            {
                ScopeBox.SelectedIndex = i;
                return;
            }
        }
        ScopeBox.SelectedIndex = 0;
    }

    /// <summary>把界面上的值收回配置对象。返回"监听参数变了没有"（变了要重启才生效）。</summary>
    private bool SaveFromUi()
    {
        var oldPort = _cfg.Port;
        var oldScope = _cfg.BindScope;

        _cfg.TimeoutSeconds = ToInt(TimeoutBox.Value, _cfg.TimeoutSeconds);
        _cfg.ModelMap = ParseModelMap(ModelMapBox.Text);

        _cfg.ToolsEnabled = ToolsEnabledSwitch.IsOn;
        _cfg.AnnounceTools = AnnounceToolsSwitch.IsOn;
        _cfg.CarryFindings = CarryFindingsSwitch.IsOn;
        _cfg.MaxToolRounds = ToInt(MaxRoundsBox.Value, _cfg.MaxToolRounds);
        _cfg.MaxToolCallsPerRequest = ToInt(MaxCallsBox.Value, _cfg.MaxToolCallsPerRequest);

        _cfg.Port = ToInt(PortBox.Value, _cfg.Port);
        _cfg.RequireToken = RequireTokenSwitch.IsOn;
        _cfg.LogBodies = LogBodiesSwitch.IsOn;
        _cfg.BindScope = (ScopeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "local";

        _cfg.VocabDbPath = (VocabPathBox.Text ?? "").Trim();
        _cfg.SeedPath = (SeedPathBox.Text ?? "").Trim();

        _cfg.SearchEnabled = SearchEnabledSwitch.IsOn;
        _cfg.SearchProvider = (SearchProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        _cfg.SearchBaseUrl = (SearchBaseUrlBox.Text ?? "").Trim();
        _cfg.SearchSniHost = (SearchSniBox.Text ?? "").Trim();
        _cfg.SearchMaxResults = ToInt(SearchMaxResultsBox.Value, _cfg.SearchMaxResults);
        _cfg.SearchTimeoutSeconds = ToInt(SearchTimeoutBox.Value, _cfg.SearchTimeoutSeconds);
        // 与站点编辑对话框同一条约定：框里有内容才覆盖密钥，空 = 不改动。
        // 否则每次点"保存设置"都会把密钥清掉——而用户无从察觉，只会在下次搜索时
        // 收到一句"需要 API Key"。
        var typedSearchKey = SearchKeyBox.Password;
        if (typedSearchKey.Length > 0)
        {
            _cfg.SearchApiKey = typedSearchKey;
            SearchKeyBox.Password = "";
        }

        _cfg.FetchEnabled = FetchEnabledSwitch.IsOn;
        _cfg.FetchEngine = SelectedFetchEngine;
        _cfg.FetchProxy = (FetchProxyBox.Text ?? "").Trim();
        _cfg.FetchBrowserPath = (FetchBrowserPathBox.Text ?? "").Trim();
        _cfg.FetchMaxChars = ToInt(FetchMaxCharsBox.Value, _cfg.FetchMaxChars);
        _cfg.FetchTimeoutSeconds = ToInt(FetchTimeoutBox.Value, _cfg.FetchTimeoutSeconds);
        _cfg.FetchCleanupSeconds = SelectedCleanupSeconds;
        _cfg.FetchAllowPrivateHosts = FetchPrivateSwitch.IsOn;

        _cfg.Normalize();
        _cfg.Save();

        return oldPort != _cfg.Port ||
               !string.Equals(oldScope, _cfg.BindScope, StringComparison.Ordinal);
    }

    private static int ToInt(double v, int fallback) =>
        double.IsNaN(v) || double.IsInfinity(v) ? fallback : (int)v;

    /// <summary>
    /// 解析"客户端名=上游名"的行式映射。
    /// 坏行**跳过而不是报错**：这个框是手打的，一行写错不该让整份配置保存失败——
    /// 那会让用户丢掉其它所有改动。
    /// </summary>
    private static Dictionary<string, string> ParseModelMap(string? text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var i = line.IndexOf('=');
            if (i <= 0) continue;

            var from = line[..i].Trim();
            var to = line[(i + 1)..].Trim();
            if (from.Length > 0 && to.Length > 0) map[from] = to;
        }
        return map;
    }

    // ---------------- 启停 ----------------

    private async Task StartServerAsync()
    {
        SaveFromUi();
        RefreshStatus();

        _server ??= new ProxyServer(_cfg, _tokens, _vocab);
        try
        {
            ToggleButton.IsEnabled = false;
            await _server.StartAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            ProxyLog.Write("启动失败：" + ex.Message);
            await ShowAsync("启动失败",
                ex.Message + "\n\n端口被占用是最常见的原因，换一个端口再试。");
        }
        finally
        {
            ToggleButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async Task StopServerAsync()
    {
        var s = _server;
        if (s is null) return;

        try
        {
            ToggleButton.IsEnabled = false;
            await s.StopAsync();
        }
        catch (Exception ex)
        {
            ProxyLog.Write("停止失败：" + ex.Message);
        }
        finally
        {
            ToggleButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (_server?.IsRunning == true) await StopServerAsync();
        else await StartServerAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var needsRestart = SaveFromUi();

        // 改完就回填一次：Normalize 可能把越界值拉了回来，界面得显示**真实生效**
        // 的那个值，而不是用户刚敲进去的。否则用户会以为端口是 80，实际跑在 48620。
        LoadIntoUi();
        RefreshStatus();
        RefreshVocabStatus();

        if (needsRestart && _server?.IsRunning == true)
        {
            ProxyLog.Write("监听参数变了，重启代理以生效");
            await StopServerAsync();
            await StartServerAsync();
        }
    }

    private void RefreshStatus()
    {
        var running = _server?.IsRunning == true;

        StatusDot.Fill = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            running ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
        StatusText.Text = running ? "运行中" : "已停止";

        BaseUrlText.Text = _server is null
            ? $"http://127.0.0.1:{_cfg.Port}/v1"
            : _server.BaseUrl;

        if (running)
        {
            HintText.Text = _cfg.BindScope switch
            {
                "local" => "把平台的 OpenAI Base URL 填成上面这个地址，API Key 填代理令牌。仅本机可访问。",
                "lan" => "把平台的 OpenAI Base URL 填成上面这个地址（127.0.0.1 换成本机 IP），API Key 填代理令牌。",
                _ => "把平台的 OpenAI Base URL 填成上面这个地址，API Key 填代理令牌。当前允许任意来源连接。"
            };
        }
        else
        {
            HintText.Text = "启动后把上面这个地址填进 agent 平台的 OpenAI Base URL。";
        }

        UptimeText.Text = running && _server is not null && _server.StartedAtText.Length > 0
            ? "启动于 " + _server.StartedAtText
            : "";

        ToggleButton.Content = running ? "停止代理" : "启动代理";
        PortBox.IsEnabled = true;

        // 托盘提示：用户把窗口收起来之后，唯一能看到状态的地方就是这里。
        Tray.ToolTipText = running
            ? $"AnimaAIProxy 运行中 — {_server!.BaseUrl}"
            : "AnimaAIProxy 已停止（右键启动）";
    }

    private void CopyBaseUrl_Click(object sender, RoutedEventArgs e)
    {
        var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
        dp.SetText(BaseUrlText.Text);
        Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);

        // 复制反馈落在按钮上，不去覆盖 HintText —— 那是常驻的操作指引，
        // 被一句一次性的提示顶掉之后，用户下次想看该填什么就得重启界面。
        CopyBaseUrlButton.Content = "已复制";
        _ = RestoreCopyButtonAsync();
    }

    private async Task RestoreCopyButtonAsync()
    {
        await Task.Delay(1200);
        CopyBaseUrlButton.Content = "复制";
    }

    // ---------------- 模型提供商 ----------------

    /// <summary>
    /// 重建两个列表。**用 <see cref="DispatcherQueue"/> 推迟**：这些刷新几乎都是从
    /// 列表项自己的事件里发起的（点"设为默认"、勾选复选框），当场把整个列表重建
    /// 等于在控件的事件处理过程中把它自己销毁掉。推迟一拍就没有这个问题。
    /// </summary>
    private void DeferRefreshLists() =>
        DispatcherQueue.TryEnqueue(() =>
        {
            RefreshProviderList();
            RefreshModelList();
        });

    private void RefreshProviderList()
    {
        _rebuilding = true;
        try
        {
            _providerRows.Clear();
            foreach (var p in _cfg.Providers)
                _providerRows.Add(new ProviderRow
                {
                    Id = p.Id,
                    Title = p.DisplayName + (p.Enabled ? "" : "（已停用）"),
                    Subtitle = p.Subtitle,
                    Enabled = p.Enabled,
                    DefaultMark = string.Equals(p.Id, _cfg.DefaultProviderId, StringComparison.Ordinal)
                        ? "默认"
                        : ""
                });
        }
        finally
        {
            _rebuilding = false;
        }

        var on = _cfg.Providers.Count(p => p.Enabled);
        ProviderSummaryText.Text = _cfg.Providers.Count == 0
            ? "还没有站点。新增一个，填入 Base URL 与密钥。"
            : $"共 {_cfg.Providers.Count} 个站点，启用 {on} 个。" +
              "只有启用的站点会被拉取模型、出现在 /v1/models 里、并接受请求。";
    }

    private void RefreshModelList()
    {
        _rebuilding = true;
        try
        {
            _modelRows.Clear();
            foreach (var p in _cfg.Providers)
                foreach (var m in p.Models)
                    _modelRows.Add(new ModelRow
                    {
                        ProviderId = p.Id,
                        ModelId = m.Id,
                        Title = m.Id,
                        Subtitle = p.DisplayName
                                   + (m.Manual ? "　·　手工添加" : "")
                                   + (p.Enabled ? "" : "　·　站点已停用，不对外提供"),
                        Enabled = m.Enabled,
                        Manual = m.Manual
                    });
        }
        finally
        {
            _rebuilding = false;
        }

        var shown = _modelRows.Count(r => r.Enabled);
        ModelSummaryText.Text = _modelRows.Count == 0
            ? "还没有模型。点上面的「刷新模型」从启用的站点拉取，或用下面的框手工添加。"
            : $"共 {_modelRows.Count} 个模型，对外提供 {shown} 个。" +
              "关掉的不会出现在 /v1/models 里，也不会再被路由。";
    }

    private void ProviderEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _rebuilding) return;
        if (sender is not CheckBox box || box.DataContext is not ProviderRow row) return;

        var p = _cfg.ProviderById(row.Id);
        if (p is null) return;

        var on = box.IsChecked == true;
        if (p.Enabled == on) return;

        p.Enabled = on;
        row.Enabled = on;
        _cfg.Save();
        DeferRefreshLists();
        ProxyLog.Write($"站点 {p.DisplayName} 已{(on ? "启用" : "停用")}");
    }

    private void SetDefaultProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ProviderRow row) return;

        var p = _cfg.ProviderById(row.Id);
        if (p is null) return;

        _cfg.DefaultProviderId = p.Id;
        _cfg.Save();
        DeferRefreshLists();
        ProxyLog.Write($"默认站点改为 {p.DisplayName}（模型名不在任何站点模型表里时发给它）");
    }

    private async void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ProviderRow row) return;

        var p = _cfg.ProviderById(row.Id);
        if (p is null) return;

        // 删除会丢掉这个站点已存的密钥（DPAPI 密文随配置一起没了），
        // 所以这里问一句——重新填一遍密钥是有代价的。
        if (!await ConfirmAsync("删除站点",
                $"确定删除「{p.DisplayName}」吗？它的地址、密钥与模型表都会一起删掉。", "删除"))
            return;

        _cfg.Providers.Remove(p);
        if (string.Equals(_cfg.DefaultProviderId, p.Id, StringComparison.Ordinal))
            _cfg.DefaultProviderId = "";
        _cfg.Normalize();
        _cfg.Save();
        DeferRefreshLists();
        ProxyLog.Write($"删除了站点 {p.DisplayName}");
    }

    private async void AddProvider_Click(object sender, RoutedEventArgs e) => await EditProviderAsync(null);

    private async void EditProvider_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ProviderRow row) return;
        await EditProviderAsync(row.Id);
    }

    /// <summary>
    /// 新增 / 编辑一个站点。
    ///
    /// 在**副本**上编辑，点保存才写回——取消不该改动真配置，这是设置界面的基本约定。
    /// 密钥框永远不回填明文（界面上没有明文可回填），所以"空"必须理解成"不改动"，
    /// 否则每次编辑都会把密钥清掉。
    /// </summary>
    private async Task EditProviderAsync(string? id)
    {
        var target = id is null ? null : _cfg.ProviderById(id);
        var isNew = target is null;

        var nameBox = new TextBox
        {
            Header = "名称（可留空，用地址的主机名）",
            Text = target?.Name ?? ""
        };
        var urlBox = new TextBox
        {
            Header = "Base URL（须自带 /v1）",
            PlaceholderText = ProxyConfig.DefaultProviderBaseUrl,
            Text = target?.BaseUrl ?? ""
        };
        var keyBox = new PasswordBox
        {
            Header = "API Key",
            PlaceholderText = string.IsNullOrEmpty(target?.ApiKeyProtected) ? "留空表示不设密钥" : "留空表示不改动"
        };
        var enabledSwitch = new ToggleSwitch
        {
            Header = "启用",
            OnContent = "参与模型聚合与请求路由",
            OffContent = "停用：不拉模型、不接受请求",
            IsOn = target?.Enabled ?? true
        };
        var autoSwitch = new ToggleSwitch
        {
            Header = "自动拉取模型",
            OnContent = "接入方拉 /v1/models 时按需拉一次",
            OffContent = "只在点「刷新模型」时拉",
            IsOn = target?.AutoFetchModels ?? true
        };
        var headerBox = new TextBox
        {
            Header = "自定义请求头（每行「名字: 值」）",
            PlaceholderText = "x-opencode-session: abc123",
            AcceptsReturn = true,
            Height = 90,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Text = target?.CustomHeaders ?? ""
        };
        var headerHint = new TextBlock { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        void UpdateHeaderHint()
        {
            var problems = CustomHeaders.DescribeProblems(headerBox.Text);
            headerHint.Text = problems.Length == 0
                ? "有些网关要求专属请求头才肯服务；没有就留空。"
                : "⚠ " + problems;
        }
        UpdateHeaderHint();
        headerBox.TextChanged += (_, _) => UpdateHeaderHint();

        var keyHint = new TextBlock { Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var error = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            Visibility = Visibility.Collapsed
        };

        var panel = new StackPanel { Spacing = 10, MinWidth = 460 };
        panel.Children.Add(error);
        panel.Children.Add(nameBox);
        panel.Children.Add(urlBox);
        panel.Children.Add(keyBox);

        // "清除密钥"只在已经有密钥时出现。它必须和"留空 = 不改动"共存：
        // 没有这个按钮，用户就永远删不掉一个填错的密钥。
        var clearKey = false;
        if (!isNew && !string.IsNullOrEmpty(target!.ApiKeyProtected))
        {
            keyHint.Text = target.ApiKeyUnreadable
                ? "⚠ 已存的密钥在这台机器/账户上解不开（换过机器？）。请重新填写。"
                : "";
            var clear = new Button { Content = "清除已存密钥" };
            clear.Click += (_, _) =>
            {
                clearKey = true;
                keyBox.Password = "";
                keyHint.Text = "保存后这个站点的密钥会被清除。";
            };
            panel.Children.Add(clear);
        }

        panel.Children.Add(keyHint);
        panel.Children.Add(enabledSwitch);
        panel.Children.Add(autoSwitch);
        panel.Children.Add(headerBox);
        panel.Children.Add(headerHint);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = isNew ? "新增站点" : "编辑站点",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520 },
            PrimaryButtonText = "保存",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary
        };

        var saved = false;
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var url = (urlBox.Text ?? "").Trim();
            if (url.Length == 0)
            {
                // 站点就是由它的地址定义的：没有地址的站点永远连不上，
                // 而留着它只会让"为什么模型表是空的"变成一个谜。
                error.Text = "Base URL 不能为空。";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            var typed = keyBox.Password;
            if (isNew)
            {
                var fresh = new ProviderProfile
                {
                    Name = (nameBox.Text ?? "").Trim(),
                    BaseUrl = url,
                    Enabled = enabledSwitch.IsOn,
                    AutoFetchModels = autoSwitch.IsOn,
                    CustomHeaders = headerBox.Text ?? ""
                };
                if (!string.IsNullOrEmpty(typed)) fresh.ApiKey = typed;
                fresh.Normalize();
                _cfg.Providers.Add(fresh);
                if (string.IsNullOrWhiteSpace(_cfg.DefaultProviderId)) _cfg.DefaultProviderId = fresh.Id;
            }
            else
            {
                var t = _cfg.ProviderById(target!.Id);
                if (t is null) return;
                // 只改"界面上有输入框的那几项"。**整对象替换会把模型表一起覆盖掉**——
                // 那张表是按需拉取随时在更新的东西，改个地址不该顺手把它回滚。
                t.Name = (nameBox.Text ?? "").Trim();
                t.BaseUrl = url;
                t.Enabled = enabledSwitch.IsOn;
                t.AutoFetchModels = autoSwitch.IsOn;
                t.CustomHeaders = headerBox.Text ?? "";
                if (!string.IsNullOrEmpty(typed)) t.ApiKey = typed;
                else if (clearKey) t.ApiKey = "";
                t.Normalize();
            }

            _cfg.Save();
            saved = true;
        };

        await dialog.ShowAsync();

        if (!saved) return;
        DeferRefreshLists();
        ProxyLog.Write(isNew ? "新增了模型提供商站点" : $"更新了站点 {nameBox.Text}");
    }

    /// <summary>拉取启用中站点的模型表。这是"维护一个模型列表"的入口。</summary>
    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        SaveFromUi();

        var targets = _cfg.Providers
            .Where(p => p.Enabled && !string.IsNullOrWhiteSpace(p.BaseUrl))
            .ToList();
        if (targets.Count == 0)
        {
            ProviderStatusText.Text = "没有启用中的站点，或站点还没填地址。";
            return;
        }

        RefreshModelsButton.IsEnabled = false;
        ProviderStatusText.Text = "正在拉取模型……";
        try
        {
            using var client = new UpstreamClient(_cfg);
            var results = await Task.WhenAll(targets.Select(async p =>
            {
                var (ok, _, ids, _, error) = await client.FetchModelsAsync(p, CancellationToken.None);
                return (Provider: p, Ok: ok, Ids: ids, Error: error);
            }));

            var lines = new List<string>();
            foreach (var r in results)
            {
                if (!r.Ok)
                {
                    lines.Add($"{r.Provider.DisplayName}：失败（{r.Error}）");
                    continue;
                }
                r.Provider.ApplyFetchedModels(r.Ids);
                lines.Add($"{r.Provider.DisplayName}：{r.Ids.Count} 个模型");
            }

            _cfg.Save();
            RefreshProviderList();
            RefreshModelList();
            ProviderStatusText.Text = string.Join("；", lines);
            foreach (var line in lines) ProxyLog.Write("刷新模型 —— " + line);
        }
        catch (Exception ex)
        {
            ProxyLog.WriteException("刷新模型失败", ex);
            ProviderStatusText.Text = "刷新失败：" + ex.Message;
        }
        finally
        {
            RefreshModelsButton.IsEnabled = true;
        }
    }

    // ---------------- 模型列表 ----------------

    private void ModelEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || _rebuilding) return;
        if (sender is not CheckBox box || box.DataContext is not ModelRow row) return;

        var p = _cfg.ProviderById(row.ProviderId);
        var m = p?.FindModel(row.ModelId);
        if (p is null || m is null) return;

        var on = box.IsChecked == true;
        if (m.Enabled == on) return;

        m.Enabled = on;
        row.Enabled = on;
        _cfg.Save();
        DeferRefreshLists();
        ProxyLog.Write($"模型 {m.Id}（{p.DisplayName}）已{(on ? "对外提供" : "不再对外提供")}");
    }

    private void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ModelRow row) return;

        var p = _cfg.ProviderById(row.ProviderId);
        var m = p?.FindModel(row.ModelId);
        if (p is null || m is null) return;

        p.Models.Remove(m);
        _cfg.Save();
        DeferRefreshLists();
        ProxyLog.Write($"从 {p.DisplayName} 移除了模型 {m.Id}");
    }

    private void AddModel_Click(object sender, RoutedEventArgs e)
    {
        var id = (NewModelBox.Text ?? "").Trim();
        if (id.Length == 0) return;

        // 加进列表里选中的那个站点；没选就加进默认站点。
        var selected = (ProviderList.SelectedItem as ProviderRow)?.Id;
        var target = (selected is null ? null : _cfg.ProviderById(selected)) ?? _cfg.DefaultProvider;
        if (target is null)
        {
            ModelAddHint.Text = "还没有可用的站点，先新增一个。";
            return;
        }
        if (target.FindModel(id) is not null)
        {
            ModelAddHint.Text = $"{target.DisplayName} 已经有「{id}」了。";
            return;
        }

        target.Models.Add(new ModelEntry { Id = id, Enabled = true, Manual = true });
        _cfg.Save();
        NewModelBox.Text = "";
        ModelAddHint.Text = $"已加进 {target.DisplayName}。手工条目在刷新模型时不会被删掉。";
        DeferRefreshLists();
        ProxyLog.Write($"{target.DisplayName}：手工添加模型 {id}");
    }

    // ---------------- 令牌 ----------------

    private void RefreshTokenList()
    {
        _tokenRows.Clear();
        foreach (var t in _tokens.Tokens)
            _tokenRows.Add(new TokenRow
            {
                Id = t.Id,
                Title = $"{t.Name}　{t.Prefix}…",
                Subtitle = $"创建 {t.CreatedAt:yyyy-MM-dd HH:mm}　" +
                           $"用过 {t.UseCount} 次" +
                           (t.LastUsedAt is { } last ? $"　最近 {last:yyyy-MM-dd HH:mm}" : "　从未用过") +
                           (string.IsNullOrWhiteSpace(t.Note) ? "" : $"　备注：{t.Note}"),
                Enabled = t.Enabled
            });

        RefreshTokenSummary();
    }

    private void RefreshTokenSummary()
    {
        var n = _tokens.EnabledCount;
        TokenSummaryText.Text = n == 0
            ? "⚠ 没有启用中的令牌，代理会拒绝所有请求。请先签发一枚。"
            : $"共 {_tokens.Tokens.Count} 枚，启用 {n} 枚。令牌明文只在签发时显示一次。";
    }

    private async void CreateToken_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenStore.Create(NewTokenNameBox.Text, NewTokenNoteBox.Text, out var plaintext);
        _tokens.Tokens.Add(token);
        _tokens.Save();

        NewTokenNameBox.Text = "";
        NewTokenNoteBox.Text = "";
        RefreshTokenList();

        ProxyLog.Write($"签发了新令牌：{token.Name}（{token.Prefix}…）");

        // 明文只在这里出现一次。**用可选择的文本框而不是纯文本**：
        // 用户要把它粘到平台配置里，不能复制的话这个对话框就毫无意义。
        var box = new TextBox
        {
            Text = plaintext,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas")
        };
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = "填进 agent 平台的 API Key。只显示这一次，库里存的是哈希。",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(box);

        var copy = new Button { Content = "复制到剪贴板" };
        copy.Click += (_, _) =>
        {
            var dp = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dp.SetText(plaintext);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dp);
            copy.Content = "已复制";
        };
        panel.Children.Add(copy);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "新令牌",
            Content = panel,
            CloseButtonText = "关闭"
        };
        await dialog.ShowAsync();
    }

    private void TokenToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not ToggleSwitch ts || ts.DataContext is not TokenRow row) return;

        var target = _tokens.Tokens.FirstOrDefault(t => t.Id == row.Id);
        if (target is null) return;

        // ToggleSwitch 没有 Click 事件，Toggled **在程序化改值时也会触发**（列表重建时
        // ListView 复用行容器，控件会按新行的值重设 IsOn）。值没变就说明不是用户在操作，
        // 直接返回：否则重建一次列表会刷出一串"已启用/停用"，还会把复用时残留的状态写错行。
        if (target.Enabled == ts.IsOn) return;

        target.Enabled = ts.IsOn;
        row.Enabled = ts.IsOn;
        _tokens.Save();
        RefreshTokenSummary();

        ProxyLog.Write($"令牌 {target.Name}（{target.Prefix}…）已{(ts.IsOn ? "启用" : "停用")}");
    }

    private void DeleteToken_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || b.Tag is not string id) return;

        var target = _tokens.Tokens.FirstOrDefault(t => t.Id == id);
        if (target is null) return;

        _tokens.Tokens.Remove(target);
        _tokens.Save();
        RefreshTokenList();
        ProxyLog.Write($"删除了令牌 {target.Name}（{target.Prefix}…）");
    }

    // ---------------- 词库 ----------------

    private async Task WarmUpVocabAsync()
    {
        await Task.Run(() => _vocab.Get(_cfg));
        DispatcherQueue.TryEnqueue(RefreshVocabStatus);
    }

    private void RefreshVocabStatus()
    {
        VocabStatusText.Text = _vocab.IsLoaded
            ? "✓ " + _vocab.Status()
            : "✗ " + _vocab.Status();
    }

    private async void ReloadVocab_Click(object sender, RoutedEventArgs e)
    {
        SaveFromUi();
        _vocab.Reset();
        RefreshVocabStatus();
        ReloadVocabButton.IsEnabled = false;
        try
        {
            await Task.Run(() => _vocab.Get(_cfg));
            RefreshVocabStatus();
        }
        finally
        {
            ReloadVocabButton.IsEnabled = true;
        }
    }

    // ---------------- 日志 ----------------

    /// <summary>
    /// <see cref="ProxyLog.Written"/> 是从**任意线程**（Kestrel 的请求线程、
    /// 上游回调）触发的，而 WinUI 的控件只能在自己的线程上碰。
    /// 直接在这里改 ObservableCollection 会在负载一上来就抛
    /// "The application called an interface that was marshalled for a different thread"。
    /// </summary>
    private void OnLogWritten(string line) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            _logLines.Add(line);
            // 面板只留最近若干行：日志是持续的，无限追加会让界面越来越慢，
            // 直到看起来像卡死。落盘那份由 ProxyLog 自己轮转。
            while (_logLines.Count > 500) _logLines.RemoveAt(0);
            if (_logLines.Count > 0) LogList.ScrollIntoView(_logLines[^1]);
        });

    /// <summary>
    /// 按需拉取改了模型表 → 重建界面列表。事件来自 Kestrel 的请求线程，
    /// 所以交给 <see cref="DeferRefreshLists"/>（它自己会 marshal 到 UI 线程）。
    /// </summary>
    private void OnModelsChanged() => DeferRefreshLists();

    // ---------------- 联网搜索 ----------------

    private void SelectProvider(string key)
    {
        for (var i = 0; i < SearchProviderBox.Items.Count; i++)
        {
            if (SearchProviderBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, key, StringComparison.Ordinal))
            {
                SearchProviderBox.SelectedIndex = i;
                return;
            }
        }
        SearchProviderBox.SelectedIndex = 0;   // （未选择）
    }

    private string SelectedProvider =>
        (SearchProviderBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void SearchProvider_Changed(object sender, SelectionChangedEventArgs e) => RefreshSearchStatus();

    private void SearchSetting_Changed(object sender, RoutedEventArgs e) => RefreshSearchStatus();

    private void SearchSetting_TextChanged(object sender, TextChangedEventArgs e) => RefreshSearchStatus();

    /// <summary>
    /// 搜索面板的实时状态。**用 <see cref="ProxyConfig.ProblemFor"/> 而不是自己判**：
    /// 界面说"已就绪"而实际没下发工具（或反过来）都不会有任何症状，
    /// 两份判据一旦走偏就只能靠人读代码发现。
    /// </summary>
    private void RefreshSearchStatus()
    {
        var provider = SelectedProvider;
        var needsKey = ProxyConfig.NeedsKeyFor(provider);
        var needsUrl = ProxyConfig.NeedsEndpointFor(provider);
        var typed = SearchKeyBox.Password;
        var hasKey = typed.Length > 0 || _cfg.SearchApiKey.Trim().Length > 0;
        var unreadable = typed.Length == 0 && _cfg.SearchApiKeyUnreadable;

        // 只让当前后端用得上的那两项可编辑：留着别的框能打字，用户会以为填了有用。
        SearchBaseUrlBox.IsEnabled = needsUrl;
        SearchKeyBox.IsEnabled = needsKey;
        ClearSearchKeyButton.Visibility = string.IsNullOrEmpty(_cfg.SearchApiKeyProtected)
            ? Visibility.Collapsed
            : Visibility.Visible;

        SearchKeyBox.PlaceholderText = !needsKey
            ? "这个后端不需要密钥"
            : string.IsNullOrEmpty(_cfg.SearchApiKeyProtected)
                ? "留空表示不设密钥"
                : unreadable ? "已存的密钥解不开，请重新填写" : "留空表示不改动";

        var hint = provider switch
        {
            "searxng" => "填实例地址。实例要在 settings.yml 的 search.formats 里开启 json，否则接口返回 403。",
            "shim" => "填网关地址与密钥。TLS SNI / Host 留空时用地址里的主机名。",
            "tavily" or "brave" or "serper" => "只需填 API Key，地址用官方域名。",
            _ => "选一个后端，再填它需要的那几项。"
        };

        var problem = ProxyConfig.ProblemFor(SearchEnabledSwitch.IsOn, provider, hasKey,
                                             unreadable, SearchBaseUrlBox.Text);
        var state = !SearchEnabledSwitch.IsOn
            ? "未启用：模型看不到这个工具。"
            : problem.Length > 0
                ? "⚠ " + problem
                : "已就绪：模型可以调用 " + ToolCatalog.WebSearchName + "。";

        SearchStatusText.Text = hint + "\n" + state;
    }

    private async void ClearSearchKey_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("清除搜索密钥",
                "已存的搜索密钥会被删除，之后需要重新填写才能使用联网搜索。", "清除"))
            return;

        _cfg.SearchApiKey = "";
        _cfg.Save();
        SearchKeyBox.Password = "";
        RefreshSearchStatus();
        ProxyLog.Write("搜索密钥已清除");
    }

    /// <summary>
    /// 用**界面上当前的值**试一次搜索，而不是用已保存的配置。
    ///
    /// 用户点"测试"的时机就是他刚填完还没保存的时候；拿盘上的旧配置去测，
    /// 测的是上一次的设置，而结果会被当成"我这次填的对不对"——那比不测更误导。
    /// </summary>
    private async void TestSearch_Click(object sender, RoutedEventArgs e)
    {
        var probe = BuildSearchProbe();
        if (!probe.SearchReady)
        {
            SearchStatusText.Text = "还没配好：" + probe.SearchProblem;
            return;
        }

        TestSearchButton.IsEnabled = false;
        SearchStatusText.Text = "正在测试…";
        try
        {
            using var client = new SearchClient();
            var (ok, message) = await client.TestAsync(probe);
            SearchStatusText.Text = (ok ? "✓ " : "✗ ") + message;
            ProxyLog.Write($"搜索自测（{probe.SearchProviderKey}）：" + message);
        }
        catch (Exception ex)
        {
            SearchStatusText.Text = "✗ 测试失败：" + ex.Message;
            ProxyLog.WriteException("搜索自测失败", ex);
        }
        finally
        {
            TestSearchButton.IsEnabled = true;
        }
    }

    /// <summary>把界面上当前填的搜索设置做成一份配置（不落盘、不动真配置）。</summary>
    private ProxyConfig BuildSearchProbe()
    {
        var probe = new ProxyConfig
        {
            SearchEnabled = SearchEnabledSwitch.IsOn,
            SearchProvider = SelectedProvider,
            SearchBaseUrl = (SearchBaseUrlBox.Text ?? "").Trim(),
            SearchSniHost = (SearchSniBox.Text ?? "").Trim(),
            SearchMaxResults = ToInt(SearchMaxResultsBox.Value, _cfg.SearchMaxResults),
            SearchTimeoutSeconds = ToInt(SearchTimeoutBox.Value, _cfg.SearchTimeoutSeconds)
        };

        var typed = SearchKeyBox.Password;
        if (typed.Length > 0) probe.SearchApiKey = typed;
        else probe.SearchApiKeyProtected = _cfg.SearchApiKeyProtected;   // 用已存的那把

        probe.Normalize();
        return probe;
    }

    // ---------------- 联网抓取 ----------------

    private string SelectedFetchEngine =>
        (FetchEngineBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";

    private void SelectFetchEngine(string engine)
    {
        for (var i = 0; i < FetchEngineBox.Items.Count; i++)
        {
            if (FetchEngineBox.Items[i] is ComboBoxItem item &&
                string.Equals(item.Tag as string, engine, StringComparison.Ordinal))
            {
                FetchEngineBox.SelectedIndex = i;
                return;
            }
        }
        FetchEngineBox.SelectedIndex = 0;   // auto
    }

    private void FetchEngine_Changed(object sender, SelectionChangedEventArgs e) => RefreshFetchStatus();

    private void FetchSetting_Changed(object sender, RoutedEventArgs e) => RefreshFetchStatus();

    private void FetchSetting_TextChanged(object sender, TextChangedEventArgs e) => RefreshFetchStatus();

    /// <summary>
    /// 抓取面板的实时状态。
    ///
    /// 浏览器是否可用**在界面这一层独立探测一次**（<see cref="BrowserFetcher.FindChromium"/> 是纯函数），
    /// 而不是去问服务端：服务端是懒启动的，用户打开界面时它可能还没建出来，
    /// 而"还没建出来"与"本机没有浏览器"在界面上必须长得不一样 ——
    /// 否则用户会以为自己没装 Edge。
    ///
    /// 判据本身用 <see cref="ProxyConfig.FetchProblemFor"/>，与下发工具用的是同一份，
    /// 理由与搜索面板相同：两份判据一旦走偏，症状是"界面说就绪、实际没下发工具"。
    /// </summary>
    private void RefreshFetchStatus()
    {
        var engine = SelectedFetchEngine;
        var path = BrowserFetcher.FindChromium((FetchBrowserPathBox.Text ?? "").Trim());
        var usable = path.Length > 0;

        FetchBrowserPathBox.PlaceholderText = usable
            ? "已找到：" + path
            : "留空 = 自动查找 Edge / Chrome（本机没找到）";

        var hint = engine switch
        {
            "auto" => "先用 HTTP 读（约 0.3 秒）；正文读不到时才自动起浏览器渲染" +
                      "（约 1–2 秒、几百 MB 内存，空闲后自动关闭）。",
            "http" => "只用 HTTP：最轻，但读不了需要 JavaScript 的页面。",
            "browser" => "每次都渲染：最接近真实浏览器，但每次都要起一遍浏览器。",
            _ => ""
        };

        var problem = ProxyConfig.FetchProblemFor(FetchEnabledSwitch.IsOn, engine, usable);
        var state = !FetchEnabledSwitch.IsOn
            ? "未启用：模型看不到这个工具。"
            : problem.Length > 0
                ? "⚠ " + problem
                : "已就绪：模型可以调用 " + ToolCatalog.WebFetchName + "。";

        var browser = usable
            ? "浏览器引擎：" + Path.GetFileName(path)
            : "浏览器引擎：本机没找到 Edge / Chrome（自动降级会退化成只用 HTTP）";

        // 清理设置也要说出来：这一项直接决定浏览器（几百 MB 内存）和临时目录活多久，
        // 而"永不清理"这个档位会一直占着内存 —— 不说明的话用户不会知道代价。
        var cleanup = CleanupSchedule.Describe(SelectedCleanupSeconds);

        FetchStatusText.Text = hint + "\n" + browser + "\n" + cleanup + "\n" + state;
    }

    // ---------------- 临时目录清理时间 ----------------

    /// <summary>把 Core 的档位表填进两个下拉框。只在窗口构造时做一次。</summary>
    private void InitCleanupOptions()
    {
        FetchCleanupBox.Items.Clear();
        foreach (var seconds in CleanupSchedule.Presets)
            FetchCleanupBox.Items.Add(new ComboBoxItem
            {
                Content = CleanupSchedule.LabelFor(seconds),
                Tag = seconds.ToString()
            });
        FetchCleanupBox.Items.Add(new ComboBoxItem { Content = "自定义…", Tag = "custom" });

        FetchCleanupUnitBox.Items.Clear();
        foreach (var unit in CleanupSchedule.Units)
            FetchCleanupUnitBox.Items.Add(new ComboBoxItem
            {
                Content = unit.Label,
                Tag = unit.SecondsPerUnit.ToString()
            });
    }

    private bool CleanupCustomSelected =>
        (FetchCleanupBox.SelectedItem as ComboBoxItem)?.Tag as string == "custom";

    private static int TagSeconds(object? item, int fallback) =>
        (item as ComboBoxItem)?.Tag is string s && int.TryParse(s, out var v) ? v : fallback;

    /// <summary>界面上当前选中的清理时间（秒）。**唯一入口** —— 保存与探针都走它。</summary>
    private int SelectedCleanupSeconds => CleanupCustomSelected
        ? CleanupSchedule.FromCustom(ToInt(FetchCleanupValueBox.Value, 1),
                                     TagSeconds(FetchCleanupUnitBox.SelectedItem, 60))
        : TagSeconds(FetchCleanupBox.SelectedItem, _cfg.FetchCleanupSeconds);

    /// <summary>把配置里的秒数回填到界面上：命中档位就选它，否则选"自定义"并填好数字与单位。</summary>
    private void SelectCleanup(int seconds)
    {
        var presetIndex = -1;
        for (var i = 0; i < CleanupSchedule.Presets.Count; i++)
        {
            if (CleanupSchedule.Presets[i] != seconds) continue;
            presetIndex = i;
            break;
        }

        if (presetIndex < 0)
        {
            CleanupSchedule.SplitForCustom(seconds, out var value, out var unitSeconds);
            FetchCleanupValueBox.Value = value;
            for (var i = 0; i < FetchCleanupUnitBox.Items.Count; i++)
            {
                if (TagSeconds(FetchCleanupUnitBox.Items[i], 0) != unitSeconds) continue;
                FetchCleanupUnitBox.SelectedIndex = i;
                break;
            }
        }

        FetchCleanupBox.SelectedIndex =
            presetIndex >= 0 ? presetIndex : FetchCleanupBox.Items.Count - 1;   // 自定义…
        UpdateCleanupCustomVisibility();
    }

    private void UpdateCleanupCustomVisibility() =>
        FetchCleanupCustomRow.Visibility = CleanupCustomSelected
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void FetchCleanup_Changed(object sender, SelectionChangedEventArgs e)
    {
        UpdateCleanupCustomVisibility();
        RefreshFetchStatus();
    }

    private void FetchCleanupValue_Changed(NumberBox sender, NumberBoxValueChangedEventArgs args) =>
        RefreshFetchStatus();

    /// <summary>
    /// 立刻清理临时目录。
    ///
    /// 优先走服务端持有的那个实例：它会**先关掉空闲的浏览器**，那个目录才会从
    /// "正在用"变成"可清理"。服务端还没建出来（或已停）时退化成纯扫描 ——
    /// 这个按钮在任何状态下都该能用。
    /// </summary>
    private void CleanupFetch_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var report = _server?.CleanupTempProfiles() ?? BrowserFetcher.CleanupLeftovers();
            var text = report.RemovedDirs == 0
                ? "没有发现可清理的临时目录。"
                : $"已清理 {report.RemovedDirs} 个临时目录，释放 {SizeText(report.FreedBytes)}。";
            if (report.BrowserStopped) text += " 浏览器引擎已关闭。";
            if (report.Busy) text += " 有抓取正在进行，当前实例的临时目录没有动。";

            FetchCleanupResultText.Text = text;
            ProxyLog.Write("手动清理浏览器临时目录：" + text);
        }
        catch (Exception ex)
        {
            FetchCleanupResultText.Text = "清理失败：" + ex.Message;
            ProxyLog.WriteException("手动清理浏览器临时目录失败", ex);
        }
    }

    /// <summary>
    /// 体积说人话。**不能一律按 MB 显示**：一次真实抓取是 13–55 MB，但手工造的小目录
    /// 会显示成"释放 0.0 MB"—— 看着像什么都没清掉，而实际上清掉了。
    /// </summary>
    private static string SizeText(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        >= 1024 => $"{bytes / 1024.0:F0} KB",
        > 0 => $"{bytes} 字节",
        _ => "0 字节",
    };

    /// <summary>
    /// 用**界面上当前的值**试抓一次，而不是用已保存的配置 —— 理由与"测试搜索"相同：
    /// 用户点测试的时机就是刚填完还没保存的时候。
    /// </summary>
    private async void TestFetch_Click(object sender, RoutedEventArgs e)
    {
        var url = (FetchTestUrlBox.Text ?? "").Trim();
        if (url.Length == 0)
        {
            FetchStatusText.Text = "先填一个测试地址。";
            return;
        }

        TestFetchButton.IsEnabled = false;
        FetchStatusText.Text = "正在抓取…";
        try
        {
            using var reader = new PageReader();
            var (ok, message) = await reader.TestAsync(BuildFetchProbe(), url);
            FetchStatusText.Text = (ok ? "✓ " : "✗ ") + message;
            ProxyLog.Write("抓取自测：" + message);
        }
        catch (Exception ex)
        {
            FetchStatusText.Text = "✗ 测试失败：" + ex.Message;
            ProxyLog.WriteException("抓取自测失败", ex);
        }
        finally
        {
            TestFetchButton.IsEnabled = true;
        }
    }

    /// <summary>把界面上当前填的抓取设置做成一份配置（不落盘、不动真配置）。</summary>
    private ProxyConfig BuildFetchProbe()
    {
        var path = (FetchBrowserPathBox.Text ?? "").Trim();
        var probe = new ProxyConfig
        {
            FetchEnabled = FetchEnabledSwitch.IsOn,
            FetchEngine = SelectedFetchEngine,
            FetchProxy = (FetchProxyBox.Text ?? "").Trim(),
            FetchBrowserPath = path,
            FetchMaxChars = ToInt(FetchMaxCharsBox.Value, _cfg.FetchMaxChars),
            FetchTimeoutSeconds = ToInt(FetchTimeoutBox.Value, _cfg.FetchTimeoutSeconds),
            FetchCleanupSeconds = SelectedCleanupSeconds,
            FetchAllowPrivateHosts = FetchPrivateSwitch.IsOn,
            // 判据要能算出 FetchReady，所以这里把"本机有没有浏览器"一并填上
            FetchBrowserUsable = BrowserFetcher.FindChromium(path).Length > 0
        };
        probe.Normalize();
        return probe;
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _logLines.Clear();
    private void OpenLogFile_Click(object sender, RoutedEventArgs e) => Open(ProxyLog.FilePath);

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e) => Open(ProxyConfig.Dir);

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ProxyLog.Write("打不开 " + path + "：" + ex.Message);
        }
    }

    // ---------------- 托盘 ----------------

    /// <summary>
    /// 托盘菜单与左键的接线。**必须在构造函数里做，而且只能用 Command**——原因见
    /// <see cref="RelayCommand"/> 的类注释（默认 PopupMenu 模式只看 Command，
    /// 绑 Click 会让每个菜单项都变成"点得动但什么也不发生"）。
    ///
    /// 不用 XAML 的 <c>x:Bind</c>：<c>MenuFlyout</c> 不在可视树里，而
    /// <c>Window</c> 又不是 <c>FrameworkElement</c>（没有 DataContext），
    /// 两种声明式写法在这里都靠不住。写在代码里既直白又不会随模板改动失效。
    /// </summary>
    private void WireTrayCommands()
    {
        TrayShowItem.Command = new RelayCommand(() => RunTray("显示窗口", ShowWindow));
        TrayStartItem.Command = new RelayCommand(() => RunTray("启动代理", TrayStart));
        TrayStopItem.Command = new RelayCommand(() => RunTray("停止代理", TrayStop));
        TrayExitItem.Command = new RelayCommand(() => RunTray("退出", () => _ = ExitAsync()));

        // 左键点图标 = 把窗口叫回来。
        // **不设的话左键什么都不做**：用户点一下毫无反应，就会判定"托盘是坏的"——
        // 而左键恰恰是托盘图标最常被点的那个键（右键菜单是第二順位）。
        Tray.LeftClickCommand = new RelayCommand(() => RunTray("左键显示窗口", ShowWindow));
    }

    /// <summary>
    /// 托盘动作的统一入口：**先回 UI 线程，再执行，而且一定留一条日志**。
    ///
    /// 两件事都是必需的：
    /// * **回 UI 线程**：这些命令来自 H.NotifyIcon 的 Win32 消息窗口，
    ///   碰 <c>AppWindow</c>、控件、列表都不是从 XAML 线程发的。
    /// * **兜住异常**：Win32 菜单回调里抛出的异常传不回我们的代码，
    ///   症状就是"点了没反应"——而"点了没反应"正是这一整套修复要消灭的东西。
    ///   所以失败必须变成日志里的一行，而不是一片沉默。
    /// </summary>
    private void RunTray(string what, Action action)
    {
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    ProxyLog.Write($"托盘：{what}");
                    action();
                }
                catch (Exception ex)
                {
                    ProxyLog.WriteException($"托盘「{what}」失败", ex);
                }
            }))
        {
            ProxyLog.Write($"托盘：{what} 未能派发到界面线程（窗口可能已销毁）");
        }
    }

    private void TrayStart()
    {
        if (!IsRunning) _ = StartServerAsync();   // 已有的判断放在动作里，不用 CanExecute（见 RelayCommand）
    }

    private void TrayStop()
    {
        if (IsRunning) _ = StopServerAsync();
    }

    /// <summary>
    /// 真正退出（只有托盘菜单里的「退出」会走到这里）。
    ///
    /// 顺序要紧：先把 Kestrel 停干净（释放端口、把在途请求处理完），再退进程。
    /// 直接 Exit 的话端口会处于 TIME_WAIT / 未释放状态，下次启动就报"地址已占用"。
    /// </summary>
    private async Task ExitAsync()
    {
        _exiting = true;   // 让 OnAppWindowClosing 放行
        ProxyLog.Written -= OnLogWritten;
        ProxyServer.ModelsChanged -= OnModelsChanged;

        try
        {
            if (_server is not null) await _server.DisposeAsync();
        }
        catch { /* 退出路径上不抛 */ }

        try { Tray.Dispose(); } catch { }
        Application.Current.Exit();
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
                                    Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_exiting) return;   // 真的退出，放行

        // 取消关闭，改成隐藏。**必须取消**：WinUI 关掉窗口之后那个 Window
        // 对象就废了，再想 Show 出来只能重建整个界面（配置、日志订阅、
        // 托盘图标全部要重做）。
        args.Cancel = true;
        sender.Hide();

        Tray.ShowNotification("AnimaAIProxy 还在后台运行",
            $"代理{(IsRunning ? "仍在" : "未")}运行。要彻底退出，右键托盘图标选「退出」。");
    }

    private bool IsRunning => _server?.IsRunning == true;

    /// <summary>
    /// 把窗口叫回来。
    ///
    /// 三件事都要做，少一件就会出现"点了没反应"：
    /// 1. 隐藏过的窗口要 <c>Show</c>；
    /// 2. **最小化过的窗口 <c>Show</c> 不会还原**，得让 presenter <c>Restore</c>
    ///    （最小化之后点托盘"显示窗口"，窗口会一闪之后仍然缩在任务栏上）；
    /// 3. <c>Activate</c> 才到前台——否则窗口可能出现在别的窗口后面，
    ///    用户看不到，照样认为没反应。
    /// </summary>
    private void ShowWindow()
    {
        if (!AppWindow.IsVisible) AppWindow.Show();
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p &&
            p.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized)
        {
            p.Restore();
        }
        Activate();
    }

    // ---------------- 收尾 ----------------

    private async Task ShowAsync(string title, string message)
    {
        try
        {
            await new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "知道了"
            }.ShowAsync();
        }
        catch { /* 窗口已经关了，弹不出来就算了 */ }
    }

    /// <summary>问一句再做。取消（或窗口已关）返回 false。</summary>
    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = primaryText,
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch
        {
            return false;   // 窗口已经关了：不执行那个不可撤销的动作
        }
    }

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        // 这条路只在 _exiting 时才会走到（平时 Closing 已经被取消、改成隐藏）。
        // 兜底再停一次：万一哪天有人把 Closing 的处理去掉了，
        // 端口也不至于一直被占着。
        ProxyLog.Written -= OnLogWritten;
        ProxyServer.ModelsChanged -= OnModelsChanged;
        try
        {
            if (_server is not null) await _server.DisposeAsync();
        }
        catch { /* 退出路径上不抛 */ }
    }
}

/// <summary>
/// 令牌列表的一行。<c>x:Bind</c> 要求属性是公开的、类型是公开的。
/// 它是**显示用的投影**，不是数据源——真正的数据在 <see cref="TokenStore"/> 里，
/// 改这里不会自动落盘，落盘由界面的事件处理显式调用 <c>Save()</c>。
/// </summary>
public sealed class TokenRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool Enabled { get; set; }
}

/// <summary>模型提供商列表的一行。同样是显示用的投影，真数据在 <see cref="ProviderProfile"/> 里。</summary>
public sealed class ProviderRow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";

    /// <summary>"默认"标记；不是默认站点时是空串（读屏器不会念出一个空标记）。</summary>
    public string DefaultMark { get; set; } = "";

    public bool Enabled { get; set; }
}

/// <summary>
/// 模型列表的一行。
///
/// 带 <see cref="ProviderId"/> 而不只是模型名：同一个模型名可能同时存在于两个站点
/// （比如两个网关都提供 <c>gpt-4o</c>），只用名字就分不清用户勾的是哪一个，
/// 而勾错的那一个不会有任何症状——只是请求发去了另一个站点。
/// </summary>
public sealed class ModelRow
{
    public string ProviderId { get; set; } = "";
    public string ModelId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public bool Enabled { get; set; }
    public bool Manual { get; set; }

    /// <summary>只有手工添加的条目才给"删除"按钮：拉回来的模型用开关关掉就够了。</summary>
    public Visibility DeleteVisibility =>
        Manual ? Visibility.Visible : Visibility.Collapsed;
}
