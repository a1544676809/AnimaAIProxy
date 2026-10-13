using AnimaAIProxy.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;

namespace AnimaAIProxy;

/// <summary>
/// 主窗口：代理的启停、上游配置、令牌管理、词库状态、实时日志。
///
/// **这里刻意不放任何"聪明"的逻辑。** 全部判定（工具怎么合并、令牌怎么校验、
/// 上游出错回什么状态码）都在 <c>Core/</c> 里，而且都被离线回归钉住了。
/// 界面只做三件事：把配置读进来、把配置写出去、把状态显示出来。
/// 界面里多一条判断，就多一条回归覆盖不到的路径——而"设置界面上的行为"
/// 恰恰是最容易在改别处时被悄悄弄坏的地方。
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly ProxyConfig _cfg;
    private readonly TokenStore _tokens;
    private readonly VocabService _vocab = new();
    private ProxyServer? _server;

    private readonly ObservableCollection<string> _logLines = new();
    private readonly ObservableCollection<TokenRow> _tokenRows = new();

    /// <summary>抑制事件：在代码里回填控件值时，别让 Changed 事件又去写配置。</summary>
    private bool _loading;

    public MainWindow()
    {
        InitializeComponent();

        _cfg = ProxyConfig.Load();
        _tokens = TokenStore.Load();

        LogList.ItemsSource = _logLines;
        TokenList.ItemsSource = _tokenRows;

        // 先把历史日志灌进面板：用户打开界面时想看到"刚才发生了什么"，
        // 而不是从打开这一刻开始的一片空白。
        foreach (var line in ProxyLog.Snapshot()) _logLines.Add(line);
        ProxyLog.Written += OnLogWritten;

        LogPathText.Text = ProxyLog.FilePath;

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
            UpstreamUrlBox.Text = _cfg.UpstreamBaseUrl;
            UpstreamKeyState.Text = _cfg.HasUpstreamKey
                ? "已设置密钥（DPAPI 加密后存在配置里，明文只在内存里）。"
                : "未设置密钥。上游不需要密钥时留空即可。";
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

        _cfg.UpstreamBaseUrl = (UpstreamUrlBox.Text ?? "").Trim();

        // PasswordBox 为空表示"不改动密钥"。这一条很重要：界面上永远不回填明文，
        // 所以"空"必须理解成"保持原样"，否则每次点保存都会把密钥清掉。
        var typedKey = UpstreamKeyBox.Password;
        if (!string.IsNullOrEmpty(typedKey)) _cfg.UpstreamApiKey = typedKey;

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
                ex.Message + "\n\n最常见的原因是端口被占用。换一个端口再试。");
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

        var hint = new StringBuilder();
        if (running && _server is not null)
        {
            hint.Append("接入方看到的 Base URL：").Append(_server.BaseUrl);
            if (_cfg.BindScope == "local")
                hint.Append("（只监听回环，外部机器连不上）");
            else if (_cfg.BindScope == "lan")
                hint.Append("（局域网内可访问，把 127.0.0.1 换成本机 IP）");
            else
                hint.Append("（任意来源都能连，注意这是不安全的）");
        }
        else
        {
            hint.Append("代理没在跑。启动之后把下面那串 Base URL 填进 agent 平台。");
        }

        BaseUrlText.Text = _server is null
            ? $"http://127.0.0.1:{_cfg.Port}/v1"
            : _server.BaseUrl;
        HintText.Text = hint.ToString();
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
        HintText.Text = "已复制：" + BaseUrlText.Text;
    }

    // ---------------- 上游 ----------------

    private async void TestUpstream_Click(object sender, RoutedEventArgs e)
    {
        SaveFromUi();

        UpstreamTestResult.Text = "正在测试……";
        TestUpstreamButton.IsEnabled = false;
        try
        {
            using var client = new UpstreamClient(_cfg);
            var (ok, status, raw, error) = await client.ModelsAsync(CancellationToken.None);

            if (ok)
            {
                var node = ChatWire.TryParseObject(raw);
                var count = (node?["data"] as System.Text.Json.Nodes.JsonArray)?.Count ?? 0;
                UpstreamTestResult.Text = $"通。上游报了 {count} 个模型。";
                ProxyLog.Write($"上游连接测试通过（{count} 个模型）");
            }
            else
            {
                UpstreamTestResult.Text = "不通：" + error;
                ProxyLog.Write("上游连接测试失败：" + error);
            }
        }
        finally
        {
            TestUpstreamButton.IsEnabled = true;
        }
    }

    private void ClearKey_Click(object sender, RoutedEventArgs e)
    {
        _cfg.UpstreamApiKey = "";
        _cfg.Save();
        UpstreamKeyBox.Password = "";
        UpstreamKeyState.Text = "已清除密钥。";
        ProxyLog.Write("上游密钥已清除");
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
            ? "⚠ 一枚启用中的令牌都没有，代理会拒绝所有请求。先签发一枚再填进平台。"
            : $"共 {_tokens.Tokens.Count} 枚，其中 {n} 枚启用中。令牌明文只在签发那一刻显示一次。";
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
            Text = "把下面这串填进 agent 平台的 API Key。它只显示这一次，关掉就再也拿不回来了" +
                   "（库里存的是哈希，不是明文）。",
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

    private void ShowWindow()
    {
        AppWindow.Show();
        Activate();
    }

    private void TrayShow_Click(object sender, RoutedEventArgs e) => ShowWindow();

    private async void TrayStart_Click(object sender, RoutedEventArgs e)
    {
        if (!IsRunning) await StartServerAsync();
    }

    private async void TrayStop_Click(object sender, RoutedEventArgs e)
    {
        if (IsRunning) await StopServerAsync();
    }

    private async void TrayExit_Click(object sender, RoutedEventArgs e)
    {
        _exiting = true;
        ProxyLog.Written -= OnLogWritten;

        // 顺序要紧：先把 Kestrel 停干净（释放端口、把在途请求处理完），
        // 再退进程。直接 Exit 的话端口会处于 TIME_WAIT / 未释放状态，
        // 下次启动就报"地址已占用"。
        try
        {
            if (_server is not null) await _server.DisposeAsync();
        }
        catch { /* 退出路径上不抛 */ }

        try { Tray.Dispose(); } catch { }
        Application.Current.Exit();
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

    private async void OnClosed(object sender, WindowEventArgs args)
    {
        // 这条路只在 _exiting 时才会走到（平时 Closing 已经被取消、改成隐藏）。
        // 兜底再停一次：万一哪天有人把 Closing 的处理去掉了，
        // 端口也不至于一直被占着。
        ProxyLog.Written -= OnLogWritten;
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
