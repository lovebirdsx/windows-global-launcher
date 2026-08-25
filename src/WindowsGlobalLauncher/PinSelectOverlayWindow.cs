using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CommandLauncher
{
    /// <summary>
    /// 贴图框选遮罩：单一无边框窗口覆盖整个虚拟屏幕（SetWindowPos 物理像素铺满，同
    /// ScreenshotOverlayWindow 的窗口基建），拖蓝色虚线橡皮筋框选贴图，松手把与框相交的
    /// 贴图交给 PinWindow.ApplyBoxSelection 落定。
    /// 无冻结帧、无状态机：一层半透明橡皮筋 + 键盘/鼠标直转，会话结束即 Close（无论选中与否）。
    /// 坐标约定与截图遮罩一致：鼠标与选择框真相源都是「虚拟屏物理像素」，渲染时按 _scale 换算 DIP。
    /// </summary>
    internal sealed class PinSelectOverlayWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;

        // 拖动小于该物理像素数视为误触（与截图遮罩 ClickThresholdPx 同口径）
        private const int ClickThresholdPx = 4;

        private readonly System.Drawing.Rectangle _virtualBounds;
        private readonly Canvas _rootCanvas = new();
        private readonly Rectangle _rubberBand = new();
        private double _scale = 1.0; // 窗口 DPI 缩放因子（Loaded 后生效；窗口不移动则不变）

        private bool _dragging;                          // 橡皮筋拖拽进行中
        private System.Drawing.Point _dragStartPhysical; // 拖拽起点（物理像素）
        private IntPtr _previousForeground;              // 框选前的前台窗口（关闭时归还，防系统随机挑窗口激活）
        private bool _closing;                           // 已进入关闭流程（区分「用户失焦」与关闭时归还前台引发的失活，见 OnDeactivated）

        public PinSelectOverlayWindow()
        {
            _virtualBounds = ScreenCapture.GetVirtualScreenBounds();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;

            // 背景必须是 alpha 非 0 的「近乎全透明」画刷，绝不能退回 Brushes.Transparent（alpha=0）：
            // AllowsTransparency=true 的窗口走 Win32 分层窗口（WS_EX_LAYERED）合成，alpha=0 的像素不
            // 参与命中测试，鼠标事件直接穿透到下层窗口——橡皮筋拖不出来，遮罩收不到任何输入，而它
            // 又没有失焦关闭逻辑的话就会全透明地隐形挂死在最上层（PinWindow._boxSelecting 卡在 true，
            // 框选热键从此永久失效）。也不能照搬截图遮罩的 AllowsTransparency=false + 不透明黑底：
            // 框选遮罩没有冻结帧，必须透出真实桌面。alpha=1（≈0.4%）视觉上不可察觉，却让整窗参与命中测试。
            var nearTransparentBg = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            nearTransparentBg.Freeze();
            Background = nearTransparentBg;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 不抢焦点，激活统一走 Loaded 里的 WindowEnumerator.Activate
            WindowStartupLocation = WindowStartupLocation.Manual;
            ResizeMode = ResizeMode.NoResize;
            Cursor = Cursors.Cross;
            Focusable = true;

            // 橡皮筋：蓝色虚线框 + 半透明蓝填充（不响应命中测试，鼠标事件统一由窗口收）
            _rubberBand.Stroke = new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4));
            _rubberBand.StrokeThickness = 1.5;
            _rubberBand.StrokeDashArray = new DoubleCollection { 2, 2 };
            _rubberBand.Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x00, 0x78, 0xD4));
            _rubberBand.IsHitTestVisible = false;
            _rubberBand.Visibility = Visibility.Collapsed;
            _rootCanvas.Children.Add(_rubberBand);
            Content = _rootCanvas;

            // 先建句柄，再以物理像素铺满整个虚拟屏（WPF 的 Left/Top 是 DIP，混合 DPI 下不可靠）
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            SetWindowPos(hwnd, HwndTopmost, _virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height, SWP_NOACTIVATE);

            // 记录框选前前台窗口：遮罩弹出时会抢走前台（OnLoaded 里 Activate + Focus 以接收 Esc），
            // 关闭前（OnClosing）据此归还，避免系统随机挑窗口激活
            _previousForeground = GetForegroundWindow();

            Loaded += OnLoaded;
            PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
            MouseMove += OnMouseMove;
            PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
            PreviewKeyDown += OnPreviewKeyDown;
        }

        private void OnLoaded(object? sender, RoutedEventArgs e)
        {
            _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            _rootCanvas.Width = _virtualBounds.Width / _scale;   // DIP = 物理 / scale
            _rootCanvas.Height = _virtualBounds.Height / _scale;

            // 低级钩子触发的显示不抢焦点，直接 Activate 会被前台锁定拒绝（窗口弹出但无键盘焦点，
            // Esc 收不到）。复用 WindowEnumerator.Activate 的 AttachThreadInput 技巧绕过前台锁定。
            WindowEnumerator.Activate(new WindowInteropHelper(this).Handle);
            Focus();

            Logger.LogInfo($"贴图框选遮罩已显示：虚拟屏 ({_virtualBounds.X},{_virtualBounds.Y}) " +
                           $"{_virtualBounds.Width}x{_virtualBounds.Height}，DPI scale={_scale:0.##}，弹出前前台窗口=0x{_previousForeground.ToInt64():X}");
        }

        // 物理像素 → 根 Canvas DIP（与截图遮罩 ToDip 同式）
        private Point ToDip(System.Drawing.Point physical) =>
            new((physical.X - _virtualBounds.X) / _scale, (physical.Y - _virtualBounds.Y) / _scale);

        private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _dragStartPhysical = System.Windows.Forms.Cursor.Position; // 物理像素（PerMonitorV2）
            _dragging = true;
            CaptureMouse();
            e.Handled = true;
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging)
                return;
            UpdateRubberBand(CurrentRubberBandRect());
        }

        // 起点与当前光标规整出的选择框（物理像素，钳制在虚拟屏内）
        private System.Drawing.Rectangle CurrentRubberBandRect()
        {
            var cur = System.Windows.Forms.Cursor.Position;
            var rect = System.Drawing.Rectangle.FromLTRB(
                Math.Min(_dragStartPhysical.X, cur.X), Math.Min(_dragStartPhysical.Y, cur.Y),
                Math.Max(_dragStartPhysical.X, cur.X), Math.Max(_dragStartPhysical.Y, cur.Y));
            var clamped = System.Drawing.Rectangle.Intersect(rect, _virtualBounds);
            return clamped.IsEmpty ? new System.Drawing.Rectangle(_virtualBounds.X, _virtualBounds.Y, 1, 1) : clamped;
        }

        // 按物理像素选择框更新橡皮筋视觉（换算成 Canvas DIP）
        private void UpdateRubberBand(System.Drawing.Rectangle physicalRect)
        {
            var dip = ToDip(new System.Drawing.Point(physicalRect.X, physicalRect.Y));
            Canvas.SetLeft(_rubberBand, dip.X);
            Canvas.SetTop(_rubberBand, dip.Y);
            _rubberBand.Width = physicalRect.Width / _scale;
            _rubberBand.Height = physicalRect.Height / _scale;
            _rubberBand.Visibility = Visibility.Visible;
        }

        private void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragging)
            {
                _dragging = false;
                ReleaseMouseCapture();
            }
            e.Handled = true;

            var rect = CurrentRubberBandRect();
            // 宽高都过小 = 误触/单击，忽略不选中；否则应用框选结果
            if (rect.Width >= ClickThresholdPx && rect.Height >= ClickThresholdPx)
                PinWindow.ApplyBoxSelection(rect);
            // 松手即结束本次框选会话，无论是否选中
            Close();
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close(); // 取消本次框选（不改变已有选中）
            }
        }

        /// <summary>
        /// 失焦即取消本次框选并关闭遮罩（不改变已有选中）。框选会话本是一次性交互，失焦即取消符合
        /// MainWindow/ClipboardWindow「失焦即隐藏」的既有风格；更重要的是兜底防卡死——即使将来
        /// 遮罩再因故收不到输入，也会随失焦立即关闭、复位 PinWindow._boxSelecting，而不是隐形挂死。
        /// 不会误触发：本窗以 ShowActivated=false 弹出，只有 OnLoaded 里 WindowEnumerator.Activate
        /// 成功激活过之后才可能收到 Deactivated（从未激活成功的窗口没有失焦可触发）。
        ///
        /// 代价是刻意接受的：拖橡皮筋期间若有别的窗口抢走前台（后台更新检查恰好弹出 UpdateWindow、
        /// 第三方 Topmost 弹窗等），进行中的框选会被取消，用户需重按框选热键。相比「遮罩隐形挂死、
        /// 此后框选功能彻底失效且只能重启程序」，取消一次框选是明显更小的代价。
        /// </summary>
        protected override void OnDeactivated(EventArgs e)
        {
            base.OnDeactivated(e);

            // 已在关闭流程中就不是「用户失焦」了：正常关闭路径（松手/Esc）里 OnClosing 会激活
            // 框选前的前台窗口，那个 Activate 若目标是本进程同线程的窗口，WM_ACTIVATE 是同步
            // 派发的——此刻窗口尚未隐藏，单看 IsVisible 会把它误判成失焦、记下一条假的
            // 「失焦取消框选」日志（行为无害，WPF 的 _isClosing 会吞掉重入的 Close，但日志是
            // 本项目排查问题的主要依据，不能留假记录）。故用显式的关闭意图标志判定。
            if (_closing)
                return;

            Logger.LogInfo("贴图框选遮罩失焦，取消本次框选并关闭");
            Close();
        }

        /// <summary>
        /// 关闭进行中（遮罩仍全屏覆盖、尚未销毁）时归还框选前的前台窗口：此刻先把原窗口激活到
        /// 遮罩之下，遮罩销毁时自身已非活动窗口，系统不会再自行挑窗口激活——与截图遮罩
        /// OnClosing 同一做法。置空保证幂等（重复关闭无事发生）。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            _closing = true; // 归还前台会同步触发 Deactivated，见 OnDeactivated
            if (_previousForeground != IntPtr.Zero && IsWindow(_previousForeground))
                WindowEnumerator.Activate(_previousForeground);
            _previousForeground = IntPtr.Zero;
            base.OnClosing(e);
        }
    }
}
