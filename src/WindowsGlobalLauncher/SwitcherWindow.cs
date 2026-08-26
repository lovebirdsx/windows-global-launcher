using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;

namespace CommandLauncher
{
    /// <summary>
    /// Alt+Tab 窗口切换器。竖向列表（应用图标 + 窗口标题），复用命令启动器的深色风格。
    /// 由全局键盘钩子驱动：Alt+Tab 显示/向后移动，Shift+Tab 向前，松开 Alt 激活，Esc 取消。
    /// </summary>
    public class SwitcherWindow : Window, IDisposable
    {
        #region Win32 P/Invoke（Shell Hook）

        [DllImport("user32.dll")]
        private static extern bool RegisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool DeregisterShellHookWindow(IntPtr hWnd);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        private const int HSHELL_FLASH            = 32774; // 6 | 0x8000 — 窗口请求注意（taskbar 闪烁）
        private const int HSHELL_WINDOWACTIVATED  = 4;
        private const int HSHELL_RUDEAPPACTIVATED = 32772; // 4 | 0x8000

        #endregion

        private const double RowHeight = 56;
        private const double WindowWidth = 560;
        private const double WindowHeight = 800; // 固定高度，不随窗口数量变化

        private readonly ObservableCollection<WindowInfo> _items = [];
        private readonly ListBox _list = CreateList();
        private readonly KeyboardHook _hook = new();

        private uint _shellHookMsg;
        private readonly HashSet<IntPtr> _flashingWindows = new();

        // 切换器是否处于激活态（Alt 仍按住、切换器逻辑上正在工作）。
        // 仅在 UI 线程（钩子回调线程）读写，无需加锁。
        private bool _isActive;
        private bool _disposed;

        public SwitcherWindow()
        {
            InitializeComponent();

            // 确保窗口句柄存在，以便枚举时排除自身，同时注册 shell hook
            new WindowInteropHelper(this).EnsureHandle();

            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            _shellHookMsg = RegisterWindowMessage("SHELLHOOK");
            RegisterShellHookWindow(hwnd);
            HwndSource.FromHwnd(hwnd).AddHook(WndProc);

            _hook.AltTab += OnAltTab;
            _hook.Commit += OnCommit;
            _hook.Cancel += OnCancel;
            _hook.Navigate += OnNavigate;
            _hook.Close += OnClose;
            _hook.MoveMonitor += OnMoveMonitor;
            _hook.IsSwitcherActive = () => _isActive;
            // 框选选中态下的全局 Esc 取消选中（空白处按 Esc）：查询须在钩子回调线程（UI 线程）轻量，
            // 实际清空选中等 UI 操作经 Dispatcher.BeginInvoke 异步执行，与动作派发同风格。
            // 条件额外排除「前台是本进程窗口」——否则会吞掉命令面板/剪贴板历史/截图遮罩/框选遮罩
            // 等本进程窗口自己的 Esc；贴图自身 OnKeyDown 已会「有选中 → 取消选中」，语义不变。
            _hook.ShouldCancelSelectionOnEscape = () => PinWindow.IsAnySelected && !PinWindow.IsAnyEditing && !PinWindow.IsForegroundOwnedByThisProcess();
            _hook.CancelSelection = () => Dispatcher.BeginInvoke(PinWindow.CancelSelectionFromGlobal);
            _hook.Install();

            // 装配可配置的窗口动作热键（如 Alt+Q 关闭前台窗口），并跟随配置热更新
            ReloadActionBindings();
            AppConfig.Instance.ConfigUpdated += () => Dispatcher.Invoke(ReloadActionBindings);

            Logger.LogInfo("Alt+Tab 切换器初始化完成");
        }

        /// <summary>
        /// 从配置重建「热键 → 窗口动作」绑定表（启动时与配置热更新时调用，须在 UI 线程）。
        /// 动作统一包一层 Dispatcher.BeginInvoke 异步执行，避免在钩子回调里阻塞（LowLevelHooksTimeout）。
        /// </summary>
        private void ReloadActionBindings()
        {
            var bindings = new List<HotKeyActionBinding>();
            foreach (var item in AppConfig.Instance.Config.WindowActions)
            {
                if (!item.Enabled)
                    continue;

                if (!HotKeyParser.TryParse(item.HotKey, out int vk, out bool ctrl, out bool alt, out bool shift, out bool win))
                {
                    Logger.LogWarning($"窗口动作热键解析失败，已跳过: {item.HotKey}");
                    continue;
                }

                if (!WindowActions.All.TryGetValue(item.Action, out var action))
                {
                    Logger.LogWarning($"未知窗口动作，已跳过: {item.Action}");
                    continue;
                }

                bindings.Add(new HotKeyActionBinding
                {
                    VirtualKey = vk,
                    Ctrl = ctrl,
                    Alt = alt,
                    Shift = shift,
                    Win = win,
                    Callback = () => Dispatcher.BeginInvoke(action)
                });
            }
            _hook.SetActionBindings(bindings);
        }

