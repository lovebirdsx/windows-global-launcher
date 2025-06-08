using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

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
        private readonly ObservableCollection<Command> _filteredCommands = [];
        private readonly HotKeyListener _hotKeyListener = new();
        private readonly System.Windows.Forms.NotifyIcon _notifyIcon = new() { Text = "Command Launcher", Visible = true };
        private readonly string _configPath;
        private int _selectedIndex = 0;
        private bool _disposed = false;

        private readonly TextBox _searchBox = CreateSearchBox();
        private readonly ListBox _commandList = CreateCommandList();

        public MainWindow()
        {
            // 支持命令行指定配置文件路径
            var args = Environment.GetCommandLineArgs();
            _configPath = args.Length > 1 ? args[1] : "config.json";

            Logger.LogInfo($"开始初始化主窗口，配置文件: {_configPath}");
            InitializeComponent();
            SetupNotifyIcon();
            SetupHotKeyListener();
            SetupUI();

            WindowState = WindowState.Minimized;
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

            // 主容器
            var mainGrid = new Grid
            {
                Margin = new Thickness(12) // 增加整体边距
            };

            // 搜索框
            _searchBox.TextChanged += SearchBox_TextChanged;
            _searchBox.KeyDown += SearchBox_KeyDown;

            // 设置ListBox样式 - 更宽松的设计
            var listBoxStyle = new Style(typeof(ListBoxItem));
            listBoxStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(15, 8, 5, 8))); // 右侧padding减少，让shell命令更靠右
            listBoxStyle.Setters.Add(new Setter(MarginProperty, new Thickness(2)));
            listBoxStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
            listBoxStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));

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
            var leftColumn = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
            var rightColumn = new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star), MaxWidth = double.PositiveInfinity };
            gridFactory.AddHandler(
                LoadedEvent,
                new RoutedEventHandler((s, e) =>
                {
                    var g = (Grid)s;
                    g.ColumnDefinitions.Clear();
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.5, GridUnitType.Star), MaxWidth = double.PositiveInfinity });
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

            // 右侧Shell命令
            var shellTextBlock = new FrameworkElementFactory(typeof(TextBlock));
            shellTextBlock.SetValue(Grid.ColumnProperty, 1);
            shellTextBlock.SetBinding(TextBlock.TextProperty, new Binding("Shell"));
            shellTextBlock.SetValue(TextBlock.FontSizeProperty, 10.0);
            shellTextBlock.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)));
            shellTextBlock.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            shellTextBlock.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Right);
            shellTextBlock.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Consolas, Courier New"));
            shellTextBlock.SetValue(MarginProperty, new Thickness(10, 0, 0, 0));
            shellTextBlock.SetValue(TextBlock.TextWrappingProperty, TextWrapping.Wrap); // 启用自动换行
            shellTextBlock.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right); // 右对齐
            shellTextBlock.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis); // 添加文本截断
            shellTextBlock.SetValue(MaxWidthProperty, 300.0); // 设置最大宽度，避免过宽
            gridFactory.AppendChild(shellTextBlock);

            dataTemplate.VisualTree = gridFactory;
            _commandList.ItemTemplate = dataTemplate;
            ScrollViewer.SetVerticalScrollBarVisibility(_commandList, ScrollBarVisibility.Hidden);

            mainGrid.Children.Add(_searchBox);
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
                contextMenu.Items.Add("设定配置文件", null, (s, e) => AppConfig.SetConfigFile());
                contextMenu.Items.Add("打开配置文件", null, (s, e) => AppConfig.OpenConfigFile());
                contextMenu.Items.Add("打开日志文件", null, (s, e) => Logger.OpenLogFile());
                contextMenu.Items.Add("退出", null, (s, e) => ExitApplication());
                _notifyIcon.ContextMenuStrip = contextMenu;

                _notifyIcon.DoubleClick += (s, e) => ShowWindow();
                Logger.LogInfo("系统托盘图标设置成功");
            }
            catch (Exception ex)
            {
                Logger.LogError("设置系统托盘图标失败", ex);
            }
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
            // 使用配置文件中的热键
            if (!_hotKeyListener.RegisterHotKey(AppConfig.Instance.Config.HotKey))
            {
                Logger.LogWarning($"注册热键失败: {AppConfig.Instance.Config.HotKey}，使用默认热键 {AppConfig.DefaultHotKey}");
                _hotKeyListener.RegisterHotKey(AppConfig.DefaultHotKey);
            }

            // 配置更新时——一定要切回到 UI 线程
            AppConfig.Instance.ConfigUpdated += () =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    _hotKeyListener.UnregisterHotKey();

                    var newHotKey = AppConfig.Instance.Config.HotKey;
                    if (!_hotKeyListener.RegisterHotKey(newHotKey))
                    {
                        Logger.LogWarning($"重新注册热键失败: {newHotKey}, 使用默认热键 {AppConfig.DefaultHotKey}");
                        _hotKeyListener.RegisterHotKey(AppConfig.DefaultHotKey);
                    }
                });
            };

            _hotKeyListener.HotKeyPressed += ShowWindow;
        }


        private void ShowWindow()
        {
            Show();
            WindowState = WindowState.Normal;

            CenterWindowOnCurrentScreen();
            Activate();

            _searchBox.Focus();
            _searchBox.SelectAll();

            RefreshCommandList();
            SelectLastExecutedCommand();
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
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime(configCmd.Name),
            }).ToList();

            // 加入对本应用程序的特殊处理
            commands.Add(new Command
            {
                Name = "config",
                Description = "打开windows-global-launcher的配置文件",
                Shell = "config",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("config")
            });

            commands.Add(new Command
            {
                Name = "setconfig",
                Description = "设定windows-global-launcher的配置文件",
                Shell = "setconfig",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("setconfig")
            });

            commands.Add(new Command
            {
                Name = "logs",
                Description = "打开windows-global-launcher的日志文件",
                Shell = "logs",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("logs")
            });

            commands.Add(new Command
            {
                Name = "exit",
                Description = "退出windows-global-launcher",
                Shell = "exit",
                LastExecuted = AppState.Instance.GetCommandLastExecutedTime("exit")
            });

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

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchBox = (TextBox)sender;
            RefreshCommandList(searchBox.Text);

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

        private void ExecuteCommand(Command selectedCommand)
        {
            if (selectedCommand.Name == "config")
            {
                AppConfig.OpenConfigFile();
                HideWindow();
                return;
            }
            else if (selectedCommand.Name == "setconfig")
            {
                AppConfig.SetConfigFile();
                HideWindow();
                return;
            }
            else if (selectedCommand.Name == "logs")
            {
                Logger.OpenLogFile();
                HideWindow();
                return;
            }
            else if (selectedCommand.Name == "exit")
            {
                AppState.Instance.SetCommandLastExecutedTime(selectedCommand.Name, DateTime.Now);
                ExitApplication();
                return;
            }

            try
            {
                Logger.LogInfo($"执行命令: {selectedCommand.Name} ({selectedCommand.Shell})");

                var processInfo = ParseShellCommand(selectedCommand.Shell);
                processInfo.UseShellExecute = true;
                processInfo.CreateNoWindow = false;
                processInfo.WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                Process.Start(processInfo);

                AppState.Instance.SetCommandLastExecutedTime(selectedCommand.Name, DateTime.Now);

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
            Hide();
            _searchBox.Clear();
        }

        private void ExitApplication()
        {
            Logger.LogInfo("程序正在退出");
            Dispose();
            Application.Current.Shutdown();
        }

        protected override void OnClosed(EventArgs e)
        {
            Dispose();
            base.OnClosed(e);
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
            HideWindow();
        }
    }
}
