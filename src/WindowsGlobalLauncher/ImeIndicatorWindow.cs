using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

// System.Drawing 与 WPF 的 Point/Rectangle/Size 命名冲突，统一用别名（纯坐标语义，物理像素）
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingSize = System.Drawing.Size;

namespace CommandLauncher
{
    /// <summary>
    /// 输入法状态角标：在插入符旁显示「中 / 英」的小徽标。
    /// 不抢焦点（ShowActivated=false + WS_EX_NOACTIVATE）、鼠标穿透（WS_EX_TRANSPARENT）、
    /// 不出现在任务栏（WS_EX_TOOLWINDOW），因此绝不调用 Activate/ForegroundActivator。
    /// 定位一律由 SetWindowPos 以物理像素落位，避免 DIP/DPI 换算误差。
    /// </summary>
    internal sealed class ImeIndicatorWindow : Window
    {
        #region Win32 P/Invoke

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        #endregion

        /// <summary>角标基准尺寸（DIP）。物理尺寸 = 该值 × 目标窗口 DPI 缩放。</summary>
        private const double BadgeSizeDip = 22.0;

        /// <summary>角标完全显示时的不透明度：略透明，减少对正文与候选窗的视觉干扰。</summary>
        private const double TargetOpacity = 0.88;

        /// <summary>淡入时长（毫秒）。</summary>
        private const int FadeInMs = 100;

        /// <summary>淡出时长（毫秒）。</summary>
        private const int FadeOutMs = 150;

        /// <summary>角标与插入符的间距（物理像素，与 ImeIndicatorGeometry.Place 的默认值一致）。</summary>
        private const int GapPx = 4;

        /// <summary>角标与工作区边缘的最小留白（物理像素）。</summary>
        private const int MarginPx = 2;

        private static readonly Brush ChineseBackground = CreateFrozenBrush(Color.FromRgb(0x00, 0x78, 0xD4));
        private static readonly Brush EnglishBackground = CreateFrozenBrush(Color.FromRgb(0x6B, 0x6B, 0x6B));

        private readonly Border _border;
        private readonly TextBlock _text;

        // 最近一次定位的输入（RecheckDpiAndReposition 在 Loaded 后按实际 DPI 重算时需要）
        private DrawingRectangle _caret;
        private double _assumedScale = 1.0;
        private DrawingPoint _lastPosition;

        // 淡入淡出的代次：淡出进行中又被 ShowAt 重新显示时，旧回调据此让位、不隐藏新窗口。
        private int _fadeToken;

        public ImeIndicatorWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 提示窗只看不点：绝不抢前台应用的焦点
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Focusable = false;
            Width = BadgeSizeDip;
            Height = BadgeSizeDip;
            Title = "ImeIndicator";

            _text = new TextBlock
            {
                Text = "中",
                Foreground = Brushes.White,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };

            _border = new Border
            {
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                BorderBrush = CreateFrozenBrush(Color.FromArgb(0x40, 255, 255, 255)),
                Background = ChineseBackground,
                Child = _text,
            };

            Content = _border;

            // 提前创建句柄：扩展样式在 OnSourceInitialized 里追加，Show 之前也能取到 DPI
            new WindowInteropHelper(this).EnsureHandle();
        }