        private static ListBox CreateList()
        {
            return new ListBox
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Foreground = Brushes.White,
                SelectionMode = SelectionMode.Single,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
        }

        private void InitializeComponent()
        {
            Title = "Window Switcher";
            Width = WindowWidth;
            Height = WindowHeight;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent; // 背景与描边/圆角统一由外层 Border 承载
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 不抢焦点，保持目标窗口前台历史，使 SetForegroundWindow 可靠
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            // 列表项样式
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(12, 6, 12, 6)));
            itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(2)));
            itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(HeightProperty, RowHeight - 4));

            var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 55, 55, 55))));
            var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))));
            // 通知状态：背景变为 amber 暗橙，优先级低于 hover/selected（排在前面即可）
            var notifyTrigger = new DataTrigger { Binding = new Binding("HasNotification"), Value = true };
            notifyTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromRgb(120, 65, 0))));
            itemStyle.Triggers.Add(notifyTrigger);
            itemStyle.Triggers.Add(hoverTrigger);
            itemStyle.Triggers.Add(selectedTrigger);
            _list.ItemContainerStyle = itemStyle;

            // 列表项模板：图标 + 标题
            var template = new DataTemplate();
            var panel = new FrameworkElementFactory(typeof(StackPanel));
            panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            panel.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            var icon = new FrameworkElementFactory(typeof(Image));
            icon.SetValue(WidthProperty, 32.0);
            icon.SetValue(HeightProperty, 32.0);
            icon.SetValue(MarginProperty, new Thickness(0, 0, 12, 0));
            icon.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            icon.SetBinding(Image.SourceProperty, new Binding("Icon"));

            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new Binding("Title"));
            title.SetValue(TextBlock.FontSizeProperty, 14.0);
            title.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            title.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            title.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);

            panel.AppendChild(icon);
            panel.AppendChild(title);
            template.VisualTree = panel;
            _list.ItemTemplate = template;
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Auto); // 内容溢出时才出现，不常驻
            ApplyFlatScrollBar();

            _list.ItemsSource = _items;
            _list.MouseDoubleClick += (s, e) => Commit();

            // 外层 Border：深色半透明背景 + 轻度描边 + 圆角，避免在黑色应用上弹窗边界不明显
            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };
            var grid = new Grid { Margin = new Thickness(12) };
            grid.Children.Add(_list);
            root.Child = grid;
            Content = root;
        }

        #region 钩子事件（运行在钩子回调即 UI 线程；重活投递到 Dispatcher 异步执行以快速返回）

        private void OnAltTab(bool reverse)
        {
            if (!_isActive)
            {
                _isActive = true;
                Dispatcher.BeginInvoke(() => ShowSwitcher(reverse));
            }
            else
            {
                Dispatcher.BeginInvoke(() => MoveSelection(reverse ? -1 : 1));
            }
        }

        private void OnCommit() => Dispatcher.BeginInvoke(Commit);

        private void OnCancel() => Dispatcher.BeginInvoke(Cancel);

        private void OnNavigate(int direction)
            => Dispatcher.BeginInvoke(() => { if (_isActive) MoveSelection(direction); });

        private void OnClose()
            => Dispatcher.BeginInvoke(() => { if (_isActive) CloseSelected(); });

        private void OnMoveMonitor(int direction)
            => Dispatcher.BeginInvoke(() => { if (_isActive) MoveSelected(direction); });

        #endregion

        // Shell hook WndProc：跟踪窗口闪烁状态（HSHELL_FLASH），用于在列表项显示通知圆点
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_shellHookMsg != 0 && (uint)msg == _shellHookMsg)
            {
                int code = (int)wParam;
                if (code == HSHELL_FLASH)
                {
                    _flashingWindows.Add(lParam);
                    UpdateNotificationDot(lParam, true);
                }
                else if (code == HSHELL_WINDOWACTIVATED || code == HSHELL_RUDEAPPACTIVATED)
                {
                    _flashingWindows.Remove(lParam);
                    UpdateNotificationDot(lParam, false);
                }
            }
            return IntPtr.Zero;
        }

        private void UpdateNotificationDot(IntPtr targetHwnd, bool value)
        {
            var item = _items.FirstOrDefault(w => w.Hwnd == targetHwnd);
            if (item != null)
                item.HasNotification = value;
        }

        private void ShowSwitcher(bool reverse)
        {
            if (!_isActive)
                return; // 可能已被 Commit/Cancel 复位

            IntPtr self = new WindowInteropHelper(this).Handle;
            List<WindowInfo> windows = WindowEnumerator.EnumerateWindows(self, _flashingWindows);

            if (windows.Count == 0)
            {
                _isActive = false;
                return;
            }

            _items.Clear();
            foreach (var w in windows)
                _items.Add(w);

            // 默认选中：正向时选“上一个窗口”（index 1），反向时选末项
            int defaultIndex = reverse
                ? _items.Count - 1
                : (_items.Count > 1 ? 1 : 0);
            _list.SelectedIndex = defaultIndex;
            _list.ScrollIntoView(_list.SelectedItem);

            CenterOnScreen();
            Show();
            Topmost = true;
        }

        private void MoveSelection(int direction)
        {
            int n = _items.Count;
            if (n == 0)
                return;

            int idx = (_list.SelectedIndex + direction + n) % n;
            _list.SelectedIndex = idx;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        // 关闭当前选中窗口，并保持切换器激活。
        private void CloseSelected()
        {
            if (!_isActive || !IsVisible || _list.SelectedItem is not WindowInfo target)
                return;

            int idx = _list.SelectedIndex;
            WindowEnumerator.CloseWindow(target.Hwnd);

            // WM_CLOSE 是异步请求，目标窗口此刻通常尚未销毁；直接从列表移除作为即时反馈，
            // 避免立即重新枚举又把它加回来。
            _items.RemoveAt(idx);

            if (_items.Count == 0)
            {
                Cancel(); // 列表空了，隐藏并复位
                return;
            }

            _list.SelectedIndex = Math.Min(idx, _items.Count - 1);
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void Commit()
        {
            if (!_isActive)
                return;
            _isActive = false;

            if (IsVisible && _list.SelectedItem is WindowInfo target)
            {
                Hide();
                _flashingWindows.Remove(target.Hwnd);
                WindowEnumerator.Activate(target.Hwnd);
            }
            else
            {
                Hide();
            }
        }

        private void Cancel()
        {
            _isActive = false;
            Hide();
        }

        private void MoveSelected(int direction)
        {
            if (!_isActive || !IsVisible || _list.SelectedItem is not WindowInfo target)
                return;
            WindowEnumerator.MoveToAdjacentMonitor(target.Hwnd, direction);
        }

        // 固定窗口大小，仅做居中（高度超出屏幕工作区时按工作区收敛）。
        private void CenterOnScreen()
        {
            try
            {
                var screen = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position);
                var dpi = VisualTreeHelper.GetDpi(this);

                double screenLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
                double screenTop = screen.WorkingArea.Top / dpi.DpiScaleY;
                double screenWidth = screen.WorkingArea.Width / dpi.DpiScaleX;
                double screenHeight = screen.WorkingArea.Height / dpi.DpiScaleY;

                Width = WindowWidth;
                Height = Math.Min(WindowHeight, screenHeight);

                Left = screenLeft + (screenWidth - Width) / 2;
                Top = screenTop + (screenHeight - Height) / 2;
            }
            catch (Exception ex)
            {
                Logger.LogError("切换器居中失败，使用默认位置", ex);
            }
        }

        // 切换器靠全局钩子驱动，失焦不应自动隐藏（避免与 ShowActivated=false 冲突）。
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);
        }

        // 扁平化深色滚动条，与整体黑色主题一致（隐藏箭头按钮，仅保留细圆角 Thumb）。
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
                _list.Resources[typeof(System.Windows.Controls.Primitives.ScrollBar)] = style;
            }
            catch (Exception ex)
            {
                Logger.LogError("应用滚动条样式失败", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
                DeregisterShellHookWindow(hwnd);
            _hook.Dispose();
            _disposed = true;
            GC.SuppressFinalize(this);
        }

        protected override void OnClosed(EventArgs e)
        {
            Dispose();
            base.OnClosed(e);
        }
    }
}
