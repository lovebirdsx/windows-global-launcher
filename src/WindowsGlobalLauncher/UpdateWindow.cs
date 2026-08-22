using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace CommandLauncher
{
    /// <summary>
    /// 「发现新版本」提示/下载窗口：深色风格，无系统标题栏，顶部自绘标题区可拖动、可调整大小。
    /// 三个界面状态——提示（初始，展示版本对比与更新日志）、下载中（进度条 + 阶段文本，禁止关闭）、
    /// 失败（红字原因 + 重试/手动下载）。下载成功由 UpdateInstaller 启动新进程后随应用一起退出。
    /// 单实例：ShowFor 已打开时激活既有窗口而非再开一个。
    /// </summary>
    public sealed class UpdateWindow : System.Windows.Window
    {
        // 深色配色（与项目一致）
        private static readonly Color WindowBackground = Color.FromRgb(30, 30, 30);       // #1E1E1E
        private static readonly Color TextBoxBackground = Color.FromRgb(37, 37, 38);       // #252526
        private static readonly Color NormalForeground = Color.FromRgb(224, 224, 224);     // #E0E0E0
        private static readonly Color HintForeground = Color.FromArgb(255, 150, 150, 150); // 占位灰字
        private static readonly Color AccentBlue = Color.FromRgb(0, 120, 212);             // 强调蓝
        private static readonly Color AccentBlueHover = Color.FromRgb(0, 100, 180);
        private static readonly Color ErrorRed = Color.FromRgb(232, 72, 85);               // 警示红（失败原因）

        private const double WindowWidth = 560;
        private const double WindowHeight = 460;
        private const double TitleBarHeight = 34;
        private const double ButtonRowHeight = 52;

        /// <summary>当前打开的更新窗口（单例，Closed 时清空）。仅在 UI 线程读写。</summary>
        private static UpdateWindow? _current;

        private readonly UpdateInfo _info;

        // 三态
        private enum UiState { Prompt, Downloading, Failed }
        private UiState _state;

        // 控件
        private TextBox _releaseNotes = null!;
        private Button _updateButton = null!;       // 「立即更新」/「重试」
        private Button _laterButton = null!;        // 「稍后」
        private Button _skipButton = null!;         // 「跳过此版本」
        private Button _releasePageButton = null!;  // 「查看发布页」/「手动下载」
        private Button _closeButton = null!;        // 失败态「关闭」
        private Button _closeTitleButton = null!;   // 标题栏 ✕
        private Grid _buttonRow = null!;
        private StackPanel _progressPanel = null!;
        private ProgressBar _progressBar = null!;
        private TextBlock _statusText = null!;
        private TextBlock _progressText = null!;

        private bool _eventsSubscribed;  // 是否已订阅 UpdateInstaller 事件（防重复订阅）
        private bool _allowClose;        // 更新成功启动新进程后置 true，允许随应用关闭退出
        private bool _openLogged;        // 窗口打开日志只记一条（Loaded 在每次 Show 时都会触发）

        public UpdateWindow(UpdateInfo info)
        {
            _info = info;
            InitializeComponent();
            EnterPrompt();
            Loaded += OnLoaded;
        }

        /// <summary>显示更新提示窗。同一时刻只保留一个实例，已打开则激活既有窗口而不是再开一个。</summary>
        public static void ShowFor(UpdateInfo info)
        {
            if (_current != null)
            {
                _current.ActivateWindow();
                return;
            }

            var window = new UpdateWindow(info);
            _current = window;
            window.Show();
        }

        /// <summary>
        /// 应用级退出前调用：放行本窗口的关闭拦截。
        /// <para>
        /// <see cref="OnClosing"/> 在下载态会 <c>e.Cancel = true</c>，它拦的是「用户误关窗口」，
        /// 但同一条路径也会把 <c>Application.Shutdown()</c> 挡下来。而托盘「退出」是先 Dispose 主窗口
        /// （移除托盘图标、注销热键与钩子）再 Shutdown 的，Shutdown 一旦被取消，程序就停在
        /// 「没有托盘图标、没有热键、也退不掉」的半死状态——用户再没有任何入口。
        /// 故应用级退出必须先经此放行；仅在 UI 线程调用。
        /// </para>
        /// </summary>
        public static void PrepareForApplicationShutdown()
        {
            if (_current != null)
                _current._allowClose = true;
        }

        private void InitializeComponent()
        {
            Title = "发现新版本";
            Width = WindowWidth;
            Height = WindowHeight;
            MinWidth = 420;
            MinHeight = 360;
            WindowStyle = WindowStyle.None;                 // 无系统标题栏
            ResizeMode = ResizeMode.CanResize;               // 允许调整大小（纯边缘拖拽，去掉右下角亮色 grip 角标）
            Background = new SolidColorBrush(WindowBackground); // 不透明背景，保证 WindowStyle.None 下可拖拽调整
            Topmost = true;                                  // 保证用户看得见
            ShowInTaskbar = true;                            // 用户可见对话框，需出现在任务栏（区别于 OCR 结果窗）
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // 用 WindowChrome 取代系统默认非客户区：GlassFrameThickness=0 消除 WindowStyle.None 下
            // 顶部残留的白色亮线（系统 resize 边框），ResizeBorderThickness=6 保留边缘拖拽调整大小
            // （该区域不可见、仅做命中测试），CaptionHeight=0 不产生系统标题命中区，CornerRadius=0 直角。
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                GlassFrameThickness = new Thickness(0),
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            // ===== 标题栏：左侧文字（可拖动），右侧 ✕ 关闭，底部细分隔线 =====
            var titleBar = new Grid { Height = TitleBarHeight, Background = Brushes.Transparent };
            titleBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            titleBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            titleBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 拖动区放在关闭按钮之外的一列，避免点击关闭按钮时误触发 DragMove
            var dragArea = new Border { Background = Brushes.Transparent };
            Grid.SetRow(dragArea, 0);
            Grid.SetColumn(dragArea, 0);
            dragArea.MouseLeftButtonDown += OnTitleBarMouseLeftButtonDown;

            var titleText = new TextBlock
            {
                Text = "发现新版本",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 190)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(14, 0, 0, 0),
            };
            dragArea.Child = titleText;
            titleBar.Children.Add(dragArea);

            _closeTitleButton = new Button
            {
                Content = "✕",
                Style = MakeTitleBarCloseButtonStyle(),
                Width = 40,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            _closeTitleButton.Click += (s, e) => Close();
            Grid.SetRow(_closeTitleButton, 0);
            Grid.SetColumn(_closeTitleButton, 1);
            titleBar.Children.Add(_closeTitleButton);

            var titleSeparator = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(titleSeparator, 1);
            Grid.SetColumnSpan(titleSeparator, 2);
            titleBar.Children.Add(titleSeparator);

            // ===== 头部：版本对比 + 更新包大小 =====
            var headerPanel = new StackPanel { Margin = new Thickness(14, 10, 14, 0) };

            var versionLine = new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
            };
            versionLine.Inlines.Add(new Run("当前版本 ") { Foreground = new SolidColorBrush(HintForeground) });
            versionLine.Inlines.Add(new Run($"v{App.AppVersionString}") { Foreground = new SolidColorBrush(NormalForeground) });
            versionLine.Inlines.Add(new Run("  →  新版本 ") { Foreground = new SolidColorBrush(HintForeground) });
            versionLine.Inlines.Add(new Run(_info.TagName) { Foreground = new SolidColorBrush(AccentBlue), FontWeight = FontWeights.SemiBold });
            headerPanel.Children.Add(versionLine);

            if (_info.AssetSize > 0)
            {
                headerPanel.Children.Add(new TextBlock
                {
                    Text = $"更新包大小：{FormatMb(_info.AssetSize)}",
                    FontSize = 11,
                    Foreground = new SolidColorBrush(HintForeground),
                    Margin = new Thickness(0, 3, 0, 0),
                });
            }

            // ===== 更新日志（主体区域，只读多行深色文本框） =====
            _releaseNotes = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalContentAlignment = VerticalAlignment.Top,
                AcceptsReturn = true,
                Background = new SolidColorBrush(TextBoxBackground),
                Foreground = new SolidColorBrush(NormalForeground),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                FontSize = 13,
                Margin = new Thickness(12, 8, 12, 8),
            };
            _releaseNotes.Text = string.IsNullOrWhiteSpace(_info.ReleaseNotes)
                ? "（本次更新没有提供更新日志）"
                : _info.ReleaseNotes;
            ApplyFlatScrollBar();

            // ===== 进度区（下载中显示进度条 + 文本，失败时显示红色原因） =====
            _progressPanel = new StackPanel
            {
                Margin = new Thickness(14, 0, 14, 2),
                MinHeight = ButtonRowHeight, // 与按钮行同高，状态切换时窗口不跳动
                Visibility = Visibility.Collapsed,
            };
            _progressBar = new ProgressBar { Height = 6, Minimum = 0, Maximum = 100 };
            _statusText = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(HintForeground),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0),
            };
            _progressText = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(HintForeground),
                Margin = new Thickness(0, 2, 0, 0),
            };
            _progressPanel.Children.Add(_progressBar);
            _progressPanel.Children.Add(_statusText);
            _progressPanel.Children.Add(_progressText);

            // ===== 按钮行 =====
            _updateButton = new Button { Content = "立即更新", Style = MakeButtonStyle(accent: true), MinWidth = 88 };
            _updateButton.Click += OnUpdateClick;

            _laterButton = new Button { Content = "稍后", Style = MakeButtonStyle(accent: false), MinWidth = 72 };
            _laterButton.Click += (s, e) => Close();

            _closeButton = new Button { Content = "关闭", Style = MakeButtonStyle(accent: false), MinWidth = 72, Visibility = Visibility.Collapsed };
            _closeButton.Click += (s, e) => Close();

            _skipButton = new Button { Content = "跳过此版本", Style = MakeLinkButtonStyle() };
            _skipButton.Click += OnSkipClick;

            _releasePageButton = new Button { Content = "查看发布页", Style = MakeLinkButtonStyle(), Margin = new Thickness(14, 0, 0, 0) };
            _releasePageButton.Click += OnOpenReleasePage;

            // 左列放链接样式按钮（跳过/发布页），右列放强调蓝主按钮与次按钮
            var leftPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
            };
            leftPanel.Children.Add(_skipButton);
            leftPanel.Children.Add(_releasePageButton);

            var rightPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            rightPanel.Children.Add(_updateButton);
            rightPanel.Children.Add(_laterButton);
            rightPanel.Children.Add(_closeButton);

            _buttonRow = new Grid { Margin = new Thickness(12, 0, 12, 8) };
            _buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _buttonRow.Children.Add(leftPanel); // 默认第 0 列
            Grid.SetColumn(rightPanel, 1);
            _buttonRow.Children.Add(rightPanel);

            // ===== 五行布局：标题栏 / 头部 / 日志（主体）/ 进度区 / 按钮行 =====
            var grid = new Grid { Margin = new Thickness(1) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ButtonRowHeight) });

            Grid.SetRow(titleBar, 0);
            Grid.SetRow(headerPanel, 1);
            Grid.SetRow(_releaseNotes, 2);
            Grid.SetRow(_progressPanel, 3);
            Grid.SetRow(_buttonRow, 4);

            grid.Children.Add(titleBar);
            grid.Children.Add(headerPanel);
            grid.Children.Add(_releaseNotes);
            grid.Children.Add(_progressPanel);
            grid.Children.Add(_buttonRow);

            // 细边框（参照剪贴板预览窗描边配色），内容承载于外层 Border
            Content = new Border
            {
                Background = new SolidColorBrush(WindowBackground),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = grid,
            };

            PreviewKeyDown += OnPreviewKeyDown;
        }

        // ================= 三态切换 =================

        /// <summary>提示态：展示版本对比与更新日志，三个操作按钮齐全。</summary>
        private void EnterPrompt()
        {
            _state = UiState.Prompt;
            _updateButton.Content = "立即更新";
            _releasePageButton.Content = "查看发布页";
            _updateButton.Visibility = Visibility.Visible;
            _laterButton.Visibility = Visibility.Visible;
            _skipButton.Visibility = Visibility.Visible;
            _releasePageButton.Visibility = Visibility.Visible;
            _closeButton.Visibility = Visibility.Collapsed;
            _buttonRow.Visibility = Visibility.Visible;
            _progressPanel.Visibility = Visibility.Collapsed;
            _closeTitleButton.IsEnabled = true;
        }

        /// <summary>下载态：隐藏按钮行、显示进度区、禁用关闭（订阅下载事件）。</summary>
        private void EnterDownloading()
        {
            _state = UiState.Downloading;
            _updateButton.Visibility = Visibility.Collapsed;
            _laterButton.Visibility = Visibility.Collapsed;
            _skipButton.Visibility = Visibility.Collapsed;
            _releasePageButton.Visibility = Visibility.Collapsed;
            _closeButton.Visibility = Visibility.Collapsed;
            _buttonRow.Visibility = Visibility.Collapsed;
            _progressPanel.Visibility = Visibility.Visible;
            _progressBar.Visibility = Visibility.Visible;
            _progressText.Visibility = Visibility.Visible;
            _statusText.Foreground = new SolidColorBrush(HintForeground);
            _statusText.Text = "正在准备下载…";
            _progressText.Text = "";
            _progressBar.IsIndeterminate = true;
            _closeTitleButton.IsEnabled = false;
            SubscribeEvents();
        }

        /// <summary>失败态：红字显示原因，按钮行变为「重试 / 手动下载 / 关闭」。</summary>
        private void EnterFailed(string reason)
        {
            _state = UiState.Failed;
            _updateButton.Content = "重试";
            _releasePageButton.Content = "手动下载";
            _updateButton.Visibility = Visibility.Visible;
            _laterButton.Visibility = Visibility.Collapsed;
            _skipButton.Visibility = Visibility.Collapsed;
            _releasePageButton.Visibility = Visibility.Visible;
            _closeButton.Visibility = Visibility.Visible;
            _buttonRow.Visibility = Visibility.Visible;
            _progressPanel.Visibility = Visibility.Visible;
            _progressBar.Visibility = Visibility.Collapsed;
            _progressText.Visibility = Visibility.Collapsed;
            _statusText.Foreground = new SolidColorBrush(ErrorRed);
            _statusText.Text = string.IsNullOrWhiteSpace(reason) ? "更新失败" : reason;
            _closeTitleButton.IsEnabled = true;
        }

        // ================= 事件处理 =================

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_openLogged)
            {
                _openLogged = true;
                Logger.LogInfo($"更新窗口打开：{_info.TagName}");
            }
            ActivateWindow();
        }

        private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                // 下载中禁止关闭（Esc 也吞掉），否则用户会误以为更新被取消
                if (_state != UiState.Downloading)
                    Close();
                e.Handled = true;
            }
        }

        private async void OnUpdateClick(object sender, RoutedEventArgs e)
        {
            if (_state == UiState.Downloading)
                return;
            await StartDownloadAsync();
        }

        private void OnSkipClick(object sender, RoutedEventArgs e)
        {
            UpdateChecker.SkipVersion(_info);
            Close();
        }

        private void OnOpenReleasePage(object sender, RoutedEventArgs e)
        {
            try
            {
                // 必须 UseShellExecute=true：.NET Core 下直接 Process.Start(url) 无法用默认浏览器打开
                Process.Start(new ProcessStartInfo(UpdateChecker.ReleasePageUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.LogError("打开发布页失败", ex);
            }
        }

        /// <summary>执行完整下载流程：切下载态 → 调用 installer → 成功随应用退出、失败切失败态。</summary>
        private async Task StartDownloadAsync()
        {
            Logger.LogInfo($"用户触发更新：{_info.TagName}");
            EnterDownloading();
            try
            {
                string? error = await UpdateInstaller.DownloadAndApplyAsync(_info);
                if (error == null)
                {
                    // 成功：新进程已由 installer 启动并等待本进程退出，这里必须立即关闭本进程
                    Logger.LogInfo($"更新成功（{App.AppVersionString} → {_info.Version}），即将退出当前进程");
                    _allowClose = true;
                    System.Windows.Application.Current.Shutdown();
                }
                else
                {
                    EnterFailed(error);
                }
            }
            catch (Exception ex)
            {
                // 防御：installer 内部已兜底返回中文错误，此处仅兜底不可预料的异常
                Logger.LogError("执行更新失败", ex);
                EnterFailed($"更新失败：{ex.Message}");
            }
        }

        // ================= 进度事件订阅/退订 =================

        /// <summary>订阅下载进度事件（幂等，最多订阅一次；OnClosed 时退订防泄漏）。</summary>
        private void SubscribeEvents()
        {
            if (_eventsSubscribed)
                return;
            _eventsSubscribed = true;
            UpdateInstaller.ProgressChanged += OnProgressChanged;
            UpdateInstaller.StatusChanged += OnStatusChanged;
        }

        private void UnsubscribeEvents()
        {
            if (!_eventsSubscribed)
                return;
            _eventsSubscribed = false;
            UpdateInstaller.ProgressChanged -= OnProgressChanged;
            UpdateInstaller.StatusChanged -= OnStatusChanged;
        }

        /// <summary>下载进度回调：事件从后台线程触发，务必切回 UI 线程更新控件。</summary>
        private void OnProgressChanged(long received, long total)
        {
            if (Dispatcher.CheckAccess())
                ApplyProgress(received, total);
            else
                Dispatcher.BeginInvoke(() => ApplyProgress(received, total));
        }

        /// <summary>阶段状态回调：事件从后台线程触发，务必切回 UI 线程更新文本。</summary>
        private void OnStatusChanged(string status)
        {
            if (Dispatcher.CheckAccess())
                _statusText.Text = status;
            else
                Dispatcher.BeginInvoke(() => _statusText.Text = status);
        }

        /// <summary>
        /// 按总字节数刷新进度条与文本：总字节 &gt; 0 显示确定进度（百分比 + x.x / y.y MB），
        /// 未知（-1）转不确定态、只显示已下载大小。
        /// </summary>
        private void ApplyProgress(long received, long total)
        {
            if (total > 0)
            {
                _progressBar.IsIndeterminate = false;
                _progressBar.Maximum = 100;
                double percent = (double)received / total * 100;
                if (percent < 0) percent = 0;
                if (percent > 100) percent = 100;
                _progressBar.Value = percent;
                _progressText.Text = $"{percent:0}%   {FormatMb(received)} / {FormatMb(total)}";
            }
            else
            {
                _progressBar.IsIndeterminate = true;
                _progressText.Text = $"已下载 {FormatMb(received)}";
            }
        }

        // ================= 窗口激活与关闭 =================

        /// <summary>
        /// 兜底激活窗口：本窗口可能由后台检查触发，程序此刻完全处于后台（无前台窗口），
        /// 直接 Show() + Activate() 会被 Windows 前台锁定规则挡下（本项目多处踩过此坑）。
        /// 故取真实窗口句柄后经 WindowEnumerator.Activate 的 AttachThreadInput 技巧绕过锁定。
        /// </summary>
        private void ActivateWindow()
        {
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    WindowEnumerator.Activate(hwnd);
                }
                else
                {
                    Activate();
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"激活更新窗口失败：{ex.Message}");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 下载/安装进行中禁止关闭：此刻后台正替换程序文件并即将启动新进程，中途关窗会让用户
            // 误以为更新被取消（实际新进程仍会启动）。标题栏关闭按钮已禁用，这里再兜底拦截 Alt+F4 等路径。
            // 两种情况需放行：更新成功后由 Application.Shutdown 关闭本窗口退出进程，
            // 以及用户从托盘明确选择退出（见 PrepareForApplicationShutdown）——两者都会先置 _allowClose。
            if (_state == UiState.Downloading && !_allowClose)
                e.Cancel = true;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            // 退订下载进度事件，防窗口关闭后仍被静态事件持有导致泄漏
            UnsubscribeEvents();
            // 清空单例引用，允许下次再开新窗口
            if (ReferenceEquals(_current, this))
                _current = null;
            base.OnClosed(e);
        }

        // ================= 辅助 =================

        /// <summary>字节数格式化为「x.x MB」。</summary>
        private static string FormatMb(long bytes)
        {
            if (bytes < 0)
                return "未知";
            return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
        }

        // ================= 样式 =================
        // 从 OcrResultWindow 照搬（项目风格为各窗口自建样式，不提取公共类）。

        /// <summary>
        /// 按钮深色样式（纯代码模板：Border + ContentPresenter，默认模板不响应 Background 触发器）。
        /// accent = true 时常态即蓝底（「立即更新」/「重试」主按钮），false 为深灰底（「稍后」/「关闭」次按钮），
        /// hover 各自变深/变浅。
        /// </summary>
        private static Style MakeButtonStyle(bool accent)
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.BackgroundProperty,
                accent ? new SolidColorBrush(AccentBlue) : new SolidColorBrush(Color.FromRgb(60, 60, 60)));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new Thickness(16, 6, 16, 6));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                accent ? new SolidColorBrush(AccentBlueHover) : new SolidColorBrush(Color.FromRgb(85, 85, 85)), "border"));
            template.Triggers.Add(hover);

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(TemplateProperty, template));
            style.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(4, 0, 0, 0)));
            return style;
        }

        /// <summary>
        /// 链接样式按钮：透明背景、无边框、蓝色文字、手型光标（hover 变浅蓝、禁用灰化），
        /// 纯 ContentPresenter 模板避开默认按钮的边框/底色。
        /// </summary>
        private static Style MakeLinkButtonStyle()
        {
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Left);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = content };

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(TemplateProperty, template));
            style.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(AccentBlue)));
            style.Setters.Add(new Setter(FontSizeProperty, 11.0));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(AccentBlueHover)));
            style.Triggers.Add(hover);

            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(HintForeground)));
            style.Triggers.Add(disabled);

            return style;
        }

        /// <summary>标题栏关闭按钮样式：透明背景灰字 ✕，hover 变红底白字（关闭语义），禁用灰化。</summary>
        private static Style MakeTitleBarCloseButtonStyle()
        {
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            border.AppendChild(content);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = border };

            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(TemplateProperty, template));
            style.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(200, 200, 200))));
            style.Setters.Add(new Setter(FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(232, 17, 35)), "border"));
            hover.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
            style.Triggers.Add(hover);

            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(90, 90, 90))));
            style.Triggers.Add(disabled);

            return style;
        }

        // 扁平深色滚动条：照搬切换器 SwitcherWindow.ApplyFlatScrollBar 的内联 XAML 模板
        // （隐藏箭头按钮，仅保留细圆角 Thumb），以隐式 ScrollBar 样式注入 TextBox.Resources。
        private void ApplyFlatScrollBar()
        {
            const string xaml = @"
<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation""
       xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml""
       TargetType=""ScrollBar"">
  <Setter Property=""Width"" Value=""8""/>
  <Setter Property=""Background"" Value=""Transparent""/>
  <Setter Property=""Template"">
    <Setter.Value>
      <ControlTemplate TargetType=""ScrollBar"">
        <Grid Background=""Transparent"">
          <Track x:Name=""PART_Track"" IsDirectionReversed=""True"">
            <Track.DecreaseRepeatButton>
              <RepeatButton Command=""{x:Static ScrollBar.PageUpCommand}"" Opacity=""0"" Focusable=""False"" IsTabStop=""False""/>
            </Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton>
              <RepeatButton Command=""{x:Static ScrollBar.PageDownCommand}"" Opacity=""0"" Focusable=""False"" IsTabStop=""False""/>
            </Track.IncreaseRepeatButton>
            <Track.Thumb>
              <Thumb>
                <Thumb.Template>
                  <ControlTemplate TargetType=""Thumb"">
                    <Border CornerRadius=""4"" Background=""#66FFFFFF"" Margin=""2,1,2,1""/>
                  </ControlTemplate>
                </Thumb.Template>
              </Thumb>
            </Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";
            try
            {
                var style = (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                _releaseNotes.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
            }
            catch (Exception ex)
            {
                Logger.LogError("应用更新窗口滚动条样式失败", ex);
            }
        }
    }
}
