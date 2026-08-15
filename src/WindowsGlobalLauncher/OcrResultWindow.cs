using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace CommandLauncher
{
    /// <summary>
    /// 截图 OCR 识别结果弹窗：深色风格，无系统标题栏，顶部自绘标题区可拖动，
    /// 主体为可编辑多行文本框，底部「复制」「关闭」按钮。
    /// 构造后处于「正在识别文字…」加载态，由调用方 Show()；识别完成后调用 SetResult 填充正文。
    /// 引擎未就绪时调用 SetDownloading() 进入下载态（进度实时刷新）、下载失败调 SetEngineUnavailable() 展示原因与重试。
    /// </summary>
    public sealed class OcrResultWindow : System.Windows.Window
    {
        // 深色配色（与项目一致）
        private static readonly Color WindowBackground = Color.FromRgb(30, 30, 30);       // #1E1E1E
        private static readonly Color TextBoxBackground = Color.FromRgb(37, 37, 38);       // #252526
        private static readonly Color NormalForeground = Color.FromRgb(224, 224, 224);     // #E0E0E0
        private static readonly Color HintForeground = Color.FromArgb(255, 150, 150, 150); // 占位灰字
        private static readonly Color AccentBlue = Color.FromRgb(0, 120, 212);             // 强调蓝
        private static readonly Color AccentBlueHover = Color.FromRgb(0, 100, 180);

        private const double WindowWidth = 560;
        private const double WindowHeight = 440;
        private const double TitleBarHeight = 34;
        private const double ButtonRowHeight = 52;
        private const double CopiedResetMs = 1200; // 「已复制」提示恢复时长

        private TextBox _textBox = null!;
        private Button _copyButton = null!;
        private Button _retryButton = null!;   // 「重试下载」链接样式按钮（引擎不可用时可见）
        private bool _statusSubscribed;        // 是否已订阅下载进度事件（防重复订阅）
        private readonly DispatcherTimer _copiedTimer;
        private bool _openLogged; // 窗口打开日志只记一条（Loaded 在每次 Show 时都会触发）

        public OcrResultWindow()
        {
            InitializeComponent();

            _copiedTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CopiedResetMs) };
            _copiedTimer.Tick += (s, e) =>
            {
                _copiedTimer.Stop();
                _copyButton.Content = "复制";
            };

            // 初始为加载态：只读灰字提示
            SetText("正在识别文字…", readOnly: true, HintForeground);

            Loaded += OnLoaded;
        }

        private void InitializeComponent()
        {
            Title = "OCR 识别结果";
            Width = WindowWidth;
            Height = WindowHeight;
            MinWidth = 380;
            MinHeight = 260;
            WindowStyle = WindowStyle.None;                 // 无系统标题栏
            ResizeMode = ResizeMode.CanResize;               // 允许调整大小（纯边缘拖拽，去掉右下角亮色 grip 角标）
            Background = new SolidColorBrush(WindowBackground); // 不透明背景，保证 WindowStyle.None 下可拖拽调整
            Topmost = true;                                  // 浮在贴图及其它窗口之上
            ShowInTaskbar = false;

            // 用 WindowChrome 取代系统默认非客户区：GlassFrameThickness=0 消除 WindowStyle.None 下
            // 顶部残留的白色亮线（系统 resize 边框），ResizeBorderThickness=6 保留边缘拖拽调整大小
            // （该区域不可见、仅做命中测试），CaptionHeight=0 不产生系统标题命中区，CornerRadius=0 直角。
            System.Windows.Shell.WindowChrome.SetWindowChrome(this, new System.Windows.Shell.WindowChrome
            {
                GlassFrameThickness = new Thickness(0),
                CaptionHeight = 0,
                ResizeBorderThickness = new Thickness(6),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });

            // 顶部自绘标题区：左侧灰白小字，可按住拖动窗口，底部一条细分隔线
            var titleBar = new Grid { Height = TitleBarHeight, Background = Brushes.Transparent };
            titleBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            titleBar.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1) });

            var titleText = new TextBlock
            {
                Text = "识别结果",
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(190, 190, 190)),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(14, 0, 0, 0),
            };
            Grid.SetRow(titleText, 0);
            titleBar.Children.Add(titleText);

            var titleSeparator = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(titleSeparator, 1);
            titleBar.Children.Add(titleSeparator);

            titleBar.MouseLeftButtonDown += OnTitleBarMouseLeftButtonDown;

            // 主体：可编辑多行文本框（深色，背景略浅于窗口，白光标）
            _textBox = new TextBox
            {
                Background = new SolidColorBrush(TextBoxBackground),
                Foreground = new SolidColorBrush(NormalForeground),
                CaretBrush = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(12),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalContentAlignment = VerticalAlignment.Top,
            };

            // 扁平深色滚动条：以隐式 ScrollBar 样式注入 TextBox.Resources，使 TextBox 模板内部的滚动条自动套用
            ApplyFlatScrollBar();

            // 底部按钮行：左侧「重试下载」链接按钮（引擎不可用时可见），右侧复制（强调蓝主按钮）+ 关闭（深灰次按钮）
            _copyButton = new Button
            {
                Content = "复制",
                Style = MakeButtonStyle(accent: true),
                MinWidth = 76,
            };
            _copyButton.Click += (s, e) => CopyText();

            var closeButton = new Button
            {
                Content = "关闭",
                Style = MakeButtonStyle(accent: false),
                MinWidth = 76,
            };
            closeButton.Click += (s, e) => Close();

            _retryButton = new Button
            {
                Content = "重试下载",
                Style = MakeLinkButtonStyle(),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            _retryButton.Click += OnRetryClick;

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            buttonPanel.Children.Add(_copyButton);
            buttonPanel.Children.Add(closeButton);

            // 按钮行用两列 Grid：左列自适应填满放重试按钮，右列固定宽度放复制/关闭
            var buttonRow = new Grid
            {
                Margin = new Thickness(12, 0, 12, 8),
            };
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonRow.Children.Add(_retryButton); // 默认第 0 列
            Grid.SetColumn(buttonPanel, 1);
            buttonRow.Children.Add(buttonPanel);

            // 三行布局：标题区 / 文本框 / 按钮行
            var grid = new Grid { Margin = new Thickness(1) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ButtonRowHeight) });

            Grid.SetRow(titleBar, 0);
            Grid.SetRow(_textBox, 1);
            Grid.SetRow(buttonRow, 2);
            _textBox.Margin = new Thickness(12, 8, 12, 8);

            grid.Children.Add(titleBar);
            grid.Children.Add(_textBox);
            grid.Children.Add(buttonRow);

            // 细边框（参照剪贴板预览窗描边配色），内容承载于外层 Border
            Content = new Border
            {
                Background = new SolidColorBrush(WindowBackground),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                Child = grid,
            };

            // Esc 关窗（含文本框编辑中）、Ctrl+Enter 复制并关窗，统一在窗口层拦截（隧道事件优先于 TextBox）
            PreviewKeyDown += OnPreviewKeyDown;
        }

        /// <summary>填充识别结果（UI 线程调用）。null 或空白 = 未识别到文字/OCR 不可用，显示占位提示。</summary>
        public void SetResult(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Logger.LogInfo("OCR 识别结果：无结果");
                SetText("未识别到文字", readOnly: true, HintForeground);
            }
            else
            {
                Logger.LogInfo($"OCR 识别结果：{text.Length} 个字符");
                SetText(text, readOnly: false, NormalForeground);
            }
        }

        /// <summary>
        /// 进入「引擎下载中」态：主文本区只读显示下载提示，并订阅 OcrEngineInstaller.StatusChanged
        /// 把进度实时刷新到该文本。下载在后台继续（即使用户关窗），由 App 启动的合流任务兜底。
        /// </summary>
        public void SetDownloading()
        {
            SetText("识图引擎正在下载，暂时不可用……完成后将自动识别", readOnly: true, HintForeground);
            _retryButton.Visibility = Visibility.Collapsed;
            SubscribeStatus();
        }

        /// <summary>
        /// 引擎不可用终态：显示失败原因 + OcrEngineInstaller.ManualInstallHint（手动安装指引），
        /// 并提供「重试下载」链接按钮。
        /// </summary>
        public void SetEngineUnavailable(string reason)
        {
            var text = string.IsNullOrWhiteSpace(reason) ? "识图引擎不可用" : $"识图引擎不可用：{reason}";
            var hint = OcrEngineInstaller.ManualInstallHint;
            if (!string.IsNullOrWhiteSpace(hint))
                text += "\n" + hint;

            SetText(text, readOnly: true, HintForeground);
            _retryButton.Content = "重试下载";
            _retryButton.IsEnabled = true;
            _retryButton.Visibility = Visibility.Visible;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (!_openLogged)
            {
                _openLogged = true;
                Logger.LogInfo("OCR 识别结果窗口打开");
            }
            CenterWindowOnCurrentScreen();
        }

        /// <summary>统一设置文本框内容 / 只读态 / 前景色，并复位光标到开头。</summary>
        private void SetText(string text, bool readOnly, Color foreground)
        {
            _textBox.Text = text;
            _textBox.IsReadOnly = readOnly;
            _textBox.Foreground = new SolidColorBrush(foreground);
            _textBox.CaretIndex = 0;
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
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                CopyText();
                Close();
                e.Handled = true;
            }
        }

        /// <summary>点击重试下载：禁用按钮、进入下载态并等待，成功提示关闭后重新识别，失败回到不可用态。</summary>
        private async void OnRetryClick(object sender, RoutedEventArgs e)
        {
            _retryButton.IsEnabled = false;
            SetDownloading();

            try
            {
                bool ok = await OcrEngineInstaller.EnsureInstalledAsync();
                if (ok)
                {
                    SetText("引擎已就绪，请关闭后重新识别", readOnly: true, HintForeground);
                }
                else
                {
                    SetEngineUnavailable("下载失败");
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("重试下载增强引擎失败", ex);
                SetEngineUnavailable(ex.Message);
            }
        }

        private void CopyText()
        {
            try
            {
                CopyToClipboardWithRetry(_textBox.Text);
                // 成功后按钮短暂显示「已复制」，随后恢复
                _copyButton.Content = "已复制";
                _copiedTimer.Stop();
                _copiedTimer.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError("复制 OCR 识别结果失败", ex);
            }
        }

        /// <summary>
        /// 复制文本回剪贴板：剪贴板被其它进程占用会抛 ExternalException，
        /// 按仓库惯例重试 3 次、每次间隔 50ms，仍失败则向上抛由调用方记 ERROR 日志。
        /// </summary>
        private static void CopyToClipboardWithRetry(string text)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (ExternalException) when (attempt < 2)
                {
                    Thread.Sleep(50);
                }
            }
        }

        /// <summary>
        /// 在鼠标所在屏幕的工作区居中：WinForms 屏幕坐标是物理像素，除以窗口 DPI 得 WPF DIP
        /// （PerMonitorV2 语义，见 CLAUDE.md DPI 小节），与 MainWindow.CenterWindowOnCurrentScreen 一致。
        /// </summary>
        private void CenterWindowOnCurrentScreen()
        {
            try
            {
                var mousePosition = System.Windows.Forms.Cursor.Position;
                var currentScreen = System.Windows.Forms.Screen.FromPoint(mousePosition);

                var dpiScale = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiScale.DpiScaleX;
                double dpiScaleY = dpiScale.DpiScaleY;

                double screenLeft = currentScreen.WorkingArea.Left / dpiScaleX;
                double screenTop = currentScreen.WorkingArea.Top / dpiScaleY;
                double screenWidth = currentScreen.WorkingArea.Width / dpiScaleX;
                double screenHeight = currentScreen.WorkingArea.Height / dpiScaleY;

                Left = screenLeft + (screenWidth - Width) / 2;
                Top = screenTop + (screenHeight - Height) / 2;
            }
            catch (Exception ex)
            {
                Logger.LogError("OCR 结果窗口居中定位失败，使用默认位置", ex);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>
        /// 按钮深色样式（纯代码模板：Border + ContentPresenter，默认模板不响应 Background 触发器）。
        /// accent = true 时常态即蓝底（「复制」主按钮），false 为深灰底（「关闭」次按钮），hover 各自变深/变浅。
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

        /// <summary>订阅增强引擎下载进度事件（幂等，最多订阅一次；OnClosed 时退订防泄漏）。</summary>
        private void SubscribeStatus()
        {
            if (_statusSubscribed)
                return;
            _statusSubscribed = true;
            OcrEngineInstaller.StatusChanged += OnStatusChanged;
        }

        /// <summary>下载进度回调：事件可能从后台线程触发，务必切回 UI 线程更新文字。</summary>
        private void OnStatusChanged(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                _textBox.Text = status;
            }
            else
            {
                Dispatcher.BeginInvoke(() => _textBox.Text = status);
            }
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
                _textBox.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
            }
            catch (Exception ex)
            {
                Logger.LogError("应用 OCR 结果窗口滚动条样式失败", ex);
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _copiedTimer.Stop();
            // 退订下载进度事件，防泄漏
            if (_statusSubscribed)
            {
                OcrEngineInstaller.StatusChanged -= OnStatusChanged;
                _statusSubscribed = false;
            }
            base.OnClosed(e);
        }
    }
}
