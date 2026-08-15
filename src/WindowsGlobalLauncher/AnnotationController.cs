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
    /// 已落定的标注元素 IsHitTestVisible = false（v1 不支持再次选中/编辑）。
    /// 撤销栈只压入「已落定」元素；编辑中的文字框不落定即不入栈。
    /// </summary>
    public sealed class AnnotationController
    {
        // ---- 常量 ----
        private const double StrokeWidth = 3.0;             // 统一线宽
        private const double ArrowShaftWidth = 3.0;         // 箭杆宽度（与线宽一致）
        private const double MinShapeSize = 3.0;            // 矩形/椭圆宽和高都小于该值视为误点击
        private const double MinArrowLength = 3.0;          // 箭头全长小于该值视为误点击
        private const double PenMinPointDistance = 1.5;     // 画笔抽稀：与上一点距离小于该值则跳过
        private const double TextFontSize = 16.0;
        private const double EditingBoxApproxHeight = 24.0; // 编辑框未布局完成时的高度估算（字号16 + 边框1×2 + 内边距2×2）

        private readonly Canvas _canvas;

        /// <summary>本控制器创建的全部元素（含编辑中文字框与拖拽中未落定元素），用于 Clear 只删自己的。</summary>
        private readonly HashSet<UIElement> _ownedElements = new();

        /// <summary>撤销栈：仅包含已落定元素（编辑中 TextBox 不入栈）。</summary>
        private readonly Stack<UIElement> _undoStack = new();

        private AnnotationTool _activeTool = AnnotationTool.None;
        private Color _strokeColor = Color.FromRgb(255, 64, 64);

        // ---- 拖拽中状态（矩形/椭圆/箭头/画笔共用） ----
        private Point _dragStart;
        private UIElement? _dragElement;   // 未落定的拖拽元素
        private bool _isDragging;

        // ---- 编辑中文字框（同一时刻至多一个） ----
        private TextBox? _editingTextBox;

        public AnnotationController(Canvas canvas)
        {
            _canvas = canvas ?? throw new ArgumentNullException(nameof(canvas));
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

        /// <summary>描边/填充颜色，默认红。只影响之后创建的元素；若存在编辑中文字框则同步其前景/边框色。</summary>
        public Color StrokeColor
        {
            get => _strokeColor;
            set
            {
                _strokeColor = value;
                if (_editingTextBox != null)
                {
                    _editingTextBox.Foreground = NewStrokeBrush();
                    _editingTextBox.BorderBrush = NewStrokeBrush();
                }
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

            if (_activeTool == AnnotationTool.Text)
            {
                OnTextMouseDown(dip);
                return;
            }

            // 形状类工具：记起点并创建未落定元素
            _dragStart = dip;
            UIElement element;
            switch (_activeTool)
            {
                case AnnotationTool.Rectangle:
                case AnnotationTool.Ellipse:
                    Shape shape = _activeTool == AnnotationTool.Rectangle ? new Rectangle() : new Ellipse();
                    shape.Stroke = NewStrokeBrush();
                    shape.StrokeThickness = StrokeWidth;
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
                        StrokeThickness = StrokeWidth,
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
            if (_activeTool == AnnotationTool.None || !_isDragging || _dragElement == null)
                return;

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
                    arrow.Points = ScreenshotGeometry.BuildArrowPolygon(_dragStart, dip, ArrowShaftWidth);
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
                _undoStack.Push(element);
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
                FontSize = TextFontSize,
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
                FontSize = TextFontSize,
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
            // 拖拽态与编辑态一并复位（元素统一经 _ownedElements 移除）
            _isDragging = false;
            _dragElement = null;
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
