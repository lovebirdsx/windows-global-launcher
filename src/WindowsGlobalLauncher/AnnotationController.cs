using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CommandLauncher
{
    /// <summary>截图标注工具。</summary>
    public enum AnnotationTool { None, Rectangle, Ellipse, Arrow, Pen, Text }

    /// <summary>
    /// 截图标注层：管理宿主 Canvas 上的标注元素（矩形/椭圆/箭头/画笔/文字），支持撤销。
    /// 坐标语义：宿主把 Canvas 坐标系（DIP）的鼠标点转发给本控制器；本控制器创建的所有视觉元素
    /// 均为 Canvas 的直接子元素（不使用 Adorner/Popup），保证宿主对 Canvas 做 VisualBrush 截图时能完整带上标注。
    /// 已落定的标注元素 IsHitTestVisible = false；命中测试由控制器自行做几何判断（支持点击拖动移动）。
    /// 撤销栈只压入「已落定」元素；编辑中的文字框不落定即不入栈。
    /// </summary>
    public sealed class AnnotationController
    {
        // ---- 常量 ----
        private const double MinShapeSize = 3.0;            // 矩形/椭圆宽和高都小于该值视为误点击
        private const double MinArrowLength = 3.0;          // 箭头全长小于该值视为误点击
        private const double PenMinPointDistance = 1.5;     // 画笔抽稀：与上一点距离小于该值则跳过
        private const double EditingBoxApproxHeight = 24.0; // 编辑框未布局完成时的高度估算（字号16 + 边框1×2 + 内边距2×2）

        /// <summary>线宽可调范围（滚轮步进 1）。</summary>
        public const double MinStrokeWidth = 1.0;
        public const double MaxStrokeWidth = 12.0;

        /// <summary>字号可调范围（滚轮步进 1）。</summary>
        public const double MinTextFontSize = 8.0;
        public const double MaxTextFontSize = 48.0;

        private readonly Canvas _canvas;

        /// <summary>本控制器创建的全部元素（含编辑中文字框与拖拽中未落定元素），用于 Clear 只删自己的。</summary>
        private readonly HashSet<UIElement> _ownedElements = new();

        /// <summary>撤销栈：仅包含已落定元素（编辑中 TextBox 不入栈）。</summary>
        private readonly Stack<UIElement> _undoStack = new();

        /// <summary>箭头端点登记：滚轮调线宽重算「刚刚绘制的箭头」时用（from, to）。</summary>
        private readonly Dictionary<Polygon, (Point From, Point To)> _arrowEndpoints = new();

        private AnnotationTool _activeTool = AnnotationTool.None;
        private Color _strokeColor = Color.FromRgb(255, 64, 64);
        private double _strokeWidth = 3.0;   // 线宽（箭头杆宽与之共用）
        private double _textFontSize = 16.0; // 文字字号

        // ---- 拖拽中状态（矩形/椭圆/箭头/画笔共用） ----
        private Point _dragStart;
        private Point _dragCurrent;        // 拖拽中的当前鼠标位置（滚轮实时改拖拽中箭头用）
        private UIElement? _dragElement;   // 未落定的拖拽元素
        private bool _isDragging;

        // ---- 移动已落定元素状态 ----
        private UIElement? _movingElement;
        private Vector _moveGrabOffset;    // 点击点相对元素锚点（包围盒左上）的偏移
        private bool _isMoving;

        // ---- 编辑中文字框（同一时刻至多一个） ----
        private TextBox? _editingTextBox;

        /// <param name="strokeWidth">初始线宽（会被 clamp 到可调范围）。</param>
        /// <param name="textFontSize">初始文字字号（会被 clamp 到可调范围）。</param>
        public AnnotationController(Canvas canvas, double strokeWidth = 3.0, double textFontSize = 16.0)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
            _strokeWidth = Math.Clamp(strokeWidth, MinStrokeWidth, MaxStrokeWidth);
            _textFontSize = Math.Clamp(textFontSize, MinTextFontSize, MaxTextFontSize);
        }

        /// <summary>当前标注工具；None = 不处理鼠标事件。切换工具时防御性收尾未落定拖拽并落定编辑中文字。</summary>
        public AnnotationTool ActiveTool
        {
            get => _activeTool;
            set
            {
                if (_activeTool == value)
                    return;
                DiscardPendingDrag();
                CommitPendingText();
                _activeTool = value;
            }
        }

        /// <summary>描边/填充颜色，默认红。作用于「正在绘制的对象」，或（无正在绘制时）「刚刚绘制的那个」，并影响之后创建的元素。</summary>
        public Color StrokeColor
        {
            get => _strokeColor;
            set
            {
                _strokeColor = value;
                if (_dragElement != null)
                    ApplyStrokeColorTo(_dragElement);   // 正在拖拽绘制的对象实时跟随
                if (_editingTextBox != null)
                {
                    _editingTextBox.Foreground = NewStrokeBrush();
                    _editingTextBox.BorderBrush = NewStrokeBrush();
                }
                // 无正在绘制/编辑的对象时，栈顶才是「刚刚绘制的那个」，此时才回写它
                if (_dragElement == null && _editingTextBox == null && _undoStack.Count > 0)
                    ApplyStrokeColorTo(_undoStack.Peek());
            }
        }

        /// <summary>当前线宽（DIP）。新元素与滚轮回写均以此为准。</summary>
        public double StrokeWidth => _strokeWidth;

        /// <summary>当前文字字号（DIP）。新元素与滚轮回写均以此为准。</summary>
        public double TextFontSize => _textFontSize;

        /// <summary>按增量调线宽：只作用于「正在绘制的对象」，或（无正在绘制时）「刚刚绘制的那个」，不回写更早的历史对象。</summary>
        public void AdjustStrokeWidth(double delta)
        {
            _strokeWidth = Math.Clamp(_strokeWidth + delta, MinStrokeWidth, MaxStrokeWidth);
            // 正在拖拽绘制的形状实时跟随（箭头用拖拽起点与当前位置重算）
            if (_dragElement is Polygon dragArrow)
                dragArrow.Points = ScreenshotGeometry.BuildArrowPolygon(_dragStart, _dragCurrent, _strokeWidth);
            else if (_dragElement is Shape dragShape)
                dragShape.StrokeThickness = _strokeWidth;
            // 无正在拖拽时，栈顶才是「刚刚绘制的那个」
            if (_dragElement == null && _undoStack.Count > 0)
                ApplyStrokeWidthTo(_undoStack.Peek());
        }

        /// <summary>按增量调字号：只作用于「正在编辑的文字框」，或（无正在编辑时）「刚刚落定的文字」，不回写更早的历史文字。</summary>
        public void AdjustTextFontSize(double delta)
        {
            _textFontSize = Math.Clamp(_textFontSize + delta, MinTextFontSize, MaxTextFontSize);
            if (_editingTextBox != null)
                _editingTextBox.FontSize = _textFontSize;
            // 无正在编辑时，栈顶才是「刚刚绘制的那个」
            if (_editingTextBox == null && _undoStack.Count > 0 && _undoStack.Peek() is TextBlock block)
                block.FontSize = _textFontSize;
        }

        /// <summary>把当前线宽应用到单个线条类元素（箭头经登记的端点重算几何，矩形/椭圆/画笔改 StrokeThickness）。</summary>
        private void ApplyStrokeWidthTo(UIElement element)
        {
            if (element is Polygon arrow && _arrowEndpoints.TryGetValue(arrow, out var ends))
                arrow.Points = ScreenshotGeometry.BuildArrowPolygon(ends.From, ends.To, _strokeWidth);
            else if (element is Shape shape)
                shape.StrokeThickness = _strokeWidth;
        }

        /// <summary>把当前颜色应用到单个标注元素（箭头改 Fill，矩形/椭圆/画笔改 Stroke，文字改 Foreground）。</summary>
        private void ApplyStrokeColorTo(UIElement element)
        {
            switch (element)
            {
                case Polygon polygon:
                    polygon.Fill = NewStrokeBrush();
                    break;
                case Shape shape:
                    shape.Stroke = NewStrokeBrush();
                    break;
                case TextBlock block:
                    block.Foreground = NewStrokeBrush();
                    break;
            }
        }

        /// <summary>是否有已落定标注，或存在内容非空白的编辑中文字框。</summary>
        public bool HasAnnotations =>
            _undoStack.Count > 0 ||
            (_editingTextBox != null && !string.IsNullOrWhiteSpace(_editingTextBox.Text));

        // ================= 鼠标事件入口（宿主转发 Canvas DIP 坐标） =================

        public void OnMouseDown(Point dip)
        {
            if (_activeTool == AnnotationTool.None)
                return;

            // 防御：上一次拖拽未正常收尾（如鼠标在窗口外松开），先丢弃未落定元素
            DiscardPendingDrag();

            // 命中已落定元素 → 进入移动模式（优先于新建；编辑中的文字框不入撤销栈、不会被命中）
            UIElement? hit = HitTestAnnotation(dip);
            if (hit != null)
            {
                CommitPendingText(); // 落定编辑中的文字，避免与移动并存
                BeginMove(hit, dip);
                return;
            }

            if (_activeTool == AnnotationTool.Text)
            {
                OnTextMouseDown(dip);
                return;
            }

            // 形状类工具：记起点并创建未落定元素
            _dragStart = dip;
            _dragCurrent = dip;
            UIElement element;
            switch (_activeTool)
            {
                case AnnotationTool.Rectangle:
                case AnnotationTool.Ellipse:
                    Shape shape = _activeTool == AnnotationTool.Rectangle ? new Rectangle() : new Ellipse();
                    shape.Stroke = NewStrokeBrush();
                    shape.StrokeThickness = _strokeWidth;
                    shape.IsHitTestVisible = false;
                    Canvas.SetLeft(shape, dip.X);
                    Canvas.SetTop(shape, dip.Y);
                    shape.Width = 0;
                    shape.Height = 0;
                    element = shape;
                    break;

                case AnnotationTool.Arrow:
                    element = new Polygon
                    {
                        Fill = NewStrokeBrush(),
                        IsHitTestVisible = false,
                        Points = new PointCollection()
                    };
                    break;

                case AnnotationTool.Pen:
                    var pen = new Polyline
                    {
                        Stroke = NewStrokeBrush(),
                        StrokeThickness = _strokeWidth,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = PenLineCap.Round,
                        StrokeEndLineCap = PenLineCap.Round,
                        IsHitTestVisible = false
                    };
                    pen.Points.Add(dip);
                    element = pen;
                    break;

                default:
                    return; // None 已在上面返回；未知工具安全忽略
            }

            _dragElement = element;
            _canvas.Children.Add(element);
            _ownedElements.Add(element);
            _isDragging = true;
        }

        public void OnMouseMove(Point dip)
        {
            if (_isMoving && _movingElement != null)
            {
                MoveElement(_movingElement, dip - _moveGrabOffset);
                return;
            }

            if (_activeTool == AnnotationTool.None || !_isDragging || _dragElement == null)
                return;

            _dragCurrent = dip;

            switch (_dragElement)
            {
                case Rectangle _:
                case Ellipse _:
                    var rect = ScreenshotGeometry.Normalize(_dragStart, dip);
                    Canvas.SetLeft(_dragElement, rect.X);
                    Canvas.SetTop(_dragElement, rect.Y);
                    ((Shape)_dragElement).Width = rect.Width;
                    ((Shape)_dragElement).Height = rect.Height;
                    break;

                case Polygon arrow:
                    // 距离过短时返回空集合，直接赋值即相当于清空
                    arrow.Points = ScreenshotGeometry.BuildArrowPolygon(_dragStart, dip, _strokeWidth);
                    break;

                case Polyline pen:
                    Point last = pen.Points[pen.Points.Count - 1];
                    if ((dip - last).Length >= PenMinPointDistance)
                        pen.Points.Add(dip);
                    break;
            }
        }

        public void OnMouseUp(Point dip)
        {
            if (_isMoving)
            {
                _isMoving = false;
                _movingElement = null;
                return;
            }

            if (_activeTool == AnnotationTool.None || !_isDragging || _dragElement == null)
                return;

            UIElement element = _dragElement;
            bool keep = true;

            switch (element)
            {
                case Rectangle _:
                case Ellipse _:
                    // 宽和高都小于阈值视为误点击
                    var rect = ScreenshotGeometry.Normalize(_dragStart, dip);
                    if (rect.Width < MinShapeSize && rect.Height < MinShapeSize)
                        keep = false;
                    break;

                case Polygon _:
                    if ((dip - _dragStart).Length < MinArrowLength)
                        keep = false;
                    break;

                case Polyline pen:
                    // 补上最终点（同抽稀规则，避免抖动重复点）
                    Point last = pen.Points[pen.Points.Count - 1];
                    if ((dip - last).Length >= PenMinPointDistance)
                        pen.Points.Add(dip);
                    if (pen.Points.Count < 2)
                        keep = false;
                    break;
            }

            _isDragging = false;
            _dragElement = null;

            if (keep)
            {
                if (element is Polygon arrow)
                {
                    // 登记端点并按松开位置重算一次，保证箭头终点与 mouse-up 位置严格一致
                    _arrowEndpoints[arrow] = (_dragStart, dip);
                    arrow.Points = ScreenshotGeometry.BuildArrowPolygon(_dragStart, dip, _strokeWidth);
                }
                _undoStack.Push(element);
            }
            else
                RemoveElement(element); // 误点击：移除不落定
        }

        // ================= 文字工具 =================

        /// <summary>文字工具的 MouseDown：先落定旧编辑框，再在点击处新建编辑框；点击落在当前编辑框内则忽略。</summary>
        private void OnTextMouseDown(Point dip)
        {
            if (_editingTextBox != null)
            {
                // 防止「点击已存在编辑框内部」误建新框
                double x = Canvas.GetLeft(_editingTextBox);
                double y = Canvas.GetTop(_editingTextBox);
                double w = _editingTextBox.ActualWidth > 0 ? _editingTextBox.ActualWidth : _editingTextBox.MinWidth;
                double h = _editingTextBox.ActualHeight > 0 ? _editingTextBox.ActualHeight : EditingBoxApproxHeight;
                if (dip.X >= x && dip.X <= x + w && dip.Y >= y && dip.Y <= y + h)
                    return;

                CommitPendingText();
            }

            var brush = NewStrokeBrush();
            var tb = new TextBox
            {
                Background = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)),
                Foreground = brush,
                FontSize = _textFontSize,
                BorderBrush = brush,
                BorderThickness = new Thickness(1),
                MinWidth = 40,
                AcceptsReturn = false,
                Padding = new Thickness(2)
            };
            Canvas.SetLeft(tb, dip.X);
            Canvas.SetTop(tb, dip.Y);

            tb.LostFocus += (_, _) => CommitPendingText();
            tb.KeyDown += OnEditingTextBoxKeyDown;

            _canvas.Children.Add(tb);
            _ownedElements.Add(tb);
            _editingTextBox = tb;

            // 焦点就位，光标置于文本末尾
            tb.Focus();
            Keyboard.Focus(tb);
            tb.CaretIndex = tb.Text.Length;
        }

        /// <summary>编辑框按键：Enter 落定，Esc 放弃（均吞键防止冒泡触发宿主逻辑）。</summary>
        private void OnEditingTextBoxKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitPendingText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                DiscardPendingText();
                e.Handled = true;
            }
        }

        /// <summary>
        /// 落定正在编辑的文字框：用等样式 TextBlock（无边框无背景、同字号同色、同位置）替换并压撤销栈；
        /// 文本为空/全空白则直接移除不落定。无编辑中文字框时为 no-op。
        /// </summary>
        public void CommitPendingText()
        {
            TextBox? tb = _editingTextBox;
            if (tb == null)
                return;

            // 先清空引用，防止移除时触发的 LostFocus 重入
            _editingTextBox = null;

            string text = tb.Text ?? string.Empty;
            double x = Canvas.GetLeft(tb);
            double y = Canvas.GetTop(tb);
            RemoveElement(tb);

            if (string.IsNullOrWhiteSpace(text))
                return; // 空文本直接丢弃，不落定

            var block = new TextBlock
            {
                Text = text,
                Foreground = NewStrokeBrush(),
                FontSize = _textFontSize,
                IsHitTestVisible = false
            };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, y);

            _canvas.Children.Add(block);
            _ownedElements.Add(block);
            _undoStack.Push(block);
            Logger.LogInfo($"文字标注已落定（{(x, y)}，{text.Length} 字符）");
        }

        /// <summary>放弃编辑中的文字框：移除，不落定（Esc 路径）。</summary>
        private void DiscardPendingText()
        {
            TextBox? tb = _editingTextBox;
            if (tb == null)
                return;

            _editingTextBox = null;
            RemoveElement(tb);
            Logger.LogInfo("文字标注已放弃（Esc）");
        }

        // ================= 撤销与清空 =================

        /// <summary>撤销最近一个已落定元素；栈空时 no-op。</summary>
        public void Undo()
        {
            if (_undoStack.Count == 0)
                return;

            UIElement element = _undoStack.Pop();
            RemoveElement(element);
            Logger.LogInfo($"撤销一个标注元素（{element.GetType().Name}），剩余 {_undoStack.Count} 个");
        }

        /// <summary>清空全部标注与撤销栈（只移除本控制器创建的元素，不动宿主的其它子元素）。</summary>
        public void Clear()
        {
            // 拖拽态、移动态与编辑态一并复位（元素统一经 _ownedElements 移除）
            _isDragging = false;
            _dragElement = null;
            _isMoving = false;
            _movingElement = null;
            _editingTextBox = null;

            foreach (UIElement element in _ownedElements)
                _canvas.Children.Remove(element);
            _ownedElements.Clear();
            _undoStack.Clear();
            Logger.LogInfo("标注层已清空");
        }

        // ================= 内部辅助 =================

        /// <summary>丢弃未落定的拖拽元素（防御路径）。</summary>
        private void DiscardPendingDrag()
        {
            if (_dragElement != null)
                RemoveElement(_dragElement);
            _dragElement = null;
            _isDragging = false;
        }

        /// <summary>从 Canvas 移除元素并同步跟踪集合。</summary>
        private void RemoveElement(UIElement element)
        {
            _canvas.Children.Remove(element);
            _ownedElements.Remove(element);
            if (element is Polygon arrow)
                _arrowEndpoints.Remove(arrow);
        }

        // ================= 移动已落定元素 =================

        /// <summary>命中测试已落定元素（撤销栈为 LIFO，后画先命中），返回命中的元素或 null。</summary>
        private UIElement? HitTestAnnotation(Point dip)
        {
            foreach (UIElement element in _undoStack)
            {
                if (HitTestElement(element, dip))
                    return element;
            }
            return null;
        }

        private static bool HitTestElement(UIElement element, Point dip)
        {
            const double tolerance = 4.0; // 点击容差（DIP）
            switch (element)
            {
                case Rectangle:
                case Ellipse:
                    var shape = (Shape)element;
                    double sx = Canvas.GetLeft(shape);
                    double sy = Canvas.GetTop(shape);
                    return dip.X >= sx - tolerance && dip.X <= sx + shape.Width + tolerance &&
                           dip.Y >= sy - tolerance && dip.Y <= sy + shape.Height + tolerance;

                case Polygon polygon:
                    return InBounds(polygon.Points, dip, tolerance);

                case Polyline polyline:
                    double half = polyline.StrokeThickness / 2.0 + tolerance;
                    for (int i = 0; i < polyline.Points.Count - 1; i++)
                    {
                        if (DistanceToSegment(dip, polyline.Points[i], polyline.Points[i + 1]) <= half)
                            return true;
                    }
                    return false;

                case TextBlock block:
                    block.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    double tx = Canvas.GetLeft(block);
                    double ty = Canvas.GetTop(block);
                    double tw = Math.Max(block.DesiredSize.Width, block.ActualWidth);
                    double th = Math.Max(block.DesiredSize.Height, block.ActualHeight);
                    return dip.X >= tx - tolerance && dip.X <= tx + tw + tolerance &&
                           dip.Y >= ty - tolerance && dip.Y <= ty + th + tolerance;

                default:
                    return false;
            }
        }

        private void BeginMove(UIElement element, Point dip)
        {
            _movingElement = element;
            _moveGrabOffset = dip - GetElementAnchor(element);
            _isMoving = true;
        }

        /// <summary>元素锚点：矩形/椭圆/文字取 Canvas.Left/Top，箭头/画笔取包围盒左上角。</summary>
        private static Point GetElementAnchor(UIElement element) => element switch
        {
            Polygon polygon => BoundsTopLeft(polygon.Points),
            Polyline polyline => BoundsTopLeft(polyline.Points),
            _ => new Point(Canvas.GetLeft(element), Canvas.GetTop(element)),
        };

        /// <summary>把元素平移到新锚点（矩形/椭圆/文字改 Canvas 坐标，箭头/画笔平移点集）。</summary>
        private void MoveElement(UIElement element, Point newAnchor)
        {
            switch (element)
            {
                case Polygon polygon:
                    Vector delta = TranslatePoints(polygon.Points, newAnchor);
                    if (_arrowEndpoints.TryGetValue(polygon, out var ends))
                        _arrowEndpoints[polygon] = (ends.From + delta, ends.To + delta);
                    break;
                case Polyline polyline:
                    TranslatePoints(polyline.Points, newAnchor);
                    break;
                default:
                    Canvas.SetLeft(element, newAnchor.X);
                    Canvas.SetTop(element, newAnchor.Y);
                    break;
            }
        }

        private static Point BoundsTopLeft(PointCollection points)
        {
            double minX = double.MaxValue, minY = double.MaxValue;
            foreach (Point p in points)
            {
                minX = Math.Min(minX, p.X);
                minY = Math.Min(minY, p.Y);
            }
            return new Point(minX, minY);
        }

        private static Vector TranslatePoints(PointCollection points, Point newAnchor)
        {
            Point topLeft = BoundsTopLeft(points);
            var delta = new Vector(newAnchor.X - topLeft.X, newAnchor.Y - topLeft.Y);
            for (int i = 0; i < points.Count; i++)
                points[i] = points[i] + delta;
            return delta;
        }

        private static bool InBounds(PointCollection points, Point dip, double tolerance)
        {
            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (Point p in points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return dip.X >= minX - tolerance && dip.X <= maxX + tolerance &&
                   dip.Y >= minY - tolerance && dip.Y <= maxY + tolerance;
        }

        private static double DistanceToSegment(Point p, Point a, Point b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-9)
                return (p - a).Length;
            double t = Math.Clamp(((p.X - a.X) * dx + (p.Y - a.Y) * dy) / lenSq, 0.0, 1.0);
            double px = p.X - (a.X + t * dx);
            double py = p.Y - (a.Y + t * dy);
            return Math.Sqrt(px * px + py * py);
        }

        /// <summary>按当前描边色新建冻结画刷（每元素独立实例，冻结利于跨线程与性能）。</summary>
        private SolidColorBrush NewStrokeBrush()
        {
            var brush = new SolidColorBrush(_strokeColor);
            brush.Freeze();
            return brush;
        }
    }
}
