using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CommandLauncher
{
    // HotKey可见性转换器
    public class HotKeyVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string hotKey && !string.IsNullOrEmpty(hotKey))
            {
                return Visibility.Visible;
            }
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    // 执行统计转换器：将 ExecuteCount + LastExecuted 合成 "3× · 5m"（count=0 时只显示时间）
    public class ExecuteStatsConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is int count && values[1] is DateTime lastExecuted
                && lastExecuted != DateTime.MinValue)
            {
                string timeStr = FormatFriendlyTime(lastExecuted);
                if (count > 0)
                    return string.IsNullOrEmpty(timeStr) ? $"{count}×" : $"{count}× · {timeStr}";
                return timeStr;
            }
            return "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotImplementedException();

        private static string FormatFriendlyTime(DateTime dt)
        {
            if (dt == DateTime.MinValue) return "";
            var elapsed = DateTime.Now - dt;
            if (elapsed.TotalSeconds < 60)  return $"{(int)elapsed.TotalSeconds}s";
            if (elapsed.TotalMinutes < 60)  return $"{(int)elapsed.TotalMinutes}m";
            if (elapsed.TotalHours   < 24)  return $"{(int)elapsed.TotalHours}h";
            if (elapsed.TotalDays    < 365) return $"{(int)elapsed.TotalDays}d";
            return $"{(int)(elapsed.TotalDays / 365)}y";
        }
    }

    // 执行统计可见性转换器（绑定 LastExecuted：有执行记录时显示，否则隐藏）
    public class ExecuteStatsVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is DateTime dt && dt != DateTime.MinValue ? Visibility.Visible : Visibility.Collapsed;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotImplementedException();
    }

    // 命令名称与快捷键组合转换器
    public class CommandNameWithHotKeyConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length >= 2 && values[0] is string name && values[1] is string hotKey)
            {
                if (string.IsNullOrEmpty(hotKey))
                    return name;

                string macStyleHotKey = ConvertToMacStyle(hotKey);
                return $"{name} ({macStyleHotKey})";
            }
            return values[0]?.ToString() ?? "";
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }

        private static string ConvertToMacStyle(string hotKey)
        {
            if (string.IsNullOrEmpty(hotKey))
                return "";

            var parts = hotKey.Split('+');
            var result = "";

            foreach (var part in parts)
            {
                var trimmedPart = part.Trim().ToLower();
                switch (trimmedPart)
                {
                    case "ctrl":
                        result += "⌃";
                        break;
                    case "alt":
                        result += "⌥";
                        break;
                    case "shift":
                        result += "⇧";
                        break;
                    case "win":
                        result += "⌘";
                        break;
                    default:
                        result += trimmedPart.ToUpper();
                        break;
                }
            }

            return result;
        }
    }

    // 主窗口
    public class MainWindow : Window, IDisposable
    {
        private static readonly string[] AppCommands = ["config", "setconfig", "logs", "update", "autostart", "exit"];

        private readonly ObservableCollection<Command> _filteredCommands = [];
        private readonly HotKeyListener _hotKeyListener = new();
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon = new() { Text = "Command Launcher", Visible = true };
        private readonly string _configPath;
        private int _selectedIndex = 0;
        private bool _disposed = false;

        /// <summary>
        /// 开机自启是否已开启的缓存值。
        /// <see cref="AutoStartManager.IsEnabled"/> 要起一个 schtasks 子进程，
        /// 而 <see cref="RefreshCommandList"/> 每敲一个字符就会跑一遍，绝不能在那里直接查询；
        /// 因此只在启动、托盘菜单打开、切换开关这三个低频时机刷新。
        /// </summary>
        private bool _autoStartEnabled;

        private readonly TextBox _searchBox = CreateSearchBox();
        private readonly TextBlock _placeholder = CreatePlaceholder();
        private readonly ListBox _commandList = CreateCommandList();

        #region 前台激活（与剪贴板历史窗口同一套策略，见 ForegroundActivator）

        /// <summary>唤出前记录的前台窗口，用于 AttachThreadInput 绕过前台锁定（取线程做输入队列附加）。</summary>
        private IntPtr _previousForeground;

        /// <summary>显示后的激活宽限期：此间失焦视为激活抖动，重试激活而非隐藏窗口。</summary>
        private const int ActivationGraceMs = 600;

        /// <summary>激活重试上限与间隔（等待系统解除前台锁定）。</summary>
        private const int MaxActivationRetries = 8;
        private const int ActivationRetryIntervalMs = 60;

        private readonly DispatcherTimer _activationTimer;
        private long _graceUntil;
        private int _activationRetries;

        #endregion

        public MainWindow()
        {
            // 支持命令行指定配置文件路径（参数统一由 StartupArgs 解析，勿直接读 GetCommandLineArgs）
            _configPath = StartupArgs.ConfigPath ?? "config.json";

            Logger.LogInfo($"开始初始化主窗口，配置文件: {_configPath}");

            // 激活重试定时器要在任何可能唤出窗口的东西（热键注册、托盘图标）之前就绪
            _activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ActivationRetryIntervalMs) };
            _activationTimer.Tick += (s, e) => TryActivateOnce();

            InitializeComponent();
            SetupNotifyIcon();
            SetupHotKeyListener();
            SetupUI();

            // 开机自启状态放到后台查，不阻塞启动：AutoStartManager.IsEnabled 要起 schtasks 子进程并
            // WaitForExit（常规 0.1~0.5s，冷启动更久），而它在启动阶段的唯一用途只是渲染 autostart
            // 命令的「当前已开启/未开启」描述文本——远不值得让整个程序就绪时间为它等着。
            // 面板要等用户按热键才会出现，届时早已回填；托盘菜单 Opening 时另有一次同步刷新兜底。
            RefreshAutoStartStateAsync();

            // 只 Hide 不 Minimize：窗口本来就没显示过，Hide 已足够让它不可见。
            // 若初始状态设为 Minimized，首次唤出就得走「Show(最小化) → 还原」两段状态切换，
            // 激活时序更脆弱；而且进程首次 ShowWindow 的 nCmdShow 会被 STARTUPINFO.wShowWindow 替换
            // （见 WindowEnumerator.Activate 的同款注释），由计划任务/脚本拉起时首次还原可能直接落空。
            ShowInTaskbar = false;
            Hide();
            Logger.LogInfo("主窗口初始化完成");
        }

        ~MainWindow()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // 清理托管资源
                    try
                    {
                        // 先停激活重试定时器：否则退出流程中它还可能触发一次 TryActivateOnce
                        _activationTimer?.Stop();

                        if (_notifyIcon != null)
                        {
                            _notifyIcon.Visible = false;
                            _notifyIcon.Dispose();
                            Logger.LogInfo("系统托盘图标已清理");
                        }

                        _hotKeyListener?.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError("清理资源失败", ex);
                    }
                }
                _disposed = true;
            }
        }

        private static TextBox CreateSearchBox()
        {
            return new TextBox
            {
                Height = 38,
                FontSize = 16,
                Padding = new Thickness(15, 8, 15, 8),
                Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 50)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top
            };
        }

        private static TextBlock CreatePlaceholder()
        {
            return new TextBlock
            {
                Text = "输入字符搜索（ctrl+p, ctrl+n上下选择，回车执行）",
                FontSize = 16,
                Padding = new Thickness(15, 8, 15, 8),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false
            };
        }

        private static ListBox CreateCommandList()
        {
            return new ListBox
            {
                Margin = new Thickness(0, 55, 0, 0), // 增加顶部间距
                Background = new SolidColorBrush(Color.FromArgb(255, 35, 35, 35)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
                BorderThickness = new Thickness(1),
                SelectionMode = SelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        private void InitializeComponent()
        {
            Title = "Command Launcher";
            Width = 650;
            Height = 500; // 增加窗口高度
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30));
            Topmost = true;
            // 不靠 Show 抢焦点：热键路径上的输入没有进入本进程消息队列，Show 自带的激活会被前台锁定
            // 间歇性拒绝，产生「短暂激活又立刻失活」的抖动，进而被 OnDeactivated 当成失焦直接隐藏
            // （表现就是按了热键什么都没有）。激活统一交给 TryActivateOnce。
            ShowActivated = false;

            // 主容器
            var mainGrid = new Grid
            {
                Margin = new Thickness(12) // 增加整体边距
            };

            // 搜索框
            _searchBox.TextChanged += SearchBox_TextChanged;
            _searchBox.PreviewKeyDown += SearchBox_KeyDown;

            // 设置ListBox样式 - 更宽松的设计
            var listBoxStyle = new Style(typeof(ListBoxItem));
            listBoxStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(15, 8, 5, 8))); // 右侧padding减少，让shell命令更靠右
            listBoxStyle.Setters.Add(new Setter(MarginProperty, new Thickness(2)));
            listBoxStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
            listBoxStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
            listBoxStyle.Setters.Add(new Setter(HeightProperty, 52.0)); // 固定行高，确保每行高度一致

            // 鼠标悬停效果
            var hoverTrigger = new Trigger
            {
                Property = IsMouseOverProperty,
                Value = true
            };
            hoverTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 55, 55, 55))));

            // 选中效果
            var selectedTrigger = new Trigger
            {
                Property = ListBoxItem.IsSelectedProperty,
                Value = true
            };
            selectedTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))));

            listBoxStyle.Triggers.Add(hoverTrigger);
            listBoxStyle.Triggers.Add(selectedTrigger);
            _commandList.ItemContainerStyle = listBoxStyle;

            // 设置ListBox的ItemTemplate - 使用Grid布局替代DockPanel
            var dataTemplate = new DataTemplate();
            var gridFactory = new FrameworkElementFactory(typeof(Grid));

            // 定义两列：左侧内容列 (*)，右侧Shell命令列 (最大50%)
            gridFactory.AddHandler(
                LoadedEvent,
                new RoutedEventHandler((s, e) =>
                {
                    var g = (Grid)s;
                    g.ColumnDefinitions.Clear();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MaxWidth = double.PositiveInfinity });
                })
            );

            // 左侧内容区域 - 垂直排列
            var leftStack = new FrameworkElementFactory(typeof(StackPanel));
            leftStack.SetValue(Grid.ColumnProperty, 0);
            leftStack.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            leftStack.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            leftStack.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));

            // 名称(包含快捷键)
            var nameTextBlock = new FrameworkElementFactory(typeof(TextBlock));
            var nameBinding = new MultiBinding
            {
                Converter = new CommandNameWithHotKeyConverter()
            };
            nameBinding.Bindings.Add(new Binding("Name"));
            nameBinding.Bindings.Add(new Binding("HotKey"));
            nameTextBlock.SetBinding(TextBlock.TextProperty, nameBinding);
            nameTextBlock.SetValue(TextBlock.FontSizeProperty, 14.0);
            nameTextBlock.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            nameTextBlock.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            nameTextBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            nameTextBlock.SetValue(MarginProperty, new Thickness(0, 0, 0, 2));

            // 描述
            var descTextBlock = new FrameworkElementFactory(typeof(TextBlock));
            descTextBlock.SetBinding(TextBlock.TextProperty, new Binding("Description"));
            descTextBlock.SetValue(TextBlock.FontSizeProperty, 11.0);
            descTextBlock.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(255, 160, 160, 160)));
            descTextBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            leftStack.AppendChild(nameTextBlock);
            leftStack.AppendChild(descTextBlock);
            gridFactory.AppendChild(leftStack);

            // 右侧列 - StackPanel (Shell + 执行统计)
            var rightStack = new FrameworkElementFactory(typeof(StackPanel));
            rightStack.SetValue(Grid.ColumnProperty, 1);
            rightStack.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            rightStack.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            rightStack.SetValue(MarginProperty, new Thickness(10, 0, 0, 0));

            // 执行统计文本："3× · 5m"
            var statsTextBlock = new FrameworkElementFactory(typeof(TextBlock));
            var statsBinding = new MultiBinding { Converter = new ExecuteStatsConverter() };
            statsBinding.Bindings.Add(new Binding("ExecuteCount"));
            statsBinding.Bindings.Add(new Binding("LastExecuted"));
            statsTextBlock.SetBinding(TextBlock.TextProperty, statsBinding);
            statsTextBlock.SetValue(TextBlock.FontSizeProperty, 12.0);
            statsTextBlock.SetValue(MarginProperty, new Thickness(0, 0, 0, 2));
            statsTextBlock.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            statsTextBlock.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            statsTextBlock.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            statsTextBlock.SetBinding(VisibilityProperty, new Binding("LastExecuted") { Converter = new ExecuteStatsVisibilityConverter() });

            // Shell 命令文本
            var shellTextBlock = new FrameworkElementFactory(typeof(TextBlock));
            shellTextBlock.SetBinding(TextBlock.TextProperty, new Binding("Shell"));
            shellTextBlock.SetValue(TextBlock.FontSizeProperty, 10.0);
            shellTextBlock.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)));
            shellTextBlock.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            shellTextBlock.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas, Courier New"));
            shellTextBlock.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            shellTextBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            rightStack.AppendChild(statsTextBlock);
            rightStack.AppendChild(shellTextBlock);
            gridFactory.AppendChild(rightStack);

            dataTemplate.VisualTree = gridFactory;
            _commandList.ItemTemplate = dataTemplate;
            ScrollViewer.SetVerticalScrollBarVisibility(_commandList, ScrollBarVisibility.Hidden);
            ScrollViewer.SetHorizontalScrollBarVisibility(_commandList, ScrollBarVisibility.Disabled); // 禁用横向滚动，确保列宽受约束使省略号生效

            mainGrid.Children.Add(_searchBox);
            mainGrid.Children.Add(_placeholder);
            mainGrid.Children.Add(_commandList);

            Content = mainGrid;
        }

        private void SetupNotifyIcon()
        {
            try
            {
                try
                {
                    var exePath = Path.Join(AppContext.BaseDirectory, Path.GetFileName(Environment.ProcessPath));
                    if (File.Exists(exePath))
                    {
                        _notifyIcon.Icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                        Logger.LogInfo("成功从exe文件中提取图标");
                    }
                    else
                    {
                        string iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
                        if (File.Exists(iconPath))
                        {
                            _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
                            Logger.LogInfo("成功加载 app.ico 图标");
                        }
                        else
                        {
                            _notifyIcon.Icon = CreateDefaultIcon();
                            Logger.LogWarning($"未找到图标文件，使用默认图标");
                        }
                    }
                }
                catch (Exception ex)
                {
                    _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
                    Logger.LogWarning($"加载图标失败，使用系统默认图标: {ex.Message}");
                }

                var contextMenu = new System.Windows.Forms.ContextMenuStrip();
                contextMenu.Items.Add("显示", null, (s, e) => ShowWindow());
                contextMenu.Items.Add("截图", null, (s, e) => ScreenshotManager.StartCapture());
                contextMenu.Items.Add("贴图 (剪贴板)", null, (s, e) => ScreenshotManager.PinFromClipboard());
                var togglePinsItem = new System.Windows.Forms.ToolStripMenuItem("隐藏所有贴图");
                togglePinsItem.Click += (s, e) => PinWindow.ToggleAllVisibility();
                contextMenu.Items.Add(togglePinsItem);
                var boxSelectItem = new System.Windows.Forms.ToolStripMenuItem("框选移动贴图");
                boxSelectItem.Click += (s, e) => PinWindow.StartBoxSelect();
                contextMenu.Items.Add(boxSelectItem);
                contextMenu.Items.Add(BuildEyeCareMenu());
                var autoStartItem = new System.Windows.Forms.ToolStripMenuItem("开机自动启动") { CheckOnClick = false };
                autoStartItem.Click += (s, e) => ToggleAutoStart();
                contextMenu.Items.Add(autoStartItem);
                contextMenu.Items.Add("设定配置文件", null, (s, e) => AppConfig.SetConfigFile());
                contextMenu.Items.Add("打开配置文件", null, (s, e) => AppConfig.OpenConfigFile());
                contextMenu.Items.Add("打开日志文件", null, (s, e) => Logger.OpenLogFile());
                contextMenu.Items.Add("检查更新", null, (s, e) => _ = UpdateCoordinator.RunManualCheckAsync());
                contextMenu.Items.Add("退出", null, (s, e) => ExitApplication());
                _notifyIcon.ContextMenuStrip = contextMenu;

                // 打开托盘菜单时按当前状态刷新文案与可用性（热键切换的状态也能正确反映）
                contextMenu.Opening += (s, e) =>
                {
                    togglePinsItem.Text = PinWindow.IsAllHidden ? "显示所有贴图" : "隐藏所有贴图";
                    togglePinsItem.Enabled = PinWindow.OpenCount > 0;
                    boxSelectItem.Enabled = PinWindow.OpenCount > 0 && !PinWindow.IsAllHidden; // 整体隐藏时框选必然选不中，同 StartBoxSelect 的忽略口径

                    // 计划任务可能被用户在「任务计划程序」里改掉，每次打开菜单实查一次（低频，可接受）
                    _autoStartEnabled = AutoStartManager.IsEnabled();
                    autoStartItem.Checked = _autoStartEnabled;
                };

                _notifyIcon.DoubleClick += (s, e) => ShowWindow();
                Logger.LogInfo("系统托盘图标设置成功");
            }
            catch (Exception ex)
            {
                Logger.LogError("设置系统托盘图标失败", ex);
            }
        }

        /// <summary>构建托盘「护眼模式」子菜单（单选打勾，每次打开时同步当前模式）。</summary>
        private static System.Windows.Forms.ToolStripMenuItem BuildEyeCareMenu()
        {
            var menu = new System.Windows.Forms.ToolStripMenuItem("护眼模式");
            foreach (var mode in EyeCareManager.Modes)
            {
                var item = new System.Windows.Forms.ToolStripMenuItem(mode.Name) { Tag = mode };
                item.Click += (s, e) =>
                {
                    try
                    {
                        EyeCareManager.ApplyMode(mode);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError($"应用护眼模式失败: {mode.Name}", ex);
                        MessageBox.Show($"应用护眼模式失败: {ex.Message}");
                    }
                };
                menu.DropDownItems.Add(item);
            }
            // 打开时刷新勾选项（命令面板执行的模式也能正确反映）
            menu.DropDownOpening += (s, e) =>
            {
                foreach (System.Windows.Forms.ToolStripMenuItem item in menu.DropDownItems)
                {
                    item.Checked = item.Tag is EyeCareMode m && m.Name == EyeCareManager.CurrentModeName;
                }
            };
            return menu;
        }

        private static System.Drawing.Icon CreateDefaultIcon()
        {
            // 创建一个简单的16x16像素图标
            var bitmap = new System.Drawing.Bitmap(16, 16);
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.Clear(System.Drawing.Color.DarkBlue);
                graphics.FillEllipse(System.Drawing.Brushes.White, 2, 2, 12, 12);
                graphics.DrawString("C", new System.Drawing.Font("Arial", 8, System.Drawing.FontStyle.Bold),
                    System.Drawing.Brushes.DarkBlue, 4, 2);
            }
            return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
        }

        private void SetupUI()
        {
            _commandList.ItemsSource = _filteredCommands;
            _commandList.MouseDoubleClick += (s, e) => ExecuteSelectedCommand();

            RefreshCommandList();
        }

        private void SetupHotKeyListener()
        {
            RegisterLauncherHotKey(isReload: false);

            // 配置更新时——一定要切回到 UI 线程
            AppConfig.Instance.ConfigUpdated += () =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _hotKeyListener.UnregisterHotKey();
                    RegisterLauncherHotKey(isReload: true);
                });
            };

            _hotKeyListener.HotKeyPressed += ShowWindow;
        }

        /// <summary>
        /// 注册命令面板的全局热键，失败时退回默认热键。
        /// <para>
        /// 两个都失败时必须让用户看得见：热键是本程序唯一的入口，
        /// 只写日志的话用户只会觉得「程序坏了」，而真实原因通常是热键被别的程序抢先注册了。
        /// </para>
        /// </summary>
        private void RegisterLauncherHotKey(bool isReload)
        {
            string prefix = isReload ? "重新注册热键失败" : "注册热键失败";
            var hotKey = AppConfig.Instance.Config.HotKey;

            if (_hotKeyListener.RegisterHotKey(hotKey))
                return;

            // 配置里就是默认热键时不必再试一遍同样的组合
            bool sameAsDefault = string.Equals(hotKey, AppConfig.DefaultHotKey, StringComparison.OrdinalIgnoreCase);
            if (!sameAsDefault)
            {
                Logger.LogWarning($"{prefix}: {hotKey}，改用默认热键 {AppConfig.DefaultHotKey}");
                if (_hotKeyListener.RegisterHotKey(AppConfig.DefaultHotKey))
                {
                    ShowHotKeyBalloon($"热键 {hotKey} 注册失败（可能已被其它程序占用），已改用 {AppConfig.DefaultHotKey}。");
                    return;
                }
            }

            Logger.LogError($"{prefix}: {hotKey}，默认热键 {AppConfig.DefaultHotKey} 同样注册失败，命令面板将只能从托盘唤出",
                new InvalidOperationException("RegisterHotKey 失败"));
            ShowHotKeyBalloon($"热键 {hotKey} 注册失败，可能已被其它程序占用。\n请改用托盘图标唤出，或在配置文件中换一个热键。");
        }

        /// <summary>托盘气泡提示（失败静默：提示不出来也不能影响程序运行）。</summary>
        private void ShowHotKeyBalloon(string message)
        {
            try
            {
                _notifyIcon.ShowBalloonTip(8000, "Command Launcher", message, System.Windows.Forms.ToolTipIcon.Warning);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"显示托盘气泡提示失败: {ex.Message}");
            }
        }


        /// <summary>
        /// 唤出命令面板：全局热键、托盘「显示」、托盘双击、以及第二个实例的唤起请求都走这里。
        /// 已经在前台时再按一次热键即收起（切换式）；已显示但没拿到焦点时不收起，而是继续抢焦点。
        /// </summary>
        public void ShowWindow()
        {
            if (IsVisible && IsActive)
            {
                HideWindow();
                return;
            }

            // 必须在 Show 之前记录：Show 之后前台就是我们自己了。
            // 已经可见（激活失败重来）时保留原值，否则会把自己记成「弹出前的前台窗口」。
            if (!IsVisible)
                _previousForeground = ForegroundActivator.GetForeground();

            CenterWindowOnCurrentScreen();
            Show(); // ShowActivated=false，只显示不激活
            WindowState = WindowState.Normal;

            _graceUntil = Environment.TickCount64 + ActivationGraceMs;
            _activationRetries = 0;

            _placeholder.Visibility = string.IsNullOrEmpty(_searchBox.Text) ? Visibility.Visible : Visibility.Hidden;

            RefreshCommandList(_searchBox.Text);
            ScrollCommandListToTop();

            // 唤出是低频操作，这条 INFO 是排查「按了热键没反应」的关键线索：
            // 日志里没有它 = WM_HOTKEY 根本没到；有它但用户没看到窗口 = 位置或激活的问题。
            Logger.LogInfo($"命令面板唤出：位置 ({Left:F0},{Top:F0}) 尺寸 {Width:F0}x{Height:F0}，弹出前前台窗口 0x{_previousForeground.ToInt64():X}");

            TryActivateOnce();
        }

        /// <summary>
        /// 抢一次前台焦点。失败时按 <see cref="ActivationRetryIntervalMs"/> 短间隔重试，
        /// 直到成功或用尽 <see cref="MaxActivationRetries"/> 次（等待系统解除前台锁定）。
        /// </summary>
        private void TryActivateOnce()
        {
            if (!IsVisible)
                return; // 已隐藏则不再激活

            var hwnd = new WindowInteropHelper(this).Handle;
            bool ok = ForegroundActivator.ForceForeground(hwnd, _previousForeground, "命令启动器");

            try
            {
                Activate(); // WPF 层同步激活态
                Keyboard.Focus(_searchBox);
                _searchBox.SelectAll();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"命令启动器激活后续处理失败: {ex.Message}");
            }

            _activationRetries++;
            if (ok)
            {
                _activationTimer.Stop(); // 激活成功即停止重试，避免定时器持续触发
            }
            else if (_activationRetries < MaxActivationRetries)
            {
                if (_activationRetries == 1)
                    Logger.LogWarning("命令启动器首次激活失败，开始短间隔重试");

                _activationTimer.Stop();
                _activationTimer.Start();
            }
            else
            {
                _activationTimer.Stop();
                Logger.LogWarning("命令启动器多次激活失败，窗口可能未获得焦点");
            }
        }

        private void CenterWindowOnCurrentScreen()
        {
            try
            {
                var mousePosition = System.Windows.Forms.Cursor.Position;
                var currentScreen = System.Windows.Forms.Screen.FromPoint(mousePosition);

                var dpiScale = VisualTreeHelper.GetDpi(this);
                double dpiScaleX = dpiScale.DpiScaleX;
                double dpiScaleY = dpiScale.DpiScaleY;

                // 将屏幕坐标从物理像素转换为WPF设备无关像素
                double screenLeft = currentScreen.WorkingArea.Left / dpiScaleX;
                double screenTop = currentScreen.WorkingArea.Top / dpiScaleY;
                double screenWidth = currentScreen.WorkingArea.Width / dpiScaleX;
                double screenHeight = currentScreen.WorkingArea.Height / dpiScaleY;

                double windowWidth = Width;
                double windowHeight = Height;

                Left = screenLeft + (screenWidth - windowWidth) / 2;
                Top = screenTop + (screenHeight - windowHeight) / 2;
            }
            catch (Exception ex)
            {
                Logger.LogError("居中显示窗口失败，使用默认位置", ex);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        private void RefreshCommandList(string filter = "")
        {
            _filteredCommands.Clear();

            // 将配置命令转换为运行时命令对象
            List<Command> commands = AppConfig.Instance.Config.Commands.Select(configCmd => new Command
            {
                Name = configCmd.Name,
                Description = configCmd.Description,
                Shell = configCmd.Shell,
                HotKey = configCmd.HotKey ?? string.Empty,
                RunAsAdmin = configCmd.RunAsAdmin,
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime(configCmd.Name),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount(configCmd.Name),
            }).ToList();

            // 加入对本应用程序的特殊处理
            commands.Add(new Command
            {
                Name = "config",
                Description = "打开windows-global-launcher的配置文件",
                Shell = "config",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("config"),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount("config")
            });

            commands.Add(new Command
            {
                Name = "setconfig",
                Description = "设定windows-global-launcher的配置文件",
                Shell = "setconfig",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("setconfig"),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount("setconfig")
            });

            commands.Add(new Command
            {
                Name = "logs",
                Description = "打开windows-global-launcher的日志文件",
                Shell = "logs",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("logs"),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount("logs")
            });

            commands.Add(new Command
            {
                Name = "update",
                Description = "检查windows-global-launcher的新版本",
                Shell = "update",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("update"),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount("update")
            });

            commands.Add(new Command
            {
                Name = "autostart",
                Description = "开机自动启动：切换开/关（当前" + (_autoStartEnabled ? "已开启" : "未开启") + "）",
                Shell = "autostart",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("autostart"),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount("autostart")
            });

            commands.Add(new Command
            {
                Name = "exit",
                Description = "退出windows-global-launcher",
                Shell = "exit",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("exit"),
                ExecuteCount = AppState.Instance.GetCommandExecuteCount("exit")
            });

            // 注入护眼模式内置命令（护眼：xxx），由 ExecuteAppCommand 特殊处理
            foreach (var mode in EyeCareManager.Modes)
            {
                commands.Add(new Command
                {
                    Name = mode.CommandName,
                    Description = mode.Description,
                    Shell = mode.CommandName,
                    LastExecuted = AppState.Instance.GetCommandLastExecutedTime(mode.CommandName),
                    ExecuteCount = AppState.Instance.GetCommandExecuteCount(mode.CommandName)
                });
            }

            if (!string.IsNullOrEmpty(filter))
            {
                commands = commands.Where(c =>
                {
                    var score = FuzzyMatcher.GetCommandMatchScore(filter, c);
                    c.MatchScore = score;
                    return score > 0;
                }).ToList();
            }
            else
            {
                foreach (var cmd in commands)
                    cmd.MatchScore = 1.0;
            }

            // 排序：先按匹配分数，再按最后执行时间
            commands = commands.OrderByDescending(c => c.MatchScore)
                              .ThenByDescending(c => c.LastExecuted)
                              .Take(AppConfig.Instance.Config.MaxDisplayItems)
                              .ToList();

            foreach (var cmd in commands)
            {
                _filteredCommands.Add(cmd);
            }
        }

        private void SelectLastExecutedCommand()
        {
            if (_filteredCommands.Count > 0)
            {
                var lastExecuted = _filteredCommands.OrderByDescending(c => c.LastExecuted).First();
                _selectedIndex = _filteredCommands.IndexOf(lastExecuted);
                _commandList.SelectedIndex = _selectedIndex;
            }
        }

        private void ScrollCommandListToTop()
        {
            if (_commandList.Items.Count > 0)
            {
                _selectedIndex = 0;
                _commandList.SelectedIndex = _selectedIndex;
                _commandList.ScrollIntoView(_commandList.Items[0]);
            }
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchBox = (TextBox)sender;
            RefreshCommandList(searchBox.Text);
            _placeholder.Visibility = string.IsNullOrEmpty(searchBox.Text) ? Visibility.Visible : Visibility.Hidden;

            if (_filteredCommands.Count > 0)
            {
                _selectedIndex = 0;
                _commandList.SelectedIndex = _selectedIndex;
            }
        }

        private void SearchBox_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    ExecuteSelectedCommand();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    HideWindow();
                    e.Handled = true;
                    break;
            }

            // Ctrl+P / Ctrl+N
            if (e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                if (e.Key == Key.P)
                {
                    MoveSelection(-1);
                    e.Handled = true;
                }
                else if (e.Key == Key.N)
                {
                    MoveSelection(1);
                    e.Handled = true;
                }
            }

            if (!e.Handled)
            {
                foreach (var cmd in AppConfig.Instance.Config.Commands)
                {
                    if (cmd.HotKey != null && IsHotKeyMatch(cmd.HotKey, e))
                    {
                        ExecuteCommand(new Command
                        {
                            Name = cmd.Name,
                            Description = cmd.Description,
                            Shell = cmd.Shell,
                            HotKey = cmd.HotKey
                        });
                        e.Handled = true;
                        break;
                    }
                }
            }
        }

        private static bool IsHotKeyMatch(string hotKey, KeyEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(hotKey))
                return false;

            try
            {
                if (new KeyGestureConverter().ConvertFromString(hotKey) is KeyGesture gesture)
                {
                    Key actualKey = e.Key == Key.System ? e.SystemKey : e.Key;
                    return actualKey == gesture.Key && e.KeyboardDevice.Modifiers == gesture.Modifiers;
                }
            }
            catch
            {
                // ignore parse errors
            }
            return false;
        }

        private void MoveSelection(int direction)
        {
            if (_filteredCommands.Count == 0)
            {
                return;
            }

            _selectedIndex = Math.Max(0, Math.Min(_filteredCommands.Count - 1, _selectedIndex + direction));
            _commandList.SelectedIndex = _selectedIndex;
            _commandList.ScrollIntoView(_commandList.SelectedItem);
        }

        private bool ExecuteAppCommand(Command selectedCommand)
        {
            // 护眼模式命令（护眼：xxx）
            var eyeCareMode = EyeCareManager.FindByCommandName(selectedCommand.Name);
            if (eyeCareMode != null)
            {
                EyeCareManager.ApplyMode(eyeCareMode);
                return true;
            }

            if (AppCommands.Contains(selectedCommand.Name))
            {
                if (selectedCommand.Name == "config")
                {
                    AppConfig.OpenConfigFile();
                }
                else if (selectedCommand.Name == "setconfig")
                {
                    AppConfig.SetConfigFile();
                }
                else if (selectedCommand.Name == "logs")
                {
                    Logger.OpenLogFile();
                }
                else if (selectedCommand.Name == "update")
                {
                    // 手动检查更新是网络操作，fire-and-forget；结果由 UpdateCoordinator 自行弹窗反馈
                    _ = UpdateCoordinator.RunManualCheckAsync();
                }
                else if (selectedCommand.Name == "autostart")
                {
                    ToggleAutoStart();
                }
                else if (selectedCommand.Name == "exit")
                {
                    ExitApplication();
                }
                return true;
            }

            return false;
        }

        /// <summary>
        /// 切换开机自动启动（命令面板的 autostart 命令与托盘菜单共用）。
        /// 结果一律弹窗告知：这是个「设置类」操作，静默成功/失败都会让用户不确定到底生效没有。
        /// </summary>
        private void ToggleAutoStart()
        {
            bool enabled = AutoStartManager.IsEnabled();
            bool ok = enabled ? AutoStartManager.Disable(out string error) : AutoStartManager.Enable(out error);

            _autoStartEnabled = AutoStartManager.IsEnabled();

            if (ok)
            {
                MessageBox.Show(
                    _autoStartEnabled
                        ? "已开启开机自动启动。\n下次登录 Windows 时会自动启动本程序。"
                        : "已关闭开机自动启动。",
                    "开机自动启动", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show($"设置开机自动启动失败：\n{error}", "开机自动启动",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 后台刷新 <see cref="_autoStartEnabled"/> 缓存，回填时切回 UI 线程。
        /// 只在启动时调用一次；托盘菜单 Opening 与 <see cref="ToggleAutoStart"/> 那两处是低频且需要即时准确的
        /// 时机，仍走同步查询。查询失败不弹窗——它只影响一句描述文本，<see cref="AutoStartManager"/> 内部已记日志。
        /// </summary>
        private void RefreshAutoStartStateAsync()
        {
            Task.Run(() =>
            {
                bool enabled = AutoStartManager.IsEnabled();
                Dispatcher.BeginInvoke(new Action(() => _autoStartEnabled = enabled));
            });
        }

        private void ExecuteCommandImpl(Command selectedCommand)
        {
            if (ExecuteAppCommand(selectedCommand))
            {
                return;
            }

            string commandName = selectedCommand.Name;
            string commandShell = selectedCommand.Shell;
            bool useShellExecute = selectedCommand.UseShellExecute;
            bool runAsAdmin = selectedCommand.RunAsAdmin;

            string workingDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var processInfo = ParseShellCommand(commandShell);

            // launcher 自身以管理员运行，直接启动的子进程会继承管理员令牌。
            // 默认借用桌面 Shell 令牌降权为普通用户权限启动；RunAsAdmin=true 时才保留管理员权限。
            if (!runAsAdmin)
            {
                Logger.LogInfo($"执行命令(普通权限): {commandName} ({commandShell})");
                // 失败时抛异常，由 ExecuteCommand 的 catch 弹窗报错、不启动（不回退到管理员）。
                MediumIntegrityProcess.Start(processInfo.FileName, processInfo.Arguments, workingDir);
                return;
            }

            Logger.LogInfo($"执行命令(管理员权限): {commandName} ({commandShell}), UseShellExecute={useShellExecute}");

            processInfo.UseShellExecute = useShellExecute;
            processInfo.CreateNoWindow = false;
            processInfo.RedirectStandardError = !useShellExecute;
            processInfo.WorkingDirectory = workingDir;

            var process = Process.Start(processInfo);
            if (process == null)
            {
                Logger.LogWarning($"命令已启动但无法获取进程句柄（可能由 OS 复用已有进程）: {commandName} ({commandShell})");
                return;
            }

            var stderrBuilder = new System.Text.StringBuilder();
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null)
                    stderrBuilder.AppendLine(e.Data);
            };
            process.BeginErrorReadLine();

            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                int exitCode = process.ExitCode;
                string stderr = stderrBuilder.ToString().Trim();

                if (exitCode != 0 && useShellExecute)
                {
                    string logDetail = string.IsNullOrEmpty(stderr)
                        ? $"ExitCode={exitCode}"
                        : $"ExitCode={exitCode}, Stderr: {stderr}";
                    Logger.LogError($"命令执行失败: {commandName} ({commandShell}), {logDetail}",
                        new Exception($"Process exited with code {exitCode}"));

                    string popupMsg = string.IsNullOrEmpty(stderr)
                        ? $"命令: {commandName}\n退出码: {exitCode}"
                        : $"命令: {commandName}\n退出码: {exitCode}\n\n错误信息:\n{stderr}";
                    Dispatcher.InvokeAsync(() =>
                        MessageBox.Show(popupMsg, "执行失败", MessageBoxButton.OK, MessageBoxImage.Error));
                }
                else
                {
                    Logger.LogInfo($"命令执行完毕: {commandName} ({commandShell}), ExitCode={exitCode}");
                }

                process.Dispose();
            };
        }

        private void ExecuteCommand(Command selectedCommand)
        {
            try
            {
                ExecuteCommandImpl(selectedCommand);
                AppState.Instance.RecordCommandExecution(selectedCommand.Name);
                HideWindow();
            }
            catch (Exception ex)
            {
                Logger.LogError($"执行命令失败: {selectedCommand.Name}", ex);
                MessageBox.Show($"执行命令失败: {ex.Message}");
            }
        }

        private void ExecuteSelectedCommand()
        {
            if (_commandList.SelectedItem is Command selectedCommand)
            {
                ExecuteCommand(selectedCommand);
            }
        }

        private static ProcessStartInfo ParseShellCommand(string shell)
        {
            if (string.IsNullOrWhiteSpace(shell))
            {
                return new ProcessStartInfo("cmd.exe");
            }

            shell = shell.Trim();

            // 如果只有可执行文件路径（可能带引号但无参数）
            if (!shell.Contains(' ') ||
                (shell.StartsWith("\"") && shell.EndsWith("\"") && shell.Count(c => c == '"') == 2))
            {
                return new ProcessStartInfo(shell.Trim('"'));
            }

            string fileName;
            string arguments = string.Empty;

            if (shell.StartsWith("\""))
            {
                // 路径被引号包裹，查找下一个引号作为路径结束
                var end = shell.IndexOf('"', 1);
                if (end == -1)
                {
                    // 引号不完整，直接作为文件名处理
                    fileName = shell.Trim('"');
                }
                else
                {
                    fileName = shell.Substring(1, end - 1);
                    arguments = shell[(end + 1)..].Trim();
                }
            }
            else
            {
                var index = shell.IndexOf(' ');
                if (index == -1)
                {
                    fileName = shell;
                }
                else
                {
                    fileName = shell[..index];
                    arguments = shell[(index + 1)..].Trim();
                }
            }

            return new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments
            };
        }

        private void HideWindow()
        {
            _activationTimer.Stop();
            Hide();
            _searchBox.Clear();
            _placeholder.Visibility = Visibility.Visible;
        }

        private void ExitApplication()
        {
            // 更新下载/安装进行中时先跟用户确认：此刻强退会中断下载，极小概率还会打断 exe 替换。
            // 用户坚持退出的话必须真能退出——所以随后要放行 UpdateWindow 的关闭拦截，
            // 否则下面 Dispose 已经摘掉托盘图标与热键，Shutdown 却被那个拦截取消，程序会陷入无入口的半死状态。
            if (UpdateInstaller.IsBusy)
            {
                var choice = MessageBox.Show(
                    "更新正在进行中，现在退出会中断更新。确定退出吗？",
                    "退出", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (choice != MessageBoxResult.Yes)
                    return;

                Logger.LogWarning("用户在更新进行中选择退出程序");
            }

            UpdateWindow.PrepareForApplicationShutdown();

            Logger.LogInfo("程序正在退出");
            Dispose();
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            Dispose();
            base.OnClosed(e);
        }

        // 失焦即隐藏。但显示后的宽限期内失焦多半是激活序列的瞬时抖动
        // （前台被原窗口短暂夺回），此时应重试激活而不是隐藏，否则热键唤出会「一闪即隐」，
        // 用户看到的现象就是「按了 Ctrl+Shift+I 完全没反应」。
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);

            if (!IsVisible)
                return;

            if (Environment.TickCount64 < _graceUntil)
            {
                TryActivateOnce();
                return;
            }

            HideWindow();
        }
    }
}
