using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;

namespace CommandLauncher
{
    /// <summary>
    /// 快捷键设置窗口：可视化查看/录制/清除三类热键——命令面板唤出热键、窗口动作热键、命令触发热键。
    /// <para>
    /// 改动即时生效：写回内存配置 → <see cref="AppConfig.SaveAndReload"/> → ConfigUpdated →
    /// MainWindow 重注册启动器热键 / SwitcherWindow 重装动作绑定，无「保存」按钮。
    /// </para>
    /// <para>
    /// 录制走 <see cref="KeyboardHook"/> 的录制模式（低级钩子）：F4/F7/Win+Q 这类会被自身钩子吞掉、
    /// 或被系统处理的组合，只有钩子路径能录到。录制期间全局吞键，靠四道安全网保证不卡死——
    /// ① 窗口失活；② 关窗；③ 15 秒超时；④ 录制回调异常。另加 Esc 兜底（钩子不在时的窗口级兜底）。
    /// </para>
    /// </summary>
    public sealed class HotkeySettingsWindow : Window
    {
        #region 配色（与 UpdateWindow/OcrResultWindow 一致；项目惯例为各窗口自带一份，不提取公共类）

        private static readonly Color WindowBackground = Color.FromRgb(30, 30, 30);        // #1E1E1E
        private static readonly Color HotKeyBoxBackground = Color.FromRgb(37, 37, 38);     // #252526
        private static readonly Color NormalForeground = Color.FromRgb(224, 224, 224);     // #E0E0E0
        private static readonly Color HintForeground = Color.FromArgb(255, 150, 150, 150);
        private static readonly Color AccentBlue = Color.FromRgb(0, 120, 212);
        private static readonly Color AccentBlueHover = Color.FromRgb(0, 100, 180);
        private static readonly Color WarnOrange = Color.FromRgb(224, 154, 0);             // 冲突/非法标色
        private static readonly Color BoxBorder = Color.FromArgb(90, 255, 255, 255);
        private static readonly Color SeparatorColor = Color.FromRgb(42, 42, 42);
        private static readonly Color SectionHeaderColor = Color.FromRgb(190, 190, 190);
        private static readonly Color DisabledForeground = Color.FromRgb(120, 120, 120);

        #endregion

        private const double WindowWidth = 660;
        private const double WindowHeight = 660;
        private const double TitleBarHeight = 34;
        private const double RowHeight = 52;
        private const double BottomBarHeight = 52;

        /// <summary>录制超时：超时自动取消录制，防止录制态意外滞留导致全局吞键。</summary>
        private const int RecordingTimeoutMs = 15_000;

        // 校验用的虚拟键码
        private const int VK_TAB = 0x09;
        private const int VK_ESCAPE = 0x1B;
        private const int VK_DELETE = 0x2E;
        private const int VK_BACK = 0x08;
        private const int VK_L = 0x4C;
        private const int VK_F1 = 0x70;
        private const int VK_F12 = 0x7B;

        /// <summary>当前打开的设置窗口（单例，Closed 时清空）。仅在 UI 线程读写。</summary>
        private static HotkeySettingsWindow? _current;

        private enum SlotKind { Launcher, WindowAction, Command }

        /// <summary>行槽位描述符：只记录「这一行是谁」，不持有配置对象引用
        /// （LoadConfig 每次重载都会替换配置对象，持旧引用会把编辑写进废弃对象被静默丢弃）。</summary>
        private sealed record Slot(SlotKind Kind, string? ActionName, int CommandIndex);

        /// <summary>一行热键设置的控件引用（重建行时整体替换）。</summary>
        private sealed class RowView
        {
            public required Slot Slot { get; init; }
            public required Border Box { get; init; }
            public required TextBlock BoxText { get; init; }
            public required Button? ClearButton { get; init; }
            public required CheckBox? EnabledCheck { get; init; }
        }

        private enum StatusKind { Hint, Recording, Error }

        // 控件
        private readonly StackPanel _sections = new();
        private readonly ScrollViewer _scroll = new();
        private TextBlock _statusText = null!;
        private TextBlock _conflictSummary = null!;

        private readonly List<RowView> _rows = [];
        private readonly DispatcherTimer _recordingTimer = new() { Interval = TimeSpan.FromMilliseconds(RecordingTimeoutMs) };

        private Slot? _recordingSlot;
        private RowView? _recordingRow;

        /// <summary>自己触发的保存会引发 ConfigUpdated，置位期间跳过自我刷新（保存后显式重建）。</summary>
        private bool _suppressSelfRefresh;
        private bool _closing;

        public HotkeySettingsWindow()
        {
            InitializeComponent();
            _recordingTimer.Tick += (s, e) =>
            {
                EndRecording();
                SetStatus("录制已超时取消，点击热键框可重新录制", StatusKind.Hint);
            };

            RebuildRows();
            SetStatus("点击热键框后按下新组合键；Esc 取消，Backspace/Delete 清除。修改即时生效。");

            // 外部编辑配置文件时刷新显示（命名方法订阅，OnClosed 必须退订，避免短命窗口被静态事件持有）
            AppConfig.Instance.ConfigUpdated += OnConfigUpdated;

            Loaded += (s, e) =>
            {
                if (!IsActive)
                    ActivateWindow();
            };
        }