        private static Brush CreateFrozenBrush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);

            // NOACTIVATE：点击/显示都不激活；TRANSPARENT：鼠标穿透，不影响下方的编辑操作；
            // TOOLWINDOW：不出现在 Alt+Tab / 任务栏，且不占用前台历史
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            exStyle |= WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW;
            SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
        }

        /// <summary>
        /// 在插入符旁显示提示。坐标均为虚拟屏物理像素。
        /// 重复调用时只更新文字并重新落位，窗口保持可见（不闪烁）；
        /// 不透明度从当前值平滑过渡到 <see cref="TargetOpacity"/>，不会每次从 0 重新淡入。
        /// </summary>
        public void ShowAt(string text, DrawingRectangle caretPhysical, IntPtr caretHwnd)
        {
            // 作废可能仍在进行的淡出：它以 Completed 回调隐藏窗口，不能让它关掉这次新显示。
            _fadeToken++;

            _text.Text = text;
            _border.Background = text == "中" ? ChineseBackground : EnglishBackground;

            PositionAt(caretPhysical, caretHwnd);

            if (!IsVisible)
            {
                // 先以全透明显示再落位：避免窗口在上一位置/默认位置闪现一帧
                Opacity = 0;
                Show();
            }

            ApplyPosition();

            // 落位后再淡入（首次从 0 开始；重复调用从当前不透明度平滑过渡）。
            AnimateOpacity(TargetOpacity, FadeInMs);

            // WPF 在 Show 后才按实际显示器 DPI 完成布局：Loaded 阶段用真实 DPI 复核一次，
            // 目标窗口与角标所在显示器 DPI 不一致时修正尺寸与位置。
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RecheckDpiAndReposition));
        }

        /// <summary>淡出后隐藏角标（不销毁窗口，下次 ShowAt 复用）。</summary>
        public void HideBadge()
        {
            // 已被关闭（应用退出时 WPF 会先关全部窗口）或本就不可见时不做动画、不调 Hide：
            // 对已关闭窗口调用会抛 InvalidOperationException，而本方法会被退出清理路径调用。
            if (!IsVisible)
            {
                // 作废尚未触发的淡出回调，并复位不透明度，保证下次 ShowAt 从干净状态开始。
                _fadeToken++;
                BeginAnimation(OpacityProperty, null);
                Opacity = TargetOpacity;
                return;
            }

            int token = ++_fadeToken;

            AnimateOpacity(0, FadeOutMs, holdEnd: true, completed: () =>
            {
                // 淡出期间又被 ShowAt 重新显示：让位，绝不隐藏新显示的窗口。
                if (token != _fadeToken)
                    return;

                try
                {
                    // 先摘掉动画再隐藏：不透明度一直被动画持有的话，下次显示的起点与 base 值都会失真。
                    BeginAnimation(OpacityProperty, null);

                    // 窗口可能已在淡出期间被关闭（应用退出）：对已关闭窗口调 Hide 会抛异常。
                    if (IsVisible)
                        Hide();
                }
                catch (Exception ex)
                {
                    // 回调在 Dispatcher 上异步执行，异常外抛会进全局未处理异常；记日志后继续复位。
                    Logger.LogWarning($"输入法提示：淡出隐藏失败: {ex.Message}");
                }
                finally
                {
                    Opacity = TargetOpacity;
                }
            });
        }

        /// <summary>
        /// 立即隐藏并清除动画（截图前需要干净画面的路径专用）。
        /// 正常隐藏（自动隐藏、失焦、切换输入法）走 <see cref="HideBadge"/> 的淡出；
        /// 截图若走淡出，抓屏时仍会拍到半透明的角标。
        /// </summary>
        public void HideImmediately()
        {
            _fadeToken++;
            BeginAnimation(OpacityProperty, null);
            Opacity = TargetOpacity;

            // 已被关闭或本就不可见时直接返回：对已关闭窗口调用 Hide 会抛异常。
            if (IsVisible)
                Hide();
        }

        /// <summary>
        /// 将窗口不透明度平滑过渡到 <paramref name="to"/>。
        /// <para>
        /// 关键顺序：先读 <see cref="UIElement.Opacity"/>（依赖属性 getter 返回当前动画的评估值，
        /// 因此重复调用能从「此刻看得见的值」过渡）→ 再写 base 值 → 最后启动动画。
        /// 先写 base 值保证动画结束或被移除后属性落在目标值，不会被旧值永久劫持。
        /// </para>
        /// </summary>
        /// <param name="holdEnd">true 时动画结束后保持目标值，等待回调移除动画（淡出用）。</param>
        /// <param name="completed">动画自然结束后的回调（UI 线程）。</param>
        private void AnimateOpacity(double to, int durationMs, bool holdEnd = false, Action? completed = null)
        {
            double from = Opacity;
            Opacity = to;

            var animation = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(durationMs))
            {
                // 淡入用 Stop：结束后由 base 值接管，不留动画持有属性；
                // 淡出用 HoldEnd：保持透明到 Completed 回调摘掉动画并隐藏，避免中途闪回。
                FillBehavior = holdEnd ? FillBehavior.HoldEnd : FillBehavior.Stop,
            };

            if (completed != null)
                animation.Completed += (s, e) => completed();

            BeginAnimation(OpacityProperty, animation);
        }

        /// <summary>
        /// 按目标窗口 DPI 计算角标位置并保存定位输入。
        /// 用 GetDpiForWindow(caretHwnd) 而非本窗口 DPI：此刻窗口还没移动到目标显示器上，
        /// 其 DPI 仍是旧值（首次显示甚至可能是主屏 DPI）。
        /// </summary>
        private void PositionAt(DrawingRectangle caret, IntPtr caretHwnd)
        {
            _caret = caret;

            double dpi = GetDpiForWindow(caretHwnd);
            if (dpi <= 0)
                dpi = 96.0 * VisualTreeHelper.GetDpi(this).DpiScaleX; // 取不到时回退本窗口 DPI

            _assumedScale = dpi / 96.0;
            int badgePx = Math.Max(1, (int)Math.Round(BadgeSizeDip * _assumedScale));
            var workArea = System.Windows.Forms.Screen
                .FromPoint(new DrawingPoint(caret.Left, caret.Top))
                .WorkingArea;
            _lastPosition = ImeIndicatorGeometry.Place(
                caret, new DrawingSize(badgePx, badgePx), workArea, GapPx, MarginPx);
        }

        /// <summary>以物理像素落位（不改尺寸、不激活）。</summary>
        private void ApplyPosition()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            SetWindowPos(hwnd, HWND_TOPMOST, _lastPosition.X, _lastPosition.Y, 0, 0,
                SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        /// <summary>用窗口实际 DPI 复核尺寸与位置；DPI 与定位假设一致时什么也不做。</summary>
        private void RecheckDpiAndReposition()
        {
            if (!IsVisible)
                return;

            double actualScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (Math.Abs(actualScale - _assumedScale) <= 0.01)
                return;

            _assumedScale = actualScale;
            int badgePx = Math.Max(1, (int)Math.Round(BadgeSizeDip * actualScale));
            var workArea = System.Windows.Forms.Screen
                .FromPoint(new DrawingPoint(_caret.Left, _caret.Top))
                .WorkingArea;
            _lastPosition = ImeIndicatorGeometry.Place(
                _caret, new DrawingSize(badgePx, badgePx), workArea, GapPx, MarginPx);
            ApplyPosition();
        }
    }
}
