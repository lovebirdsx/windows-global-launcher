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

        public PinSelectOverlayWindow()
        {
            _virtualBounds = ScreenCapture.GetVirtualScreenBounds();

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent; // 橡皮筋之外的区域全透明，直接透出底下的桌面
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
        /// 关闭进行中（遮罩仍全屏覆盖、尚未销毁）时归还框选前的前台窗口：此刻先把原窗口激活到
        /// 遮罩之下，遮罩销毁时自身已非活动窗口，系统不会再自行挑窗口激活——与截图遮罩
        /// OnClosing 同一做法。置空保证幂等（重复关闭无事发生）。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (_previousForeground != IntPtr.Zero && IsWindow(_previousForeground))
                WindowEnumerator.Activate(_previousForeground);
            _previousForeground = IntPtr.Zero;
            base.OnClosing(e);
        }
    }
}