        /// <summary>显示设置窗口。同一时刻只保留一个实例，已打开则激活既有窗口。</summary>
        public static void ShowFor()
        {
            if (_current != null)
            {
                _current.ActivateWindow();
                return;
            }

            var window = new HotkeySettingsWindow();
            _current = window;
            window.Show();
            window.ActivateWindow();
        }

        /// <summary>
        /// 兜底激活：本窗口可能由全局钩子动作（OpenHotkeySettings）触发，键盘输入未进入本进程队列、
        /// 直接 Activate 会被前台锁定规则挡下，故经 WindowEnumerator.Activate 的 AttachThreadInput 技巧。
        /// </summary>
        private void ActivateWindow()
        {
            try
            {
                if (WindowState == WindowState.Minimized)
                    WindowState = WindowState.Normal;

                var hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    WindowEnumerator.Activate(hwnd);
                Activate();
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"激活快捷键设置窗口失败: {ex.Message}");
            }
        }

        // ================= 界面构建 =================

        private void InitializeComponent()
        {
            Title = "快捷键设置";
            Width = WindowWidth;
            Height = WindowHeight;
            MinWidth = 560;
            MinHeight = 420;
            WindowStyle = WindowStyle.None;                  // 无系统标题栏
            ResizeMode = ResizeMode.CanResize;                // 纯边缘拖拽调整大小
            Background = new SolidColorBrush(WindowBackground);
            ShowInTaskbar = true;                             // 用户可见对话框（区别于 OCR 结果窗）
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            // 刻意不设 Topmost：这是会长时间停留、用户可能切出去对照测试的窗口

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

            var dragArea = new Border { Background = Brushes.Transparent };
            Grid.SetRow(dragArea, 0);
            Grid.SetColumn(dragArea, 0);
            dragArea.MouseLeftButtonDown += OnTitleBarMouseLeftButtonDown;
            dragArea.Child = new TextBlock
            {
                Text = "快捷键设置",
                FontSize = 12,
                Foreground = new SolidColorBrush(SectionHeaderColor),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(14, 0, 0, 0),
            };
            titleBar.Children.Add(dragArea);

            var closeTitleButton = new Button
            {
                Content = "✕",
                Style = MakeTitleBarCloseButtonStyle(),
                Width = 40,
                VerticalAlignment = VerticalAlignment.Stretch,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            closeTitleButton.Click += (s, e) => Close();
            Grid.SetRow(closeTitleButton, 0);
            Grid.SetColumn(closeTitleButton, 1);
            titleBar.Children.Add(closeTitleButton);

            var titleSeparator = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            Grid.SetRow(titleSeparator, 1);
            Grid.SetColumnSpan(titleSeparator, 2);
            titleBar.Children.Add(titleSeparator);

            // ===== 状态行：左侧提示（录制/错误状态变色），右侧冲突汇总 =====
            _statusText = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(HintForeground),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _conflictSummary = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(HintForeground),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            var statusRow = new Grid { Margin = new Thickness(14, 8, 14, 6) };
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            statusRow.Children.Add(_statusText);
            Grid.SetColumn(_conflictSummary, 1);
            statusRow.Children.Add(_conflictSummary);

            // ===== 主体：滚动列表（分区 + 行） =====
            _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            _scroll.Padding = new Thickness(0, 0, 0, 8);
            _scroll.Content = _sections;
            ApplyFlatScrollBar();

            // ===== 底部栏：左「恢复默认」，右「关闭」 =====
            var restoreButton = new Button
            {
                Content = "恢复默认",
                Style = MakeLinkButtonStyle(),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "重置命令面板热键与窗口动作热键（含启用状态），不影响命令热键",
            };
            restoreButton.Click += (s, e) => RestoreDefaults();

            var closeButton = new Button
            {
                Content = "关闭",
                Style = MakeButtonStyle(accent: false),
                MinWidth = 72,
                VerticalAlignment = VerticalAlignment.Center,
            };
            closeButton.Click += (s, e) => Close();

            var bottomBar = new Grid { Height = BottomBarHeight, Margin = new Thickness(14, 0, 12, 0) };
            bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            bottomBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            bottomBar.Children.Add(restoreButton);
            Grid.SetColumn(closeButton, 1);
            bottomBar.Children.Add(closeButton);

            // ===== 行布局：标题栏 / 状态行 / 列表（主体）/ 底部栏 =====
            var grid = new Grid { Margin = new Thickness(1) };
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(TitleBarHeight) });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BottomBarHeight) });

            Grid.SetRow(titleBar, 0);
            Grid.SetRow(statusRow, 1);
            Grid.SetRow(_scroll, 2);
            Grid.SetRow(bottomBar, 3);

            grid.Children.Add(titleBar);
            grid.Children.Add(statusRow);
            grid.Children.Add(_scroll);
            grid.Children.Add(bottomBar);

            Content = new Border
            {
                Background = new SolidColorBrush(WindowBackground),
                BorderBrush = new SolidColorBrush(BoxBorder),
                BorderThickness = new Thickness(1),
                Child = grid,
            };

            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnTitleBarMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch (InvalidOperationException) { }
            }
        }

        /// <summary>既有行列表的重建入口（首次打开、每次保存、外部配置变化时调用）。</summary>
        private void RebuildRows()
        {
            double scrollOffset = _scroll.VerticalOffset;

            _rows.Clear();
            _sections.Children.Clear();

            // 命令面板唤出热键（不允许清除，至少一个修饰键）
            _sections.Children.Add(CreateSectionHeader("命令面板"));
            _sections.Children.Add(CreateRow(
                new Slot(SlotKind.Launcher, null, -1),
                "唤出命令面板",
                "全局热键，在任何窗口下唤出命令面板",
                showClear: false,
                showEnabled: false));

            // 窗口动作（注册表顺序即展示顺序，含默认未绑定的动作）
            _sections.Children.Add(CreateSectionHeader("窗口动作（全局生效）"));
            foreach (var descriptor in WindowActions.AllActions)
            {
                _sections.Children.Add(CreateRow(
                    new Slot(SlotKind.WindowAction, descriptor.Name, -1),
                    descriptor.DisplayName,
                    descriptor.Description,
                    showClear: true,
                    showEnabled: true));
            }

            // 命令触发热键（仅命令面板打开时生效）
            _sections.Children.Add(CreateSectionHeader("命令（仅命令面板打开时生效）"));
            var commands = AppConfig.Instance.Config.Commands;
            if (commands.Count == 0)
            {
                _sections.Children.Add(CreateEmptyHint("配置文件中还没有命令"));
            }
            else
            {
                for (int i = 0; i < commands.Count; i++)
                {
                    var command = commands[i];
                    _sections.Children.Add(CreateRow(
                        new Slot(SlotKind.Command, null, i),
                        command.Name,
                        string.IsNullOrWhiteSpace(command.Description) ? command.Shell : command.Description,
                        showClear: true,
                        showEnabled: false));
                }
            }

            // 重建后重新解析录制行（Slot 为值相等，可跨重建找回同一槽位）
            if (_recordingSlot is { } slot)
                _recordingRow = _rows.FirstOrDefault(r => r.Slot == slot);

            RefreshRowVisuals();
            _scroll.ScrollToVerticalOffset(scrollOffset);
        }

        private static TextBlock CreateSectionHeader(string text) => new()
        {
            Text = text,
            FontSize = 13,
            Foreground = new SolidColorBrush(SectionHeaderColor),
            Margin = new Thickness(14, 12, 14, 6),
        };

        private static TextBlock CreateEmptyHint(string text) => new()
        {
            Text = text,
            FontSize = 12,
            Foreground = new SolidColorBrush(HintForeground),
            Margin = new Thickness(14, 4, 14, 4),
        };

        /// <summary>创建一行：名称/描述 + 热键框（点击录制）+ 清除 + 启用勾选。</summary>
        private Border CreateRow(Slot slot, string name, string description, bool showClear, bool showEnabled)
        {
            var grid = new Grid { Height = RowHeight, Margin = new Thickness(14, 0, 14, 0) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            // 「清除」「启用」两列用固定宽度而非 Auto：不显示这两列的行走（命令面板行）也要占住同样宽度，
            // 否则各行的热键框横向位置对不齐
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });

            var textPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            textPanel.Children.Add(new TextBlock
            {
                Text = name,
                FontSize = 14,
                Foreground = new SolidColorBrush(NormalForeground),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            textPanel.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 11,
                Foreground = new SolidColorBrush(HintForeground),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            grid.Children.Add(textPanel);

            var boxText = new TextBlock
            {
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 10, 0),
            };
            var box = new Border
            {
                MinWidth = 150,
                Height = 30,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(HotKeyBoxBackground),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(BoxBorder),
                Cursor = Cursors.Hand,
                Child = boxText,
                VerticalAlignment = VerticalAlignment.Center,
            };
            box.MouseLeftButtonDown += (s, e) =>
            {
                e.Handled = true;
                StartRecording(slot);
            };
            Grid.SetColumn(box, 1);
            grid.Children.Add(box);

            Button? clearButton = null;
            if (showClear)
            {
                clearButton = new Button
                {
                    Content = "清除",
                    Style = MakeLinkButtonStyle(),
                    Margin = new Thickness(12, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "清除该热键（录制中按 Backspace/Delete 同效）",
                };
                clearButton.Click += (s, e) => ClearBinding(slot);
                Grid.SetColumn(clearButton, 2);
                grid.Children.Add(clearButton);
            }

            CheckBox? enabledCheck = null;
            if (showEnabled)
            {
                enabledCheck = new CheckBox
                {
                    Style = MakeCheckBoxStyle(),
                    Margin = new Thickness(16, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "启用/禁用该动作热键（禁用后热键不生效，但绑定保留）",
                };
                enabledCheck.Click += (s, e) => SetEnabled(slot, enabledCheck.IsChecked == true);
                Grid.SetColumn(enabledCheck, 3);
                grid.Children.Add(enabledCheck);
            }

            var row = new Border
            {
                Child = grid,
                BorderBrush = new SolidColorBrush(SeparatorColor),
                BorderThickness = new Thickness(0, 0, 0, 1),
            };

            _rows.Add(new RowView
            {
                Slot = slot,
                Box = box,
                BoxText = boxText,
                ClearButton = clearButton,
                EnabledCheck = enabledCheck,
            });
            return row;
        }

        // ================= 配置读写（按槽位定位，不持有配置对象引用） =================

        private static ConfigWindowAction? FindActionEntry(Config config, string actionName)
            => config.WindowActions.FirstOrDefault(a => string.Equals(a.Action, actionName, StringComparison.OrdinalIgnoreCase));

        private static string GetHotKey(Slot slot)
        {
            var config = AppConfig.Instance.Config;
            return slot.Kind switch
            {
                SlotKind.Launcher => config.HotKey ?? "",
                SlotKind.WindowAction => FindActionEntry(config, slot.ActionName!)?.HotKey ?? "",
                SlotKind.Command => slot.CommandIndex >= 0 && slot.CommandIndex < config.Commands.Count
                    ? config.Commands[slot.CommandIndex].HotKey ?? ""
                    : "",
                _ => "",
            };
        }

        private static bool GetEnabled(Slot slot)
        {
            if (slot.Kind != SlotKind.WindowAction)
                return true;

            return FindActionEntry(AppConfig.Instance.Config, slot.ActionName!)?.Enabled ?? false;
        }

        /// <summary>把槽位对应的热键写入内存配置（窗口动作缺条目时补一条）。返回是否写成功。</summary>
        private static bool TryWriteHotKey(Slot slot, string hotKey)
        {
            var config = AppConfig.Instance.Config;
            switch (slot.Kind)
            {
                case SlotKind.Launcher:
                    config.HotKey = hotKey;
                    return true;

                case SlotKind.WindowAction:
                    var entry = FindActionEntry(config, slot.ActionName!);
                    if (entry == null)
                    {
                        entry = new ConfigWindowAction { Action = slot.ActionName!, Enabled = true };
                        config.WindowActions.Add(entry);
                    }
                    entry.HotKey = hotKey;
                    return true;

                case SlotKind.Command:
                    if (slot.CommandIndex < 0 || slot.CommandIndex >= config.Commands.Count)
                        return false;
                    config.Commands[slot.CommandIndex].HotKey = hotKey;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryWriteEnabled(Slot slot, bool enabled)
        {
            if (slot.Kind != SlotKind.WindowAction)
                return false;

            var entry = FindActionEntry(AppConfig.Instance.Config, slot.ActionName!);
            if (entry == null)
                return false;

            entry.Enabled = enabled;
            return true;
        }

        /// <summary>槽位展示名（冲突提示与日志用）。</summary>
        private static string GetSlotLabel(Slot slot) => slot.Kind switch
        {
            SlotKind.Launcher => "命令面板唤出热键",
            SlotKind.WindowAction => WindowActions.All.TryGetValue(slot.ActionName!, out var descriptor)
                ? descriptor.DisplayName
                : slot.ActionName!,
            SlotKind.Command => slot.CommandIndex >= 0 && slot.CommandIndex < AppConfig.Instance.Config.Commands.Count
                ? $"命令「{AppConfig.Instance.Config.Commands[slot.CommandIndex].Name}」"
                : "命令",
            _ => "未知项",
        };

        // ================= 行视觉（含冲突标黄） =================

        /// <summary>刷新全部行的显示：热键文本、清除按钮可用性、启用勾选、冲突/非法标色、冲突汇总。</summary>
        private void RefreshRowVisuals()
        {
            // 1) 计算生效绑定之间的冲突（启动器恒生效；窗口动作要求 Enabled；命令恒生效；
            //    不可解析的热键不参与——它们本来就不会生效，另行标为「无法解析」）
            var activeBindings = new List<(RowView Row, string Label, string Normalized, bool IsLauncher)>();
            foreach (var row in _rows)
            {
                string hotKey = GetHotKey(row.Slot);
                if (string.IsNullOrWhiteSpace(hotKey))
                    continue;
                if (row.Slot.Kind == SlotKind.WindowAction && !GetEnabled(row.Slot))
                    continue;
                if (HotKeyParser.Normalize(hotKey) is not { } normalized)
                    continue;
                if (!HotKeyParser.TryParse(hotKey, out _, out _, out _, out _, out _))
                    continue;

                activeBindings.Add((row, GetSlotLabel(row.Slot), normalized, row.Slot.Kind == SlotKind.Launcher));
            }

            var conflictTooltips = new Dictionary<RowView, string>();
            int conflictCount = 0;
            foreach (var group in activeBindings.GroupBy(b => b.Normalized, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                bool withLauncher = group.Any(b => b.IsLauncher);
                foreach (var binding in group)
                {
                    conflictCount++;
                    var otherLabels = group.Where(b => !ReferenceEquals(b.Row, binding.Row))
                        .Select(b => b.Label).Distinct().ToList();
                    string others = string.Join("」「", otherLabels);

                    conflictTooltips[binding.Row] = withLauncher
                        ? (binding.IsLauncher
                            ? $"与「{others}」冲突：该组合会被窗口动作钩子抢先吞掉，命令面板将无法唤出"
                            : $"与「命令面板唤出热键」冲突：该组合被本项抢先吞掉，命令面板将无法唤出")
                        : $"与「{others}」重复：同时按下时只有配置中先命中的一项生效";
                }
            }

            // 2) 逐行应用视觉
            foreach (var row in _rows)
            {
                string hotKey = GetHotKey(row.Slot);
                bool empty = string.IsNullOrWhiteSpace(hotKey);
                bool parseable = !empty && HotKeyParser.TryParse(hotKey, out _, out _, out _, out _, out _);

                if (row.ClearButton != null)
                    row.ClearButton.IsEnabled = !empty;

                if (row.EnabledCheck != null)
                    row.EnabledCheck.IsChecked = GetEnabled(row.Slot);

                if (ReferenceEquals(row, _recordingRow))
                {
                    row.BoxText.Text = "请按下快捷键…";
                    row.BoxText.Foreground = new SolidColorBrush(AccentBlue);
                    row.Box.BorderBrush = new SolidColorBrush(AccentBlue);
                    row.Box.BorderThickness = new Thickness(1.5);
                    row.Box.ToolTip = null;
                    continue;
                }

                if (empty)
                {
                    row.BoxText.Text = "未设置";
                    row.BoxText.Foreground = new SolidColorBrush(HintForeground);
                    row.Box.BorderBrush = new SolidColorBrush(BoxBorder);
                    row.Box.BorderThickness = new Thickness(1);
                    row.Box.ToolTip = row.Slot.Kind == SlotKind.WindowAction && !GetEnabled(row.Slot)
                        ? "该动作已禁用（勾选右侧「启用」后生效）"
                        : null;
                    continue;
                }

                if (!parseable)
                {
                    // 配置里手写的非法热键：显原串并标黄，提示不会生效
                    row.BoxText.Text = hotKey.Trim();
                    row.BoxText.Foreground = new SolidColorBrush(WarnOrange);
                    row.Box.BorderBrush = new SolidColorBrush(WarnOrange);
                    row.Box.BorderThickness = new Thickness(1.5);
                    row.Box.ToolTip = "无法解析该热键，不会生效";
                    continue;
                }

                row.BoxText.Text = FormatDisplay(hotKey);
                if (conflictTooltips.TryGetValue(row, out var tip))
                {
                    row.BoxText.Foreground = new SolidColorBrush(NormalForeground);
                    row.Box.BorderBrush = new SolidColorBrush(WarnOrange);
                    row.Box.BorderThickness = new Thickness(1.5);
                    row.Box.ToolTip = tip;
                }
                else
                {
                    bool disabled = row.Slot.Kind == SlotKind.WindowAction && !GetEnabled(row.Slot);
                    row.BoxText.Foreground = new SolidColorBrush(disabled ? DisabledForeground : NormalForeground);
                    row.Box.BorderBrush = new SolidColorBrush(BoxBorder);
                    row.Box.BorderThickness = new Thickness(1);
                    row.Box.ToolTip = disabled ? "该动作已禁用（勾选右侧「启用」后生效）" : "点击后按下新的组合键";
                }
            }

            _conflictSummary.Text = conflictCount == 0 ? "无冲突" : $"发现 {conflictCount} 处冲突（已标黄，仍会保存）";
            _conflictSummary.Foreground = new SolidColorBrush(conflictCount == 0 ? HintForeground : WarnOrange);
        }

        /// <summary>展示用热键串：可解析则用规范串（大小写/修饰键顺序/ESCAPE 别名统一），否则原样。</summary>
        private static string FormatDisplay(string hotKey)
        {
            if (HotKeyParser.TryParse(hotKey, out int vk, out bool ctrl, out bool alt, out bool shift, out bool win))
                return HotKeyParser.Format(vk, ctrl, alt, shift, win) ?? hotKey.Trim();

            return hotKey.Trim();
        }

        private void SetStatus(string message, StatusKind kind = StatusKind.Hint)
        {
            _statusText.Text = message;
            _statusText.Foreground = new SolidColorBrush(kind switch
            {
                StatusKind.Recording => AccentBlue,
                StatusKind.Error => WarnOrange,
                _ => HintForeground,
            });
        }

        // ================= 录制 =================

        /// <summary>点击热键框：进入录制态（低级钩子捕获下一次按键）。</summary>
        private void StartRecording(Slot slot)
        {
            var switcher = SwitcherWindow.Current;
            if (switcher == null)
            {
                SetStatus("录制不可用：窗口切换器未初始化（程序可能正在退出）", StatusKind.Error);
                return;
            }

            if (_recordingSlot != null)
                EndRecording();

            _recordingSlot = slot;
            _recordingRow = _rows.FirstOrDefault(r => r.Slot == slot);
            RefreshRowVisuals();
            SetStatus("请按下新的快捷键…（Esc 取消，Backspace/Delete 清除）", StatusKind.Recording);

            _recordingTimer.Stop();
            _recordingTimer.Start();

            try
            {
                switcher.BeginKeyRecording(OnKeyCapturedFromHook);
            }
            catch (Exception ex)
            {
                Logger.LogError("进入按键录制失败", ex);
                EndRecording();
                SetStatus("进入录制失败，请重试", StatusKind.Error);
            }
        }

        /// <summary>钩子录制回调（运行在钩子栈上）：只做转发，实际处理在 dispatcher 上下文中执行。</summary>
        private void OnKeyCapturedFromHook(int vk, bool ctrl, bool alt, bool shift, bool win)
        {
            Dispatcher.BeginInvoke(() => HandleCapturedKey(vk, ctrl, alt, shift, win));
        }

        private void HandleCapturedKey(int vk, bool ctrl, bool alt, bool shift, bool win)
        {
            if (_recordingSlot is not { } slot)
                return;

            // 裸 Esc：取消录制
            if (vk == VK_ESCAPE && !ctrl && !alt && !shift && !win)
            {
                EndRecording();
                SetStatus("已取消录制");
                return;
            }

            // 裸 Backspace / Delete：清除绑定
            if ((vk == VK_BACK || vk == VK_DELETE) && !ctrl && !alt && !shift && !win)
            {
                if (slot.Kind == SlotKind.Launcher)
                {
                    SetStatus("命令面板热键不能清除，可录制一个新组合或点「恢复默认」", StatusKind.Error);
                    return; // 保持录制态
                }

                EndRecording();
                ApplyBinding(slot, "");
                return;
            }

            string? text = HotKeyParser.Format(vk, ctrl, alt, shift, win);
            if (text == null)
            {
                SetStatus("不支持该按键（小键盘、媒体键等），请换一个；Esc 取消录制", StatusKind.Error);
                return; // 保持录制态，等用户再按一个
            }

            if (!ValidateBinding(slot, vk, ctrl, alt, shift, win, out string? error))
            {
                SetStatus(error!, StatusKind.Error);
                return; // 保持录制态
            }

            if (slot.Kind == SlotKind.Launcher)
            {
                string current = AppConfig.Instance.Config.HotKey ?? "";
                if (string.Equals(HotKeyParser.Normalize(current), HotKeyParser.Normalize(text), StringComparison.Ordinal))
                {
                    EndRecording();
                    SetStatus("热键未变化");
                    return;
                }

                if (!IsLauncherHotKeyAvailable(text, out string? probeError))
                {
                    SetStatus(probeError!, StatusKind.Error);
                    return; // 保持录制态
                }
            }

            EndRecording();
            ApplyBinding(slot, text);
        }

        /// <summary>校验录制的组合能否作为该槽位的热键；失败返回 false 并给出提示文案。</summary>
        private static bool ValidateBinding(Slot slot, int vk, bool ctrl, bool alt, bool shift, bool win, out string? error)
        {
            error = null;
            bool hasModifier = ctrl || alt || shift || win;

            // 系统保留 / 已被本程序占用的组合
            if (vk == VK_TAB && alt)
            {
                error = "含 Alt+Tab 的组合已用于窗口切换器，请换一个";
                return false;
            }
            if (vk == VK_ESCAPE && ctrl && alt)
            {
                error = "Ctrl+Alt+Del 是系统保留组合，无法设置";
                return false;
            }
            if (vk == VK_ESCAPE && ctrl && shift && !alt && !win)
            {
                error = "Ctrl+Shift+Esc 是系统保留组合，无法设置";
                return false;
            }
            if (vk == VK_L && win && !ctrl && !alt)
            {
                error = "Win+L 是系统保留组合，无法设置";
                return false;
            }

            // 命令面板热键走 RegisterHotKey 通道，必须至少带一个修饰键
            if (slot.Kind == SlotKind.Launcher && !hasModifier)
            {
                error = "命令面板热键至少需要一个修饰键（Ctrl/Alt/Shift/Win），请重新按下";
                return false;
            }

            // 其余热键走全局钩子，允许裸键但只限 F1-F12：其余裸键会把普通输入全局吞掉
            if (slot.Kind != SlotKind.Launcher && !hasModifier && (vk < VK_F1 || vk > VK_F12))
            {
                error = "不带修饰键时只支持 F1-F12（避免影响正常输入），请加上 Ctrl/Alt/Shift/Win";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 探测命令面板热键能否注册（RegisterHotKey 通道）。默认热键跳过探测：
        /// 它可能正是启动器注册失败后的回退值、已由本进程注册着，探测会误报占用。
        /// 探测本身异常时不阻止用户（保存后的重注册失败仍会走既有的回退 + 气泡兜底）。
        /// </summary>
        private static bool IsLauncherHotKeyAvailable(string candidate, out string? error)
        {
            error = null;

            if (string.Equals(HotKeyParser.Normalize(candidate), HotKeyParser.Normalize(AppConfig.DefaultHotKey), StringComparison.Ordinal))
                return true;

            try
            {
                var probe = new HotKeyListener();
                try
                {
                    if (probe.RegisterHotKey(candidate, silent: true))
                        return true;
                }
                finally
                {
                    probe.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("热键占用探测失败", ex);
                return true;
            }

            error = "该组合键已被其它程序占用，请换一个";
            return false;
        }

        /// <summary>结束录制态（可为无录制态时调用，幂等）。须在 UI 线程、不在钩子回调栈上执行。</summary>
        private void EndRecording()
        {
            bool wasRecording = _recordingSlot != null;
            _recordingSlot = null;
            _recordingRow = null;
            _recordingTimer.Stop();

            if (wasRecording)
            {
                SwitcherWindow.Current?.EndKeyRecording();
                RefreshRowVisuals();
            }
        }

        // ================= 写入并即时生效 =================

        /// <summary>写入绑定 → 保存配置并重载生效 → 重建行。保存失败时回滚内存改动。</summary>
        private void ApplyBinding(Slot slot, string hotKey)
        {
            string oldValue = GetHotKey(slot);
            if (!TryWriteHotKey(slot, hotKey))
            {
                SetStatus("对应的配置项已不存在，请关闭窗口后重试", StatusKind.Error);
                return;
            }

            if (SaveAndReload())
                SetStatus(string.IsNullOrEmpty(hotKey) ? "已清除该热键" : $"已设置为 {FormatDisplay(hotKey)}，已生效");
            else
            {
                TryWriteHotKey(slot, oldValue); // 回滚，避免界面显示未落盘的改动
                RebuildRows();
                SetStatus("保存配置失败，修改未生效（详见日志）", StatusKind.Error);
            }
        }

        private void SetEnabled(Slot slot, bool enabled)
        {
            if (!TryWriteEnabled(slot, enabled))
                return;

            if (SaveAndReload())
                SetStatus(enabled ? $"已启用「{GetSlotLabel(slot)}」" : $"已禁用「{GetSlotLabel(slot)}」");
            else
            {
                TryWriteEnabled(slot, !enabled);
                RebuildRows();
                SetStatus("保存配置失败，修改未生效（详见日志）", StatusKind.Error);
            }
        }

        private void ClearBinding(Slot slot)
        {
            if (slot.Kind == SlotKind.Launcher)
                return; // 启动器行不显示清除按钮，防御性返回

            EndRecording();
            ApplyBinding(slot, "");
        }

        /// <summary>恢复默认：仅重置命令面板热键与窗口动作（含启用状态），不动命令热键。</summary>
        private void RestoreDefaults()
        {
            EndRecording();

            var result = MessageBox.Show(this,
                "将命令面板热键与全部窗口动作热键（含启用状态）恢复为默认值，命令热键不受影响。是否继续？",
                "恢复默认快捷键", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result != MessageBoxResult.OK)
                return;

            var config = AppConfig.Instance.Config;

            // 快照旧值，保存失败时回滚（避免界面显示默认值而文件里还是旧值）
            string oldHotKey = config.HotKey;
            var oldEntries = config.WindowActions
                .Select(a => (Entry: a, a.HotKey, a.Enabled))
                .ToList();
            var addedEntries = new List<ConfigWindowAction>();

            config.HotKey = AppConfig.DefaultHotKey;

            var defaults = AppConfig.DefaultWindowActions();
            var defaultMap = defaults.ToDictionary(a => a.Action, StringComparer.OrdinalIgnoreCase);

            foreach (var entry in config.WindowActions)
            {
                if (defaultMap.TryGetValue(entry.Action, out var def))
                {
                    entry.HotKey = def.HotKey;
                    entry.Enabled = true;
                }
            }
            // 配置里缺失的动作补上（保持配置文件自解释）
            foreach (var def in defaults)
            {
                if (!config.WindowActions.Any(a => string.Equals(a.Action, def.Action, StringComparison.OrdinalIgnoreCase)))
                {
                    var entry = new ConfigWindowAction { Action = def.Action, HotKey = def.HotKey, Enabled = true };
                    config.WindowActions.Add(entry);
                    addedEntries.Add(entry);
                }
            }

            if (SaveAndReload())
            {
                SetStatus("已恢复默认热键");
                return;
            }

            // 回滚
            config.HotKey = oldHotKey;
            foreach (var (entry, hotKey, enabled) in oldEntries)
            {
                entry.HotKey = hotKey;
                entry.Enabled = enabled;
            }
            foreach (var entry in addedEntries)
                config.WindowActions.Remove(entry);

            RebuildRows();
            SetStatus("保存配置失败，修改未生效（详见日志）", StatusKind.Error);
        }

        /// <summary>保存并重载（自我刷新抑制在内部处理）。返回是否成功。</summary>
        private bool SaveAndReload()
        {
            bool saved;
            _suppressSelfRefresh = true;
            try
            {
                saved = AppConfig.Instance.SaveAndReload();
            }
            finally
            {
                _suppressSelfRefresh = false;
            }

            RebuildRows();
            return saved;
        }

        // ================= 事件与安全网 =================

        /// <summary>配置变化（外部编辑）时刷新显示；自己触发的保存已在 SaveAndReload 中抑制。</summary>
        private void OnConfigUpdated()
        {
            // 事件来自 FileSystemWatcher 线程；本窗口的 UI 操作必须回 UI 线程
            Dispatcher.Invoke(() =>
            {
                if (_suppressSelfRefresh || _closing)
                    return;

                Logger.LogInfo("配置变化，快捷键设置窗口刷新显示");
                RebuildRows();
                SetStatus("检测到配置文件变化，已刷新显示");
            });
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_recordingSlot != null)
            {
                // 兜底：正常情况下录制中的按键由钩子吞掉、窗口收不到；钩子若不在（如初始化失败），
                // 这里至少保证 Esc 能结束录制，而不是把正在录制的窗口直接关掉。
                if (e.Key == Key.Escape)
                {
                    EndRecording();
                    SetStatus("已取消录制");
                }
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);

            // 安全网①：窗口失活（用户切到别的窗口、命令面板被唤出等）时结束录制，避免全局吞键滞留
            if (_recordingSlot != null)
            {
                EndRecording();
                SetStatus("窗口失焦，已取消录制");
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 安全网②：关窗（✕ / Esc / 托盘退出 / Application.Shutdown）前必须结束录制
            _closing = true;
            EndRecording();
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _recordingTimer.Stop();
            AppConfig.Instance.ConfigUpdated -= OnConfigUpdated;
            if (ReferenceEquals(_current, this))
                _current = null;

            base.OnClosed(e);
        }

        // ================= 样式 =================
        // 从 UpdateWindow 照搬（项目风格为各窗口自建样式，不提取公共类）。

        /// <summary>按钮深色样式（纯代码模板：Border + ContentPresenter）。accent=true 为蓝底主按钮。</summary>
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

        /// <summary>链接样式按钮：透明背景、蓝色文字、hover 变浅蓝、禁用灰化。</summary>
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

        /// <summary>标题栏关闭按钮样式：透明背景灰字 ✕，hover 变红底白字，禁用灰化。</summary>
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

            var hoverTemplate = new Trigger { Property = IsMouseOverProperty, Value = true };
            hoverTemplate.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(232, 17, 35)), "border"));
            template.Triggers.Add(hoverTemplate);

            var hoverStyle = new Trigger { Property = IsMouseOverProperty, Value = true };
            hoverStyle.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
            style.Triggers.Add(hoverStyle);

            var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(ForegroundProperty, new SolidColorBrush(Color.FromRgb(90, 90, 90))));
            style.Triggers.Add(disabled);

            return style;
        }

        /// <summary>深色复选框样式：15×15 圆角方框，勾选后蓝底白勾（默认模板是亮色系统样式，深色底上突兀）。</summary>
        private static Style MakeCheckBoxStyle()
        {
            var box = new FrameworkElementFactory(typeof(Border), "box");
            box.SetValue(Border.WidthProperty, 15.0);
            box.SetValue(Border.HeightProperty, 15.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            box.SetValue(Border.BackgroundProperty, new SolidColorBrush(HotKeyBoxBackground));
            box.SetValue(Border.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(120, 120, 120)));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            var check = new FrameworkElementFactory(typeof(TextBlock), "check");
            check.SetValue(TextBlock.TextProperty, "✓");
            check.SetValue(TextBlock.FontSizeProperty, 10.0);
            check.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(VisibilityProperty, Visibility.Collapsed);
            box.AppendChild(check);

            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            panel.AppendChild(box);

            var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = panel };

            var checkedTrigger = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(AccentBlue), "box"));
            checkedTrigger.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(AccentBlue), "box"));
            checkedTrigger.Setters.Add(new Setter(VisibilityProperty, Visibility.Visible, "check"));
            template.Triggers.Add(checkedTrigger);

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(AccentBlueHover), "box"));
            template.Triggers.Add(hover);

            var style = new Style(typeof(CheckBox));
            style.Setters.Add(new Setter(TemplateProperty, template));
            style.Setters.Add(new Setter(CursorProperty, Cursors.Hand));
            return style;
        }

        /// <summary>扁平深色滚动条（照搬 UpdateWindow/SwitcherWindow 的内联 XAML 模板，隐藏箭头只留细 Thumb）。</summary>
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
                _scroll.Resources[typeof(ScrollBar)] = style;
            }
            catch (Exception ex)
            {
                Logger.LogError("应用快捷键设置窗口滚动条样式失败", ex);
            }
        }
    }
}
