using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CommandLauncher
{
    /// <summary>
    /// 截图遮罩窗口：单一无边框窗口覆盖整个虚拟屏幕（PerMonitorV2 下 SetWindowPos 物理像素铺满），
    /// 显示抓屏冻结帧并承载全部截图交互。
    ///
    /// 架构要点：
    /// - 坐标系：选区真相源是「虚拟屏物理像素」System.Drawing.Rectangle；渲染时按窗口 DPI（_scale）换算 DIP。
    ///   PerMonitorV2 窗口 DWM 不做位图缩放，窗口缓冲区 1:1 对应物理像素，冻结帧在所有屏幕上像素精确。
    /// - 状态机：Hovering（窗口吸附高亮 + 放大镜取色）→ Dragging（拖拽框选）→ Selected（手柄微调 + 工具条）
    ///   → Annotating（标注工具激活，选区锁定）。
    /// - 合成：确认时 CroppedBitmap 裁冻结帧 + VisualBrush 截取标注层，RenderTargetBitmap 以
    ///   96×_scale DPI 渲染，输出像素尺寸精确等于选区物理尺寸。
    /// - Completed 事件恰好触发一次：所有确认/取消路径显式触发，OnClosed 兜底补发 Cancel。
    /// </summary>
    public sealed class ScreenshotOverlayWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const uint SWP_NOACTIVATE = 0x0010;

        private enum OverlayState { Hovering, Dragging, Selected, Annotating }

        // ---- 常量 ----
        private const int ClickThresholdPx = 4;       // 拖动小于该物理像素数视为点击（选中悬停窗口）
        private const double HandleSize = 6.0;        // 选区手柄边长（DIP）
        private const int MagSrcW = 21;               // 放大镜采样宽（物理像素）
        private const int MagSrcH = 15;               // 放大镜采样高（物理像素）
        private const double MagPixelScale = 8.0;     // 放大镜每物理像素显示为多少 DIP
        private static readonly Color AccentBlue = Color.FromRgb(0, 120, 212);

        // ---- 构造入参 ----
        private readonly BitmapSource _frozen;
        private readonly System.Drawing.Rectangle _virtualBounds;
        private readonly WindowRectSnapshot _snapshot;

        // ---- 坐标换算 ----
        private double _scale = 1.0;   // 窗口 DPI 缩放因子（Loaded 后生效；窗口不移动则不变）

        // ---- 视觉层（自底向上加入根 Canvas） ----
        private readonly Canvas _rootCanvas = new();
        private readonly Image _screenImage = new();
        private readonly Canvas _annotationCanvas = new();
        private readonly AnnotationController _annotation;
        private readonly Path _dimPath = new();
        private readonly Rectangle _selectionBorder = new();
        private readonly Rectangle[] _handles = new Rectangle[8];
        private readonly Border _sizeLabel = new();
        private readonly TextBlock _sizeLabelText = new();
        private Border _toolbar = null!;
        private Canvas _magCanvas = null!;
        private Border _magnifier = null!;
        private Image _magImage = null!;
        private Line _magCrossH = null!;
        private Line _magCrossV = null!;
        private TextBlock _magLine1 = null!;
        private TextBlock _magLine2 = null!;
        private readonly ToggleButton[] _toolButtons = new ToggleButton[5];
        private TextBlock _settingIndicator = null!;
        private static readonly AnnotationTool[] ToolOrder =
            { AnnotationTool.Rectangle, AnnotationTool.Ellipse, AnnotationTool.Arrow, AnnotationTool.Pen, AnnotationTool.Text };
        private readonly System.Collections.Generic.List<Border> _colorSwatches = new();

        // ---- 状态 ----
        private OverlayState _state = OverlayState.Hovering;
        private System.Drawing.Rectangle _selection;       // 选区（物理像素，虚拟屏坐标）
        private System.Drawing.Rectangle _hoverRect;       // 悬停高亮矩形（物理像素）
        private System.Drawing.Point _dragStartPhysical;   // 框选/移动的起点
        private System.Drawing.Rectangle _dragOriginRect;  // 移动/缩放开始时的选区
        private int _dragHandle = -1;                      // -1 无；0..7 手柄缩放；8 整体移动
        private bool _annotationMouseActive;               // 标注拖拽进行中（已转发 OnMouseDown）
        private bool _completed;                           // Completed 是否已触发（恰好一次保证）
        private SnipResult? _pendingResult;                // 确认动作暂存的结果，OnClosed 时统一触发
        private Color _magColor = Colors.Black;            // 放大镜当前像素色（C 键复制用）
        private DateTime _copiedHintUntil = DateTime.MinValue; // 「已复制」提示的截止时刻

        /// <summary>手柄定义：位置系数（0/0.5/1）+ 该手柄控制哪些边。索引与 _handles 对应。</summary>
        private static readonly (double Fx, double Fy, bool L, bool T, bool R, bool B)[] HandleDefs =
        {
            (0.0, 0.0, true,  true,  false, false), // 左上
            (0.5, 0.0, false, true,  false, false), // 上中
            (1.0, 0.0, false, true,  true,  false), // 右上
            (0.0, 0.5, true,  false, false, false), // 左中
            (1.0, 0.5, false, false, true,  false), // 右中
            (0.0, 1.0, true,  false, false, true),  // 左下
            (0.5, 1.0, false, false, false, true),  // 下中
            (1.0, 1.0, false, false, true,  true),  // 右下
        };

        private static readonly Cursor[] HandleCursors =
        {
            Cursors.SizeNWSE, Cursors.SizeNS, Cursors.SizeNESW, Cursors.SizeWE,
            Cursors.SizeWE, Cursors.SizeNESW, Cursors.SizeNS, Cursors.SizeNWSE,
        };

        /// <summary>选区确定/取消时触发（一次会话恰好触发一次），由 ScreenshotManager 订阅分发。</summary>
        public event Action<SnipResult>? Completed;

        public ScreenshotOverlayWindow(BitmapSource frozen, System.Drawing.Rectangle virtualBounds, WindowRectSnapshot snapshot)
        {
            _frozen = frozen;
            _virtualBounds = virtualBounds;
            _snapshot = snapshot;
            // 恢复上次会话的颜色/线宽/字号（新元素直接沿用，会话结束再写回 AppState）
            _annotation = new AnnotationController(
                _annotationCanvas,
                AppState.Instance.GetAnnotationStrokeWidth(),
                AppState.Instance.GetAnnotationTextFontSize());
            _annotation.StrokeColor = ScreenshotGeometry.ParseHex(AppState.Instance.GetAnnotationStrokeColor());

            WindowStyle = WindowStyle.None;
            AllowsTransparency = false;     // 整窗不透明（性能好），压暗靠内容层
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.Cross;
            Focusable = true;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            Background = Brushes.Black;

            BuildVisualTree();
            Content = _rootCanvas;

            // 先建句柄，再以物理像素铺满整个虚拟屏（WPF 的 Left/Top 是 DIP，混合 DPI 下不可靠）
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            SetWindowPos(hwnd, HwndTopmost, virtualBounds.X, virtualBounds.Y, virtualBounds.Width, virtualBounds.Height, SWP_NOACTIVATE);

            Loaded += OnOverlayLoaded;
            // 刻意不订阅 DpiChanged：本窗口每次截图新建、SetWindowPos 定位后不再移动，DPI 恒定。
            // 实测系统会派发一次虚假 DpiChanged（Old==New==当前缩放），若在处理器中调用
            // ApplyLayout 等布局修改，WPF 会对这个 PerMonitorV2 全屏窗口做一次「DPI 倍数」的
            // 二次缩放——表现为屏幕左上出现黑边、冻结帧内容被放大 1.25 倍（对照实验结论：
            // 空操作处理器或不订阅均正常，处理器内改布局即复现）。
            PreviewMouseLeftButtonDown += OnOverlayPreviewMouseDown;
            MouseLeftButtonDown += OnOverlayMouseDown;
            MouseMove += OnOverlayMouseMove;
            MouseLeftButtonUp += OnOverlayMouseUp;
            PreviewKeyDown += OnOverlayPreviewKeyDown;
            PreviewMouseWheel += OnOverlayPreviewMouseWheel;
        }

        // ================= 初始化与布局 =================

        private void BuildVisualTree()
        {
            // 1. 冻结帧（物理 1:1 显示，NearestNeighbor 防模糊）
            _screenImage.Source = _frozen;
            RenderOptions.SetBitmapScalingMode(_screenImage, BitmapScalingMode.NearestNeighbor);
            _rootCanvas.Children.Add(_screenImage);

            // 2. 标注层：鼠标统一由窗口处理后转发，本层不参与命中
            _annotationCanvas.IsHitTestVisible = false;
            _rootCanvas.Children.Add(_annotationCanvas);

            // 3. 压暗层（挖洞）
            _dimPath.Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0));
            _dimPath.IsHitTestVisible = false;
            _rootCanvas.Children.Add(_dimPath);

            // 4. 选区边框 + 手柄 + 尺寸角标
            _selectionBorder.Stroke = new SolidColorBrush(AccentBlue);
            _selectionBorder.StrokeThickness = 2;
            _selectionBorder.IsHitTestVisible = false;
            _selectionBorder.Visibility = Visibility.Collapsed;
            _rootCanvas.Children.Add(_selectionBorder);

            for (int i = 0; i < _handles.Length; i++)
            {
                int index = i; // 闭包捕获
                var handle = new Rectangle
                {
                    Width = HandleSize,
                    Height = HandleSize,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(AccentBlue),
                    StrokeThickness = 1,
                    Cursor = HandleCursors[i],
                    Visibility = Visibility.Collapsed,
                };
                handle.MouseLeftButtonDown += (_, e) =>
                {
                    if (_state != OverlayState.Selected)
                        return;
                    _dragHandle = index;
                    _dragOriginRect = _selection;
                    CaptureMouse();
                    e.Handled = true;
                };
                _handles[i] = handle;
                _rootCanvas.Children.Add(handle);
            }

            _sizeLabelText.Foreground = Brushes.White;
            _sizeLabelText.FontSize = 11;
            _sizeLabel.Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30));
            _sizeLabel.CornerRadius = new CornerRadius(3);
            _sizeLabel.Padding = new Thickness(4, 2, 4, 2);
            _sizeLabel.Child = _sizeLabelText;
            _sizeLabel.IsHitTestVisible = false;
            _sizeLabel.Visibility = Visibility.Collapsed;
            _rootCanvas.Children.Add(_sizeLabel);

            // 5. 放大镜与 6. 工具条
            BuildMagnifier();
            _rootCanvas.Children.Add(_magnifier);
            BuildToolbar();
            _rootCanvas.Children.Add(_toolbar);
        }

        private void OnOverlayLoaded(object? sender, RoutedEventArgs e)
        {
            _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            ApplyLayout();

            // 低级钩子触发的显示不抢焦点，直接 Activate 会被前台锁定拒绝（窗口弹出但无键盘焦点，
            // 导致 Esc/Enter/C/方向键等快捷键失效）。复用 WindowEnumerator.Activate 的
            // AttachThreadInput 技巧绕过前台锁定，确保遮罩能接收键盘。
            WindowEnumerator.Activate(new WindowInteropHelper(this).Handle);
            Focus();

            // 初始悬停：按当前鼠标位置立即高亮，避免「弹出后一动不动就没有反馈」
            var cursor = System.Windows.Forms.Cursor.Position;
            var phys = new System.Drawing.Point(cursor.X, cursor.Y);
            UpdateHover(phys);
            UpdateMagnifier(ToDip(phys));

            Logger.LogInfo($"截图遮罩已显示：虚拟屏 ({_virtualBounds.X},{_virtualBounds.Y}) " +
                           $"{_virtualBounds.Width}x{_virtualBounds.Height}，DPI scale={_scale:0.##}");
        }

        /// <summary>按当前 _scale 布置根 Canvas 与全尺寸层（DIP = 物理 / _scale）。</summary>
        private void ApplyLayout()
        {
            double w = _virtualBounds.Width / _scale;
            double h = _virtualBounds.Height / _scale;
            _rootCanvas.Width = w;
            _rootCanvas.Height = h;
            _screenImage.Width = w;
            _screenImage.Height = h;
            _annotationCanvas.Width = w;
            _annotationCanvas.Height = h;
        }

        // ================= 坐标换算（物理像素 ↔ 根 Canvas DIP） =================

        private Point ToDip(System.Drawing.Point physical) =>
            new((physical.X - _virtualBounds.X) / _scale, (physical.Y - _virtualBounds.Y) / _scale);

        private Rect ToDipRect(System.Drawing.Rectangle physical) =>
            new((physical.X - _virtualBounds.X) / _scale, (physical.Y - _virtualBounds.Y) / _scale,
                physical.Width / _scale, physical.Height / _scale);

        private System.Drawing.Point ToPhysical(Point dip) =>
            new((int)Math.Round(dip.X * _scale) + _virtualBounds.X,
                (int)Math.Round(dip.Y * _scale) + _virtualBounds.Y);

        private Rect CanvasRectDip => new(0, 0, _rootCanvas.Width, _rootCanvas.Height);

        // ================= 鼠标交互 =================

        /// <summary>
        /// Preview 隧道阶段统一拦截双击：选区（Selected 或 Annotating 态）内双击 = 复制到剪贴板并结束。
        /// 必须用 Preview（隧道）而非 bubbling——Annotating 态下鼠标在 bubbling 阶段已被转发给标注层
        /// 绘制，且文字工具首击创建的 TextBox 会吃掉第二击，只有隧道阶段能可靠拿到双击。
        /// 从 Hovering 直接双击某窗口时，首击已完成选中、次击确认，等效「双击窗口即截取该窗口」。
        /// </summary>
        private void OnOverlayPreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2)
                return;

            if (_state is not (OverlayState.Selected or OverlayState.Annotating))
                return;

            var phys = ToPhysical(e.GetPosition(_rootCanvas));
            if (!_selection.Contains(phys))
                return;

            // 排除 1（工具条）：快速连点撤销/颜色等按钮不能误触发确认。Preview 隧道先于工具条
            // 自身的 Handled 标记，必须显式排除；且工具条可能摆放在选区内部。IsAncestorOf 不含
            // 参数自身，故同时判 v == _toolbar 更稳。
            if (e.OriginalSource is Visual v && (v == _toolbar || _toolbar.IsAncestorOf(v)))
                return;

            // 排除 2（手柄）：点击选区手柄是缩放/微调，不能当成双击确认
            if (Array.IndexOf(_handles, e.OriginalSource) >= 0)
                return;

            // 排除 3（正在编辑的非空文字框）：沿可视树向上（含起点自身）找 TextBox，有内容说明
            // 用户是在双击选词，不能吞；空白（本次双击首击刚创建的空框）则放行确认——Finish 内部
            // CommitPendingText 会把空框直接丢弃。遍历到 null 或 _rootCanvas 即停。
            DependencyObject? node = e.OriginalSource as DependencyObject;
            while (node != null && !ReferenceEquals(node, _rootCanvas))
            {
                if (node is TextBox tb && !string.IsNullOrWhiteSpace(tb.Text))
                    return;
                node = VisualTreeHelper.GetParent(node);
            }

            // 全部排除项通过：确认复制并吞掉本次事件。Handled 抑制 bubbling 的 OnOverlayMouseDown，
            // 避免第二击又被当成移动选区/开始标注。
            Finish(SnipAction.CopyToClipboard);
            e.Handled = true;
        }

        private void OnOverlayMouseDown(object sender, MouseButtonEventArgs e)
        {
            // 文字标注编辑中丢焦点会导致后续键盘失灵，这里点击空白处时把焦点拉回窗口
            if (Keyboard.FocusedElement is not TextBox)
                Focus();

            Point dip = e.GetPosition(_rootCanvas);
            var phys = ToPhysical(dip);

            switch (_state)
            {
                case OverlayState.Hovering:
                    _dragStartPhysical = phys;
                    _state = OverlayState.Dragging;
                    CaptureMouse();
                    break;

                case OverlayState.Selected:
                    if (_selection.Contains(phys))
                    {
                        _dragHandle = 8; // 整体移动
                        _dragOriginRect = _selection;
                        _dragStartPhysical = phys;
                        CaptureMouse();
                    }
                    break;

                case OverlayState.Annotating:
                    if (_selection.Contains(phys))
                    {
                        _annotation.OnMouseDown(dip);
                        _annotationMouseActive = true;
                        CaptureMouse();
                    }
                    break;
            }
        }

        private void OnOverlayMouseMove(object sender, MouseEventArgs e)
        {
            Point dip = e.GetPosition(_rootCanvas);
            var phys = ToPhysical(dip);

            switch (_state)
            {
                case OverlayState.Hovering:
                    UpdateHover(phys);
                    UpdateMagnifier(dip);
                    break;

                case OverlayState.Dragging:
                    _selection = ClampToVirtual(NormalizePhysical(_dragStartPhysical, phys));
                    UpdateSelectionVisuals();
                    UpdateMagnifier(dip);
                    break;

                case OverlayState.Selected:
                    if (_dragHandle == 8)
                    {
                        MoveSelection(phys);
                        UpdateSelectionVisuals();
                    }
                    else if (_dragHandle >= 0)
                    {
                        ResizeByHandle(_dragHandle, phys);
                        UpdateSelectionVisuals();
                    }
                    else
                    {
                        // 选区内给出「可移动」光标反馈
                        Cursor = _selection.Contains(phys) ? Cursors.SizeAll : Cursors.Cross;
                    }
                    break;

                case OverlayState.Annotating:
                    if (_annotationMouseActive)
                        _annotation.OnMouseMove(dip);
                    break;
            }
        }

        private void OnOverlayMouseUp(object sender, MouseButtonEventArgs e)
        {
            Point dip = e.GetPosition(_rootCanvas);
            var phys = ToPhysical(dip);

            switch (_state)
            {
                case OverlayState.Dragging:
                    ReleaseMouseCapture();
                    int dx = Math.Abs(phys.X - _dragStartPhysical.X);
                    int dy = Math.Abs(phys.Y - _dragStartPhysical.Y);
                    if (dx < ClickThresholdPx && dy < ClickThresholdPx)
                        _selection = ClampToVirtual(_hoverRect); // 点击 = 选中悬停高亮的窗口/屏幕
                    if (_selection.Width < 1 || _selection.Height < 1)
                    {
                        // 无有效选区（极端情形），回到悬停态重新框选
                        _state = OverlayState.Hovering;
                        UpdateHover(phys);
                        return;
                    }
                    EnterSelected();
                    break;

                case OverlayState.Selected:
                    if (_dragHandle >= 0)
                    {
                        _dragHandle = -1;
                        ReleaseMouseCapture();
                    }
                    break;

                case OverlayState.Annotating:
                    if (_annotationMouseActive)
                    {
                        _annotation.OnMouseUp(dip);
                        _annotationMouseActive = false;
                        ReleaseMouseCapture();
                    }
                    break;
            }
        }

        /// <summary>进入 Selected 态：显示工具条与手柄、隐藏放大镜。</summary>
        private void EnterSelected()
        {
            _state = OverlayState.Selected;
            _magnifier.Visibility = Visibility.Collapsed;
            _toolbar.Visibility = Visibility.Visible;
            foreach (var handle in _handles)
                handle.Visibility = Visibility.Visible;
            UpdateSelectionVisuals();
            RestoreLastTool();
        }

        /// <summary>恢复上次会话选中的标注工具（非 None 时自动激活，进入标注态）。</summary>
        private void RestoreLastTool()
        {
            string toolName = AppState.Instance.GetAnnotationTool();
            if (!Enum.TryParse<AnnotationTool>(toolName, out var tool))
                return;
            for (int i = 0; i < ToolOrder.Length; i++)
            {
                if (ToolOrder[i] == tool)
                {
                    _toolButtons[i].IsChecked = true; // 触发 OnToolChecked，自动进入标注态
                    return;
                }
            }
        }

        /// <summary>整体移动选区（保持尺寸，钳制在虚拟屏内）。</summary>
        private void MoveSelection(System.Drawing.Point phys)
        {
            int dx = phys.X - _dragStartPhysical.X;
            int dy = phys.Y - _dragStartPhysical.Y;
            var moved = ScreenshotGeometry.NudgeOrResize(
                new Rect(_dragOriginRect.X, _dragOriginRect.Y, _dragOriginRect.Width, _dragOriginRect.Height),
                dx, dy, resize: false,
                new Rect(_virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height));
            _selection = new System.Drawing.Rectangle(
                (int)Math.Round(moved.X), (int)Math.Round(moved.Y), _dragOriginRect.Width, _dragOriginRect.Height);
        }

        /// <summary>手柄缩放：把该手柄控制的边移动到当前鼠标处，左右/上下交叉时自动翻转。</summary>
        private void ResizeByHandle(int index, System.Drawing.Point phys)
        {
            var def = HandleDefs[index];
            int l = _dragOriginRect.Left, t = _dragOriginRect.Top;
            int r = _dragOriginRect.Right, b = _dragOriginRect.Bottom;
            if (def.L) l = phys.X;
            if (def.R) r = phys.X;
            if (def.T) t = phys.Y;
            if (def.B) b = phys.Y;

            var rect = System.Drawing.Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
            if (rect.Width < 1) rect.Width = 1;
            if (rect.Height < 1) rect.Height = 1;
            _selection = ClampToVirtual(rect);
        }

        private static System.Drawing.Rectangle NormalizePhysical(System.Drawing.Point a, System.Drawing.Point b) =>
            System.Drawing.Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

        private System.Drawing.Rectangle ClampToVirtual(System.Drawing.Rectangle rect)
        {
            var clamped = System.Drawing.Rectangle.Intersect(rect, _virtualBounds);
            return clamped.IsEmpty ? new System.Drawing.Rectangle(_virtualBounds.X, _virtualBounds.Y, 1, 1) : clamped;
        }

        // ================= 键盘交互 =================

        private void OnOverlayPreviewKeyDown(object sender, KeyEventArgs e)
        {
            // 文字标注编辑中：Enter/Esc 等交给 TextBox 自己处理（AnnotationController 内已接管）
            if (Keyboard.FocusedElement is TextBox)
                return;

            bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

            switch (e.Key)
            {
                case Key.Escape:
                    CancelAndClose();
                    e.Handled = true;
                    return;

                case Key.Enter:
                    if (_state is OverlayState.Selected or OverlayState.Annotating)
                    {
                        Finish(SnipAction.CopyToClipboard);
                        e.Handled = true;
                    }
                    return;

                case Key.Z when ctrl:
                    _annotation.Undo();
                    e.Handled = true;
                    return;

                case Key.C when _state is OverlayState.Hovering or OverlayState.Dragging:
                    CopyMagnifierColor(asRgb: shift);
                    e.Handled = true;
                    return;

                case Key.Left or Key.Right or Key.Up or Key.Down when _state == OverlayState.Selected:
                    int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
                    int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                    var adjusted = ScreenshotGeometry.NudgeOrResize(
                        new Rect(_selection.X, _selection.Y, _selection.Width, _selection.Height),
                        dx, dy, resize: shift,
                        new Rect(_virtualBounds.X, _virtualBounds.Y, _virtualBounds.Width, _virtualBounds.Height));
                    _selection = new System.Drawing.Rectangle(
                        (int)Math.Round(adjusted.X), (int)Math.Round(adjusted.Y),
                        Math.Max(1, (int)Math.Round(adjusted.Width)), Math.Max(1, (int)Math.Round(adjusted.Height)));
                    UpdateSelectionVisuals();
                    e.Handled = true;
                    return;
            }
        }

        /// <summary>C / Shift+C：复制放大镜当前像素颜色（HEX / RGB），失败仅记 WARN 不中断截图。</summary>
        private void CopyMagnifierColor(bool asRgb)
        {
            string text = asRgb
                ? ScreenshotGeometry.FormatRgb(_magColor.R, _magColor.G, _magColor.B)
                : ScreenshotGeometry.FormatHex(_magColor.R, _magColor.G, _magColor.B);
            try
            {
                Clipboard.SetText(text);
                _copiedHintUntil = DateTime.Now.AddMilliseconds(800);
                _magLine2.Text = $"已复制 {text}";
                Logger.LogInfo($"已复制取色值: {text}");
            }
            catch (ExternalException ex)
            {
                Logger.LogWarning($"复制取色值失败（剪贴板被占用）: {ex.Message}");
            }
        }

        /// <summary>标注态滚轮：跟随当前工具调线宽（线条类）或字号（文字），未选工具不响应。</summary>
        private void OnOverlayPreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_state != OverlayState.Annotating)
                return;

            var tool = _annotation.ActiveTool;
            if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Arrow or AnnotationTool.Pen)
            {
                _annotation.AdjustStrokeWidth(e.Delta > 0 ? 1.0 : -1.0);
                UpdateSettingIndicator();
                e.Handled = true;
            }
            else if (tool == AnnotationTool.Text)
            {
                _annotation.AdjustTextFontSize(e.Delta > 0 ? 1.0 : -1.0);
                UpdateSettingIndicator();
                e.Handled = true;
            }
        }

        // ================= 视觉更新 =================

        /// <summary>Hovering 态：命中窗口矩形或光标所在屏幕，挖洞高亮 + 尺寸角标。</summary>
        private void UpdateHover(System.Drawing.Point phys)
        {
            var hit = _snapshot.HitTest(phys);
            System.Drawing.Rectangle raw = hit
                ?? System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(phys.X, phys.Y)).Bounds;
            _hoverRect = ClampToVirtual(raw);

            UpdateCutout(_hoverRect);
            _selectionBorder.Visibility = Visibility.Visible;
            PositionSelectionBorder(_hoverRect);
            UpdateSizeLabel(_hoverRect);
        }

        /// <summary>Selected/Dragging/Annotating 态：按选区更新挖洞、边框、手柄、角标、工具条与标注裁剪。</summary>
        private void UpdateSelectionVisuals()
        {
            UpdateCutout(_selection);
            _selectionBorder.Visibility = Visibility.Visible;
            PositionSelectionBorder(_selection);
            UpdateSizeLabel(_selection);

            var dipRect = ToDipRect(_selection);
            _annotationCanvas.Clip = new RectangleGeometry(dipRect);

            if (_state == OverlayState.Selected)
            {
                for (int i = 0; i < _handles.Length; i++)
                {
                    Canvas.SetLeft(_handles[i], dipRect.X + dipRect.Width * HandleDefs[i].Fx - HandleSize / 2);
                    Canvas.SetTop(_handles[i], dipRect.Y + dipRect.Height * HandleDefs[i].Fy - HandleSize / 2);
                }
            }

            if (_toolbar.Visibility == Visibility.Visible)
                RepositionToolbar();
        }

        /// <summary>压暗层挖洞：全屏矩形 Exclude 指定物理矩形。</summary>
        private void UpdateCutout(System.Drawing.Rectangle holePhysical)
        {
            _dimPath.Data = new CombinedGeometry(
                GeometryCombineMode.Exclude,
                new RectangleGeometry(CanvasRectDip),
                new RectangleGeometry(ToDipRect(holePhysical)));
        }

        private void PositionSelectionBorder(System.Drawing.Rectangle physical)
        {
            var dip = ToDipRect(physical);
            Canvas.SetLeft(_selectionBorder, dip.X - 1);
            Canvas.SetTop(_selectionBorder, dip.Y - 1);
            _selectionBorder.Width = Math.Max(0, dip.Width + 2);
            _selectionBorder.Height = Math.Max(0, dip.Height + 2);
        }

        /// <summary>尺寸角标：显示物理像素宽高，放选区左上角上方，放不下贴选区内。</summary>
        private void UpdateSizeLabel(System.Drawing.Rectangle physical)
        {
            _sizeLabelText.Text = $"{physical.Width} × {physical.Height}";
            _sizeLabel.Visibility = Visibility.Visible;
            _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var dip = ToDipRect(physical);
            double y = dip.Y - _sizeLabel.DesiredSize.Height - 4;
            if (y < 0)
                y = dip.Y + 4;
            double x = Math.Max(0, Math.Min(dip.X, _rootCanvas.Width - _sizeLabel.DesiredSize.Width));
            Canvas.SetLeft(_sizeLabel, x);
            Canvas.SetTop(_sizeLabel, y);
        }

        // ================= 放大镜 =================

        private void BuildMagnifier()
        {
            double magW = MagSrcW * MagPixelScale;  // 168 DIP
            double magH = MagSrcH * MagPixelScale;  // 120 DIP

            _magImage = new Image { Width = magW, Height = magH };
            RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor);

            var crossBrush = new SolidColorBrush(Color.FromArgb(200, 0, 120, 212));
            _magCrossH = new Line { Stroke = crossBrush, StrokeThickness = 1, X1 = 0, X2 = magW };
            _magCrossV = new Line { Stroke = crossBrush, StrokeThickness = 1, Y1 = 0, Y2 = magH };

            _magCanvas = new Canvas { Width = magW, Height = magH, ClipToBounds = true };
            _magCanvas.Children.Add(_magImage);
            _magCanvas.Children.Add(_magCrossH);
            _magCanvas.Children.Add(_magCrossV);

            _magLine1 = new TextBlock { Foreground = Brushes.White, FontSize = 11, Margin = new Thickness(0, 4, 0, 0) };
            _magLine2 = new TextBlock { Foreground = Brushes.White, FontSize = 11 };

            var panel = new StackPanel();
            panel.Children.Add(_magCanvas);
            panel.Children.Add(_magLine1);
            panel.Children.Add(_magLine2);

            _magnifier = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(4),
                IsHitTestVisible = false,
                Child = panel,
            };
        }

        /// <summary>放大镜跟随光标：采样窗贴边时平移采样原点（十字线随之偏移），保证 CroppedBitmap 不越界。</summary>
        private void UpdateMagnifier(Point dip)
        {
            var phys = ToPhysical(dip);
            int px = phys.X - _virtualBounds.X; // 位图像素坐标
            int py = phys.Y - _virtualBounds.Y;
            px = Math.Max(0, Math.Min(px, _frozen.PixelWidth - 1));
            py = Math.Max(0, Math.Min(py, _frozen.PixelHeight - 1));

            _magColor = ScreenCapture.GetPixel(_frozen, px, py);

            int srcW = Math.Min(MagSrcW, _frozen.PixelWidth);
            int srcH = Math.Min(MagSrcH, _frozen.PixelHeight);
            int srcX = Math.Max(0, Math.Min(px - MagSrcW / 2, _frozen.PixelWidth - srcW));
            int srcY = Math.Max(0, Math.Min(py - MagSrcH / 2, _frozen.PixelHeight - srcH));
            _magImage.Source = new CroppedBitmap(_frozen, new Int32Rect(srcX, srcY, srcW, srcH));

            // 十字线对准光标像素中心（贴边时采样窗平移，十字线偏离中心属预期）
            double crossX = (px - srcX + 0.5) * MagPixelScale;
            double crossY = (py - srcY + 0.5) * MagPixelScale;
            _magCrossH.Y1 = _magCrossH.Y2 = crossY;
            _magCrossV.X1 = _magCrossV.X2 = crossX;

            _magLine1.Text = $"POS ({phys.X}, {phys.Y})  {ScreenshotGeometry.FormatHex(_magColor.R, _magColor.G, _magColor.B)}";
            if (DateTime.Now >= _copiedHintUntil)
                _magLine2.Text = $"RGB {ScreenshotGeometry.FormatRgb(_magColor.R, _magColor.G, _magColor.B)}   C:复制HEX Shift+C:复制RGB";

            _magnifier.Visibility = Visibility.Visible;
            _magnifier.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var pos = ScreenshotGeometry.PlaceMagnifier(dip, _magnifier.DesiredSize, CanvasRectDip);
            Canvas.SetLeft(_magnifier, pos.X);
            Canvas.SetTop(_magnifier, pos.Y);
        }

        private void UpdateAllVisuals()
        {
            if (_state == OverlayState.Hovering)
                UpdateCutout(_hoverRect);
            else
                UpdateSelectionVisuals();
        }

        /// <summary>按当前选区重新测量并摆放工具条；指示器文本变化导致宽度改变时也复用此方法。</summary>
        private void RepositionToolbar()
        {
            _toolbar.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var pos = ScreenshotGeometry.PlaceToolbar(ToDipRect(_selection), _toolbar.DesiredSize, CanvasRectDip);
            Canvas.SetLeft(_toolbar, pos.X);
            Canvas.SetTop(_toolbar, pos.Y);
        }

        // ================= 工具条 =================

        private void BuildToolbar()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };

            // 五个标注工具（互斥 ToggleButton）
            (string Glyph, string Tip)[] tools =
            {
                ("□", "矩形标注"), ("○", "椭圆标注"), ("↗", "箭头标注"), ("✎", "画笔标注"), ("A", "文字标注"),
            };
            for (int i = 0; i < tools.Length; i++)
            {
                int index = i;
                var button = new ToggleButton
                {
                    Content = tools[i].Glyph,
                    ToolTip = tools[i].Tip,
                    Style = MakeToolbarButtonStyle(toggle: true),
                };
                button.Checked += (_, _) => OnToolChecked(index);
                button.Unchecked += (_, _) => OnToolUnchecked();
                _toolButtons[i] = button;
                panel.Children.Add(button);
            }

            panel.Children.Add(MakeSeparator());

            // 线宽/字号指示器：选中工具后常驻显示，滚轮实时刷新
            _settingIndicator = new TextBlock
            {
                FontSize = 11,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 2, 0),
                Visibility = Visibility.Collapsed,
            };
            panel.Children.Add(_settingIndicator);

            panel.Children.Add(MakeSeparator());

            // 颜色块
            Color[] palette = { Color.FromRgb(255, 64, 64), Color.FromRgb(255, 212, 0), AccentBlue, Colors.White };
            foreach (var color in palette)
            {
                var swatchColor = color; // 闭包捕获
                var swatch = new Border
                {
                    Width = 18,
                    Height = 18,
                    Margin = new Thickness(3, 0, 3, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Background = new SolidColorBrush(swatchColor),
                    BorderThickness = new Thickness(2),
                    CornerRadius = new CornerRadius(2),
                    Cursor = Cursors.Hand,
                    Tag = swatchColor,
                };
                swatch.MouseLeftButtonDown += (_, e) => { SelectColor(swatchColor); e.Handled = true; };
                _colorSwatches.Add(swatch);
                panel.Children.Add(swatch);
            }
            RefreshSwatchBorders();

            panel.Children.Add(MakeSeparator());

            panel.Children.Add(MakeActionButton("↶", "撤销 (Ctrl+Z)", () => _annotation.Undo()));
            panel.Children.Add(MakeActionButton("", "钉图：把选区钉为屏幕贴图", () => Finish(SnipAction.Pin), fontFamily: "Segoe MDL2 Assets"));
            panel.Children.Add(MakeActionButton("Aa", "识别文字 (OCR)", () => Finish(SnipAction.Ocr)));
            panel.Children.Add(MakeActionButton("", "保存为 PNG 文件", () => Finish(SnipAction.SaveToFile), fontFamily: "Segoe MDL2 Assets"));
            panel.Children.Add(MakeActionButton("✕", "取消 (Esc)", CancelAndClose));
            panel.Children.Add(MakeActionButton("✓", "复制到剪贴板 (Enter / 双击)", () => Finish(SnipAction.CopyToClipboard), accent: true));

            _toolbar = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(240, 30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(6),
                Visibility = Visibility.Collapsed,
                Child = panel,
            };
            // 工具条空白区的点击不能落到窗口（否则会被当成移动选区/开始标注）
            _toolbar.MouseLeftButtonDown += (_, e) => e.Handled = true;
            _toolbar.MouseLeftButtonUp += (_, e) => e.Handled = true;
        }

        private static Rectangle MakeSeparator() => new()
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
        };

        private Button MakeActionButton(string glyph, string tip, Action onClick, bool accent = false, string? fontFamily = null)
        {
            var button = new Button
            {
                Content = glyph,
                ToolTip = tip,
                Style = MakeToolbarButtonStyle(toggle: false, accent),
            };
            if (fontFamily != null)
                button.FontFamily = new FontFamily(fontFamily);
            button.Click += (_, _) => onClick();
            return button;
        }

        /// <summary>
        /// 工具条按钮深色样式（纯代码模板：Border + ContentPresenter，默认模板不响应 Background 触发器）。
        /// accent = true 时常态即蓝底（「复制」确认按钮）。
        /// </summary>
        private static Style MakeToolbarButtonStyle(bool toggle, bool accent = false)
        {
            var border = new FrameworkElementFactory(typeof(Border), "border");
            border.SetValue(Border.BackgroundProperty, accent ? new SolidColorBrush(AccentBlue) : Brushes.Transparent);
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(4));
            border.SetValue(Border.PaddingProperty, new Thickness(0));

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(content);

            var template = new ControlTemplate(toggle ? typeof(ToggleButton) : typeof(Button)) { VisualTree = border };

            var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty,
                new SolidColorBrush(accent ? Color.FromRgb(0, 100, 180) : Color.FromArgb(255, 55, 55, 55)), "border"));
            template.Triggers.Add(hover);

            if (toggle)
            {
                var check = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                check.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(AccentBlue), "border"));
                template.Triggers.Add(check);
            }

            var style = new Style(toggle ? typeof(ToggleButton) : typeof(Button));
            style.Setters.Add(new Setter(TemplateProperty, template));
            style.Setters.Add(new Setter(ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(FontSizeProperty, 14.0));
            style.Setters.Add(new Setter(WidthProperty, 30.0));   // 统一固定尺寸，内容居中
            style.Setters.Add(new Setter(HeightProperty, 30.0));
            style.Setters.Add(new Setter(MarginProperty, new Thickness(2, 0, 2, 0)));
            style.Setters.Add(new Setter(FocusableProperty, false)); // 按钮不抢窗口键盘焦点
            return style;
        }

        /// <summary>工具选中：互斥取消其余工具，进入 Annotating（选区锁定、手柄隐藏）。</summary>
        private void OnToolChecked(int index)
        {
            for (int i = 0; i < _toolButtons.Length; i++)
            {
                if (i != index && _toolButtons[i].IsChecked == true)
                    _toolButtons[i].IsChecked = false;
            }
            _annotation.ActiveTool = ToolOrder[index];
            _state = OverlayState.Annotating;
            Cursor = Cursors.Cross;
            foreach (var handle in _handles)
                handle.Visibility = Visibility.Collapsed;
            UpdateSelectionVisuals();
            UpdateSettingIndicator();
        }

        /// <summary>工具取消：若已无任何工具选中，回到 Selected（恢复手柄）。</summary>
        private void OnToolUnchecked()
        {
            foreach (var button in _toolButtons)
            {
                if (button.IsChecked == true)
                    return; // 互斥切换过程中（先 Uncheck 旧再 Check 新），另一个仍选中时不回退
            }
            _annotation.ActiveTool = AnnotationTool.None;
            if (_state == OverlayState.Annotating)
            {
                _state = OverlayState.Selected;
                foreach (var handle in _handles)
                    handle.Visibility = Visibility.Visible;
                UpdateSelectionVisuals();
                UpdateSettingIndicator();
            }
        }

        private void SelectColor(Color color)
        {
            _annotation.StrokeColor = color;
            RefreshSwatchBorders();
        }

        /// <summary>当前色块加白色描边，其余用暗描边。</summary>
        private void RefreshSwatchBorders()
        {
            foreach (var swatch in _colorSwatches)
            {
                bool selected = (Color)swatch.Tag == _annotation.StrokeColor;
                swatch.BorderBrush = selected ? Brushes.White : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
            }
        }

        /// <summary>刷新线宽/字号指示器：跟随当前工具显示「粗细 N」或「字号 N」，未选工具时隐藏。</summary>
        private void UpdateSettingIndicator()
        {
            var tool = _annotation.ActiveTool;
            if (tool is AnnotationTool.Rectangle or AnnotationTool.Ellipse or AnnotationTool.Arrow or AnnotationTool.Pen)
            {
                _settingIndicator.Text = $"粗细 {_annotation.StrokeWidth:0}";
                _settingIndicator.Visibility = Visibility.Visible;
            }
            else if (tool == AnnotationTool.Text)
            {
                _settingIndicator.Text = $"字号 {_annotation.TextFontSize:0}";
                _settingIndicator.Visibility = Visibility.Visible;
            }
            else
            {
                _settingIndicator.Visibility = Visibility.Collapsed;
            }
            RepositionToolbar();
        }

        // ================= 输出合成与收尾 =================

        /// <summary>
        /// 确认动作：合成成品图并触发 Completed 后关闭。普通动作合成「冻结帧选区裁剪 + 标注层」
        /// 为像素精确成品图；OCR 动作仅用纯冻结帧选区裁剪（不含标注/压暗，文字识别更干净）。
        /// </summary>
        private void Finish(SnipAction action)
        {
            try
            {
                _annotation.CommitPendingText();

                var sel = System.Drawing.Rectangle.Intersect(_selection, _virtualBounds);
                if (sel.Width < 1 || sel.Height < 1)
                {
                    CancelAndClose();
                    return;
                }

                var cropped = new CroppedBitmap(_frozen,
                    new Int32Rect(sel.X - _virtualBounds.X, sel.Y - _virtualBounds.Y, sel.Width, sel.Height));

                BitmapSource resultImage;
                if (action == SnipAction.Ocr)
                {
                    // OCR 用纯冻结帧选区裁剪：不含标注、不含压暗层（标注图形会干扰文字识别）；
                    // 护眼矩阵在抓屏前已挂起，冻结帧颜色干净。sel 已与虚拟屏求交，不会越界。
                    cropped.Freeze();
                    resultImage = cropped;
                }
                else
                {
                    double wDip = sel.Width / _scale;
                    double hDip = sel.Height / _scale;
                    var visual = new DrawingVisual();
                    using (DrawingContext dc = visual.RenderOpen())
                    {
                        dc.DrawImage(cropped, new Rect(0, 0, wDip, hDip));
                        if (_annotation.HasAnnotations)
                        {
                            // 用 VisualBrush 按选区（DIP）截取标注层；标注 Canvas 的 Clip=选区，与取样区域一致无相互影响
                            var brush = new VisualBrush(_annotationCanvas)
                            {
                                ViewboxUnits = BrushMappingMode.Absolute,
                                Viewbox = ToDipRect(sel),
                                Stretch = Stretch.Fill,
                            };
                            dc.DrawRectangle(brush, null, new Rect(0, 0, wDip, hDip));
                        }
                    }

                    var bitmap = new RenderTargetBitmap(sel.Width, sel.Height, 96 * _scale, 96 * _scale, PixelFormats.Pbgra32);
                    bitmap.Render(visual);
                    bitmap.Freeze();
                    resultImage = bitmap;
                }

                Logger.LogInfo($"截图确认：{action}，选区 ({sel.X},{sel.Y}) {sel.Width}x{sel.Height}");
                // 结果暂存、OnClosed 统一触发：SaveFileDialog 等模态交互必须等全屏 Topmost 遮罩
                // 关闭后再弹出，否则对话框会被遮罩挡住、看起来像卡死
                _pendingResult = new SnipResult(action, resultImage, sel);
                Close();
            }
            catch (Exception ex)
            {
                // 合成失败绝不能让全屏遮罩卡在屏幕上：按取消收尾
                Logger.LogError("截图合成失败，按取消处理", ex);
                CancelAndClose();
            }
        }

        /// <summary>
        /// 把当前选区直接钉为屏幕贴图（等同工具条 📌）。供 F7 热键路径调用：
        /// F7 被全局键盘钩子吞掉、不会到达本窗口，ScreenshotManager.PinFromClipboard
        /// 检测到截图会话进行中时转发到这里。尚未框选（Hovering/Dragging）时忽略。
        /// </summary>
        public void PinCurrentSelection()
        {
            if (_state is OverlayState.Selected or OverlayState.Annotating)
                Finish(SnipAction.Pin);
            else
                Logger.LogInfo("截图会话中按下贴图热键，但尚未框选选区，忽略");
        }

        private void CancelAndClose()
        {
            _pendingResult = new SnipResult(SnipAction.Cancel, null, default);
            Close();
        }

        /// <summary>Completed 恰好触发一次的唯一出口。</summary>
        private void RaiseCompleted(SnipResult result)
        {
            if (_completed)
                return;
            _completed = true;
            Completed?.Invoke(result);
        }

        /// <summary>
        /// 窗口关闭（确认/取消/Alt+F4 等任何方式）后统一触发 Completed：
        /// 有暂存结果则派发之，否则兜底 Cancel——保证事件恰好触发一次且在遮罩消失之后。
        /// </summary>
        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            // 会话结束写回本次的工具/颜色/线宽/字号，供下次截图恢复
            AppState.Instance.SetAnnotationSettings(
                _annotation.ActiveTool.ToString(),
                ScreenshotGeometry.FormatHex(_annotation.StrokeColor.R, _annotation.StrokeColor.G, _annotation.StrokeColor.B),
                _annotation.StrokeWidth,
                _annotation.TextFontSize);
            RaiseCompleted(_pendingResult ?? new SnipResult(SnipAction.Cancel, null, default));
        }
    }
}
