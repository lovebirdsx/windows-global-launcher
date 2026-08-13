using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
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
    /// <summary>
    /// 剪贴板历史弹出窗口（默认热键 Ctrl+Alt+C，见 WindowActions.ShowClipboardHistory）。
    /// 紧凑版深色风格（与命令启动器一致）：顶部搜索框模糊过滤，↑↓/Ctrl+P/Ctrl+N 选择，
    /// 回车把选中条目粘贴回弹出前的前台窗口，Delete 删除条目，Esc/失焦取消。
    /// 弹出位置优先取前台窗口的插入符位置，取不到时回退到鼠标所在屏幕居中。
    /// </summary>
    public class ClipboardWindow : Window
    {
        #region Win32 P/Invoke（前台窗口与插入符定位）

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool BringWindowToTop(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool GetGUIThreadInfo(uint idThread, ref GUITHREADINFO lpgui);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct GUITHREADINFO
        {
            public int cbSize;
            public int dwFlags;
            public IntPtr hwndActive;
            public IntPtr hwndFocus;
            public IntPtr hwndCapture;
            public IntPtr hwndMenuOwner;
            public IntPtr hwndMoveSize;
            public IntPtr hwndCaret;
            public RECT rcCaret;
        }

        private const byte VK_CONTROL = 0x11;
        private const byte VK_MENU = 0x12;   // Alt
        private const byte VK_V = 0x56;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        #endregion

        private const double WindowWidth = 480;
        private const double WindowHeight = 420;
        private const double RowHeight = 34;

        // 图片预览窗的尺寸上限（同时受屏幕工作区比例限制，见 ShowImagePreview）
        private const double MaxPreviewWidth = 720;
        private const double MaxPreviewHeight = 560;
        private const double PreviewPadding = 18; // 边框 1 + 内边距 8，双侧

        // 文本预览：预览文本折行后大致一行能放下的字符数，超过则在旁边弹完整文本预览
        private const int TextPreviewThreshold = 55;
        private const double TextPreviewWidth = 420;
        // 超长文本（上限 5 万字符）截断预览，避免逐键移动选择时的换行布局卡顿
        private const int TextPreviewMaxChars = 5000;

        private readonly ObservableCollection<ClipboardEntry> _items = [];
        private TextBox _searchBox = null!;
        private TextBlock _placeholder = null!;
        private TextBlock _emptyHint = null!;
        private readonly ListBox _list = CreateList();

        // 预览窗：选中条目时跟随弹出（图片显原图，长文本显完整内容），ShowActivated=false 不抢焦点
        private readonly Window _previewWindow;
        private readonly Image _previewImage;
        private readonly TextBlock _previewText;
        private readonly ScrollViewer _previewTextScroll;

        // 弹出前的前台窗口，回车后把焦点还给它再模拟 Ctrl+V
        private IntPtr _previousForeground;

        // 显示后的激活宽限期（毫秒）：此间失焦多为激活序列的瞬时抖动（VS Code 短暂夺回前台），
        // 重试激活而非隐藏，避免第一次唤出「一闪即隐」；宽限期过后恢复「失焦即隐藏」。
        private const int ActivationGraceMs = 600;
        private const int ActivationRetryIntervalMs = 50;
        private const int MaxActivationRetries = 8;

        private long _graceUntil; // Environment.TickCount64 标记的宽限期截止时刻
        private readonly DispatcherTimer _activationTimer; // 激活失败时短间隔重试
        private int _activationRetries;

        public ClipboardWindow()
        {
            InitializeComponent();

            // 预览窗：与主窗口同风格的无边框圆角窗，按条目类型切换显示图片或文本
            _previewImage = new Image { Stretch = Stretch.Uniform };
            _previewText = new TextBlock
            {
                FontSize = 13,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            };
            // 滚动条刻意隐藏：点击滚动条会激活预览窗、导致主窗口失焦关闭，长文本用鼠标滚轮滚动即可
            _previewTextScroll = new ScrollViewer
            {
                Content = _previewText,
                VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Visibility = Visibility.Collapsed,
            };
            _previewWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                ShowActivated = false, // 预览只看不点，不能抢搜索框焦点
                Content = new Border
                {
                    Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(8),
                    Child = new Grid { Children = { _previewImage, _previewTextScroll } },
                },
            };
            _list.SelectionChanged += (s, e) => UpdatePreview();

            // 激活失败时的重试定时器（短间隔，配合前台锁定解除）
            _activationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ActivationRetryIntervalMs) };
            _activationTimer.Tick += (s, e) => TryActivateOnce();

            // 提前创建句柄，保证未显示时也能取到 DPI 做坐标换算
            new WindowInteropHelper(this).EnsureHandle();

            // 窗口关闭期间历史可能变化，重新打开时统一刷新即可；可见时才即时刷新
            ClipboardHistoryManager.Instance.HistoryChanged += () =>
            {
                if (IsVisible)
                    Dispatcher.Invoke(RefreshList);
            };
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
            Title = "Clipboard History";
            Width = WindowWidth;
            Height = WindowHeight;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent; // 背景与描边/圆角统一由外层 Border 承载
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 不抢焦点，激活统一由 TryActivateOnce 走 AttachThreadInput + 前台解锁，避免 Show 的半激活抖动

            // 搜索框 + 占位提示（与命令启动器同风格，尺寸更紧凑）
            _searchBox = new TextBox
            {
                Height = 32,
                FontSize = 14,
                Padding = new Thickness(12, 5, 12, 5),
                Background = new SolidColorBrush(Color.FromArgb(255, 50, 50, 50)),
                Foreground = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Top,
            };
            _searchBox.TextChanged += (s, e) =>
            {
                _placeholder.Visibility = string.IsNullOrEmpty(_searchBox.Text) ? Visibility.Visible : Visibility.Hidden;
                RefreshList();
            };
            _searchBox.PreviewKeyDown += SearchBox_PreviewKeyDown;

            _placeholder = new TextBlock
            {
                Text = "输入字符搜索（↑↓ 选择，回车粘贴，Delete 删除）",
                FontSize = 14,
                Padding = new Thickness(12, 5, 12, 5),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 150, 150, 150)),
                VerticalAlignment = VerticalAlignment.Top,
                IsHitTestVisible = false,
            };

            // 列表项样式
            var itemStyle = new Style(typeof(ListBoxItem));
            itemStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(10, 4, 10, 4)));
            itemStyle.Setters.Add(new Setter(MarginProperty, new Thickness(2)));
            itemStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
            itemStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
            itemStyle.Setters.Add(new Setter(HeightProperty, RowHeight));

            var hoverTrigger = new Trigger { Property = IsMouseOverProperty, Value = true };
            hoverTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 55, 55, 55))));
            var selectedTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
            selectedTrigger.Setters.Add(new Setter(BackgroundProperty, new SolidColorBrush(Color.FromArgb(255, 0, 120, 212))));
            itemStyle.Triggers.Add(hoverTrigger);
            itemStyle.Triggers.Add(selectedTrigger);
            _list.ItemContainerStyle = itemStyle;

            // 列表项模板：图片缩略图（仅图片条目）+ 单行预览 + 右侧相对时间
            var template = new DataTemplate();
            var dock = new FrameworkElementFactory(typeof(DockPanel));

            var thumbnail = new FrameworkElementFactory(typeof(Image));
            thumbnail.SetValue(DockPanel.DockProperty, Dock.Left);
            thumbnail.SetValue(WidthProperty, 26.0);
            thumbnail.SetValue(HeightProperty, 26.0);
            thumbnail.SetValue(MarginProperty, new Thickness(0, 0, 10, 0));
            thumbnail.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            thumbnail.SetValue(Image.StretchProperty, Stretch.Uniform);
            thumbnail.SetBinding(Image.SourceProperty, new Binding("Thumbnail"));
            thumbnail.SetBinding(VisibilityProperty, new Binding("IsImage") { Converter = new BooleanToVisibilityConverter() });

            var time = new FrameworkElementFactory(typeof(TextBlock));
            time.SetValue(DockPanel.DockProperty, Dock.Right);
            time.SetValue(MarginProperty, new Thickness(10, 0, 0, 0));
            time.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            time.SetValue(TextBlock.FontSizeProperty, 10.0);
            time.SetValue(TextBlock.ForegroundProperty, new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)));
            time.SetBinding(TextBlock.TextProperty, new Binding("Timestamp") { Converter = new TimeAgoConverter() });

            var preview = new FrameworkElementFactory(typeof(TextBlock));
            preview.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            preview.SetValue(TextBlock.FontSizeProperty, 13.0);
            preview.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            preview.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            preview.SetBinding(TextBlock.TextProperty, new Binding("Preview"));

            dock.AppendChild(thumbnail);
            dock.AppendChild(time);
            dock.AppendChild(preview);
            template.VisualTree = dock;
            _list.ItemTemplate = template;

            // 隐藏滚动条：垂直保留滚动能力（键盘导航 ScrollIntoView / 鼠标滚轮需要）但不显示；
        // 水平禁用，内容约束在列表宽度内，超长文本由省略号截断而不是撑出滚动条
        ScrollViewer.SetVerticalScrollBarVisibility(_list, ScrollBarVisibility.Hidden);
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            _list.ItemsSource = _items;
            _list.MouseDoubleClick += (s, e) => PasteSelected();

            // 空历史提示
            _emptyHint = new TextBlock
            {
                Text = "暂无剪贴板历史",
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.FromArgb(255, 120, 120, 120)),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Hidden,
            };

            // 外层 Border：深色半透明背景 + 轻度描边 + 圆角（与切换器一致）
            var root = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
            };
            var grid = new Grid { Margin = new Thickness(10) };
            grid.Children.Add(_searchBox);
            grid.Children.Add(_placeholder);

            var listArea = new Grid { Margin = new Thickness(0, 42, 0, 0) };
            listArea.Children.Add(_list);
            listArea.Children.Add(_emptyHint);
            grid.Children.Add(listArea);

            root.Child = grid;
            Content = root;
        }

        /// <summary>唤出窗口（热键动作入口）。已可见时再按一次则关闭（切换式）。</summary>
        public void ShowHistory()
        {
            if (IsVisible)
            {
                HideWindow();
                return;
            }

            // 必须在 Show 之前记录，Show 之后前台就是我们自己了
            _previousForeground = GetForegroundWindow();

            _searchBox.Text = ""; // 触发 TextChanged → RefreshList
            RefreshList();
            PositionWindow();

            Show(); // ShowActivated=false，仅显示不激活；激活统一交给 TryActivateOnce（前台解锁 + 重试）
            _graceUntil = Environment.TickCount64 + ActivationGraceMs;
            _activationRetries = 0;
            TryActivateOnce();
            UpdatePreview(); // RefreshList 发生在 Show 之前，SelectionChanged 时被 IsVisible 挡住，这里补一次
        }

        // 热键经低级键盘钩子到达，输入并未真正进入本进程的消息队列，
        // 直接 Activate/SetForegroundWindow 会被前台锁定间歇性拒绝（表现为窗口弹出但无焦点）。
        // 复用 WindowEnumerator.Activate 的 AttachThreadInput 技巧绕过前台锁定；失败时用 Alt 击发解锁并短间隔重试。
        private void TryActivateOnce()
        {
            if (!IsVisible)
                return; // 已隐藏则不再激活

            bool ok = false;
            try
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                uint thisThread = GetCurrentThreadId();
                // 用弹出前记录的前台窗口取线程附加（而非 Show 之后的 GetForegroundWindow，避免被自身干扰导致附加错线程）
                uint foreThread = _previousForeground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(_previousForeground, out _);

                bool attached = false;
                if (foreThread != 0 && foreThread != thisThread)
                    attached = AttachThreadInput(thisThread, foreThread, true);

                BringWindowToTop(hwnd);
                ok = SetForegroundWindow(hwnd);

                // 前台锁定仍拒绝时：模拟一次 Alt 击发解锁（经典 SetForegroundWindow 解锁手段），再重试一次
                if (!ok)
                {
                    keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
                    keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                    ok = SetForegroundWindow(hwnd);
                }

                if (attached)
                    AttachThreadInput(thisThread, foreThread, false);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"激活剪贴板窗口失败: {ex.Message}");
            }

            Activate(); // WPF 层同步激活态
            Keyboard.Focus(_searchBox);

            _activationRetries++;
            if (ok)
            {
                _activationTimer.Stop(); // 激活成功即停止重试，避免定时器持续触发
            }
            else if (_activationRetries < MaxActivationRetries)
            {
                // 短间隔后重试，等待前台锁定解除
                _activationTimer.Stop();
                _activationTimer.Start();
            }
            else
            {
                _activationTimer.Stop();
                Logger.LogWarning("剪贴板窗口多次激活失败，窗口可能未获得焦点");
            }
        }

        private void HideWindow()
        {
            _activationTimer.Stop();
            Hide();
            _previewWindow.Hide();
            _searchBox.Text = "";
        }

        // 选中条目时在主窗口右侧（放不下则左侧）弹出预览：
        // 图片条目尽量按原始尺寸显示原图；文本条目过长（单行预览被截断）时显示完整文本。
        private void UpdatePreview()
        {
            if (!IsVisible || _list.SelectedItem is not ClipboardEntry entry)
            {
                _previewWindow.Hide();
                return;
            }

            if (entry.IsImage)
                ShowImagePreview(entry);
            else if (entry.Preview.Length > TextPreviewThreshold)
                ShowTextPreview(entry);
            else
                _previewWindow.Hide();
        }

        private void ShowImagePreview(ClipboardEntry entry)
        {
            var bmp = ClipboardHistoryManager.Instance.LoadFullImage(entry);
            if (bmp == null)
            {
                _previewWindow.Hide();
                return;
            }
            _previewImage.Source = bmp;

            var wa = GetWorkAreaDip();
            var dpi = VisualTreeHelper.GetDpi(this);

            // 原始尺寸（物理像素 → DIP），超过上限则等比缩小；上限同时受屏幕工作区约束
            double w = bmp.PixelWidth / dpi.DpiScaleX;
            double h = bmp.PixelHeight / dpi.DpiScaleY;
            double maxW = Math.Min(MaxPreviewWidth, wa.Width * 0.5);
            double maxH = Math.Min(MaxPreviewHeight, wa.Height * 0.6);
            double scale = Math.Min(1.0, Math.Min(maxW / w, maxH / h));

            _previewImage.Visibility = Visibility.Visible;
            _previewTextScroll.Visibility = Visibility.Collapsed;

            PlacePreview(Math.Max(w * scale, 1) + PreviewPadding, Math.Max(h * scale, 1) + PreviewPadding, wa);
        }

        private void ShowTextPreview(ClipboardEntry entry)
        {
            var wa = GetWorkAreaDip();
            double textW = Math.Min(TextPreviewWidth, wa.Width * 0.5);
            double maxH = Math.Min(MaxPreviewHeight, wa.Height * 0.6);

            _previewText.Text = entry.Text.Length > TextPreviewMaxChars
                ? entry.Text[..TextPreviewMaxChars] + "\n……"
                : entry.Text;
            _previewText.Measure(new Size(textW, double.PositiveInfinity));
            double textH = Math.Min(Math.Max(_previewText.DesiredSize.Height, 20), maxH);

            _previewImage.Visibility = Visibility.Collapsed;
            _previewTextScroll.Visibility = Visibility.Visible;

            PlacePreview(textW + PreviewPadding, textH + PreviewPadding, wa);
        }

        /// <summary>主窗口中心所在屏幕的工作区（DIP），用于预览窗的尺寸约束与定位。</summary>
        private Rect GetWorkAreaDip()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            int cx = (int)((Left + Width / 2) * dpi.DpiScaleX);
            int cy = (int)((Top + Height / 2) * dpi.DpiScaleY);
            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(cx, cy)).WorkingArea;
            return new Rect(
                wa.Left / dpi.DpiScaleX, wa.Top / dpi.DpiScaleY,
                (wa.Right - wa.Left) / dpi.DpiScaleX, (wa.Bottom - wa.Top) / dpi.DpiScaleY);
        }

        // 预览窗统一定位：主窗口右侧，放不下翻左侧，钳制在屏幕工作区内
        private void PlacePreview(double width, double height, Rect wa)
        {
            _previewWindow.Width = width;
            _previewWindow.Height = height;

            double previewLeft = Left + Width + 8;
            if (previewLeft + width > wa.Right)
                previewLeft = Left - width - 8; // 右侧放不下就翻到左侧
            _previewWindow.Left = Math.Max(wa.Left, previewLeft);
            _previewWindow.Top = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - height));

            if (!_previewWindow.IsVisible)
                _previewWindow.Show();
        }

        private void RefreshList()
        {
            int previousIndex = Math.Max(_list.SelectedIndex, 0);

            _items.Clear();
            foreach (var entry in ClipboardHistoryManager.Instance.Query(_searchBox.Text))
            {
                ClipboardHistoryManager.Instance.EnsureThumbnail(entry);
                _items.Add(entry);
            }

            _emptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Hidden;
            if (_items.Count > 0)
            {
                _list.SelectedIndex = Math.Min(previousIndex, _items.Count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
            }
        }

        #region 键盘交互（与命令启动器一致：↑↓/Ctrl+P/Ctrl+N 移动，回车执行，Esc 取消）

        private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

            if (e.Key == Key.Down || (ctrl && e.Key == Key.N))
            {
                MoveSelection(1);
                e.Handled = true;
            }
            else if (e.Key == Key.Up || (ctrl && e.Key == Key.P))
            {
                MoveSelection(-1);
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                PasteSelected();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideWindow();
                e.Handled = true;
            }
            else if (e.Key == Key.Delete)
            {
                DeleteSelected();
                e.Handled = true;
            }
        }

        private void MoveSelection(int direction)
        {
            int n = _items.Count;
            if (n == 0)
                return;

            int idx = Math.Clamp(_list.SelectedIndex + direction, 0, n - 1);
            _list.SelectedIndex = idx;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        #endregion

        private void PasteSelected()
        {
            if (_list.SelectedItem is not ClipboardEntry entry)
                return;

            var target = _previousForeground;
            HideWindow();
            PasteEntryAsync(entry, target);
        }

        // 先把内容写回剪贴板，再把焦点还给弹出前的前台窗口，最后模拟 Ctrl+V。
        // 写剪贴板会触发一次剪贴板监听，相同内容去重置顶，无副作用。
        private async void PasteEntryAsync(ClipboardEntry entry, IntPtr target)
        {
            try
            {
                ClipboardHistoryManager.Instance.SetToClipboard(entry);

                if (target != IntPtr.Zero)
                    WindowEnumerator.Activate(target);

                // 等目标窗口真正拿到焦点再发按键
                await Task.Delay(120);

                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                Logger.LogError("粘贴剪贴板历史失败", ex);
            }
        }

        private void DeleteSelected()
        {
            if (_list.SelectedItem is not ClipboardEntry entry)
                return;
            ClipboardHistoryManager.Instance.Delete(entry); // 触发 HistoryChanged → RefreshList
        }

        #region 弹出位置（优先插入符，失败回退鼠标屏幕居中）

        private void PositionWindow()
        {
            try
            {
                var dpi = VisualTreeHelper.GetDpi(this);

                if (TryGetCaretScreenPoint(_previousForeground, out var caretPt) ||
                    TryGetCaretViaUIA(out caretPt))
                {
                    Left = caretPt.X / dpi.DpiScaleX + 4;
                    Top = caretPt.Y / dpi.DpiScaleY + 8;
                    ClampToScreen(caretPt, dpi);
                }
                else
                {
                    // 回退：鼠标所在屏幕居中（与命令启动器一致）
                    var mouse = System.Windows.Forms.Cursor.Position;
                    var screen = System.Windows.Forms.Screen.FromPoint(mouse);
                    double screenLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
                    double screenTop = screen.WorkingArea.Top / dpi.DpiScaleY;
                    double screenWidth = screen.WorkingArea.Width / dpi.DpiScaleX;
                    double screenHeight = screen.WorkingArea.Height / dpi.DpiScaleY;
                    Left = screenLeft + (screenWidth - Width) / 2;
                    Top = screenTop + (screenHeight - Height) / 2;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("剪贴板窗口定位失败，使用默认位置", ex);
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        /// <summary>取前台窗口插入符的屏幕坐标（物理像素）。自绘光标的应用（浏览器等）取不到，返回 false。</summary>
        private static bool TryGetCaretScreenPoint(IntPtr foreground, out System.Drawing.Point point)
        {
            point = default;
            if (foreground == IntPtr.Zero)
                return false;

            uint threadId = GetWindowThreadProcessId(foreground, out _);
            var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (!GetGUIThreadInfo(threadId, ref info) || info.hwndCaret == IntPtr.Zero)
                return false;

            // 宽高均为 0 说明插入符不可见
            if (info.rcCaret.Right <= info.rcCaret.Left && info.rcCaret.Bottom <= info.rcCaret.Top)
                return false;

            var pt = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Bottom };
            if (!ClientToScreen(info.hwndCaret, ref pt))
                return false;

            point = new System.Drawing.Point(pt.X, pt.Y);
            return true;
        }

        /// <summary>
        /// 经 UI Automation 取焦点元素的插入符位置（屏幕坐标，物理像素）。
        /// VS Code 等 Electron/Chromium 应用的光标是自绘的，GetGUIThreadInfo 取不到，
        /// 但它们实现了 TextPattern，可从选区矩形定位光标。
        /// </summary>
        private static bool TryGetCaretViaUIA(out System.Drawing.Point point)
        {
            point = default;
            try
            {
                var focused = System.Windows.Automation.AutomationElement.FocusedElement;
                if (focused == null)
                    return false;
                if (!focused.TryGetCurrentPattern(System.Windows.Automation.TextPattern.Pattern, out var patternObj))
                    return false;

                var ranges = ((System.Windows.Automation.TextPattern)patternObj).GetSelection();
                if (ranges.Length == 0)
                    return false;

                // 多行选区时取最后一个矩形（光标在选区末尾）；插入符矩形的宽度为 0 属正常
                var rects = ranges[^1].GetBoundingRectangles();
                if (rects.Length == 0)
                    return false;
                var rect = rects[^1];
                if (rect.IsEmpty || rect.Height <= 0)
                    return false;

                point = new System.Drawing.Point((int)rect.Left, (int)rect.Bottom);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // 防止窗口超出 caret 所在屏幕的工作区
        private void ClampToScreen(System.Drawing.Point anchor, DpiScale dpi)
        {
            var screen = System.Windows.Forms.Screen.FromPoint(anchor);
            double waLeft = screen.WorkingArea.Left / dpi.DpiScaleX;
            double waTop = screen.WorkingArea.Top / dpi.DpiScaleY;
            double waRight = screen.WorkingArea.Right / dpi.DpiScaleX;
            double waBottom = screen.WorkingArea.Bottom / dpi.DpiScaleY;

            Left = Math.Max(waLeft, Math.Min(Left, waRight - Width));
            Top = Math.Max(waTop, Math.Min(Top, waBottom - Height));
        }

        #endregion

        // 失焦即取消（与命令启动器一致）。但显示后的宽限期内，失焦多为激活序列的瞬时抖动
        // （前台被 VS Code 短暂夺回），此时重试激活而非隐藏，避免第一次唤出一闪即隐。
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

        /// <summary>相对时间显示：刚刚 / n 分钟前 / n 小时前 / 昨天 HH:mm / MM-dd HH:mm。</summary>
        private class TimeAgoConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            {
                if (value is not DateTime t)
                    return "";

                var now = DateTime.Now;
                var span = now - t;
                if (span.TotalMinutes < 1)
                    return "刚刚";
                if (span.TotalHours < 1)
                    return $"{(int)span.TotalMinutes} 分钟前";
                if (t.Date == now.Date)
                    return $"{(int)span.TotalHours} 小时前";
                if (t.Date == now.Date.AddDays(-1))
                    return "昨天 " + t.ToString("HH:mm");
                if (t.Year == now.Year)
                    return t.ToString("MM-dd HH:mm");
                return t.ToString("yyyy-MM-dd");
            }

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
                => throw new NotSupportedException();
        }
    }
}
