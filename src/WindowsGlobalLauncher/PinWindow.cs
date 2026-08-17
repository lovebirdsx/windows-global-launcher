using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CommandLauncher
{
    /// <summary>
    /// 贴图浮窗：把一张图片钉在屏幕最顶层，可拖动、缩放、调透明度；或把一段文字钉为
    /// 便签式贴图（深色底白字，超高可滚动）。单类双模式，经静态工厂 FromImage/FromText 创建。
    /// </summary>
    /// <remarks>
    /// DPI 语义（与项目 PerMonitorV2 约定一致）：构造传入的 physicalTopLeft、以及
    /// System.Windows.Forms.Screen 返回/接收的坐标均为「物理像素」；WPF 的 Left/Top/Width/Height
    /// 为「DIP」。换算方向：物理 = DIP × DpiScale，故物理 → DIP 一律用除法。
    /// 图片初始按 1:1 物理像素显示：基准 DIP 尺寸 = 像素尺寸 ÷ DpiScale，且必须用
    /// PixelWidth/PixelHeight（剪贴板图片的 DPI 元数据会让 BitmapSource.Width/Height 不可靠）。
    /// 文本贴图尺寸为 DIP 语义（DPI 无关）：宽固定 TextWidthDip、高按内容测量并钳制在工作区内。
    /// 构造时读一次 GetDpi，Loaded 后再读一次，若不同（目标显示器 DPI 与初始不同）则按新值校正一次。
    /// </remarks>
    public sealed class PinWindow : Window
    {
        // ---- 已打开贴图的静态跟踪（仅 UI 线程访问）：构造加入、Closed 移除 ----
        private static readonly List<PinWindow> _open = new();

        // 描边画刷：常态白色半透明，鼠标悬停变蓝（冻结以便跨实例复用）
        private static readonly Brush NormalBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
        private static readonly Brush HoverBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 120, 212)));

        private const double MinZoom = 0.1;   // 缩放下限 10%
        private const double MaxZoom = 5.0;   // 缩放上限 500%
        private const double ZoomStep = 1.1;  // 滚轮缩放步进：×1.1 / ÷1.1
        private const double OpacityStep = 0.05;
        private const double MinOpacity = 0.2;
        private const int HintHideMs = 800;   // 缩放/透明度提示角标的显示时长
        private const double BorderDip = 2.0; // 双侧各 1 DIP 描边，计算内容可视区时扣除

        // ---- 文本贴图参数 ----
        private const double TextWidthDip = 480.0;     // 文本贴图初始宽度（DIP），受所在屏工作区约束
        private const double TextMaxHeightRatio = 0.6; // 文本贴图高度上限 = 所在屏工作区高的 60%
        private const double TextPaddingDip = 10.0;    // 文本内容内边距
        private const double TextCornerRadius = 8.0;   // 文本贴图圆角（图片贴图保持 0 保证像素完整）
        private const double MinTextHeightDip = 20.0;  // 极短文本的最小内容高度
        private static readonly Brush TextBackgroundBrush = Freeze(new SolidColorBrush(Color.FromArgb(240, 30, 30, 30))); // 同剪贴板历史预览窗

        // 内容模式：图片（既有行为）或文本（便签）
        private enum ContentMode { Image, Text }

        // 文本贴图滚动容器：
        // ① Ctrl+滚轮是窗口级「调透明度」语义，此处不消费、不标记 Handled，让事件继续冒泡到
        //   PinWindow.OnMouseWheel（ScrollViewer 类处理器不检查修饰键、Ctrl 时也会照常滚动）；
        // ② 左键按下同样不处理——ScrollViewer 基类 OnMouseLeftButtonDown 会 Focus() 并标 Handled
        //   （自身不可聚焦时焦点落到窗口本身、Focus 仍返回 true），事件到不了窗口级 handler，
        //   表现为文本区无法拖动/双击关闭（边缘内边距不经 ScrollViewer 所以正常）。空实现让事件
        //   冒泡到 PinWindow.OnMouseLeftButtonDown；滚动条（ScrollBar）自行处理点击，不受影响。
        private sealed class TextScrollViewer : ScrollViewer
        {
            // 深色扁平滚动条样式（同切换器 ApplyFlatScrollBar 先例）：透明窄轨 + 细圆角半透明白
            // Thumb、隐藏箭头，与便签深色底协调。解析一次静态复用。
            private static readonly Style FlatScrollBarStyle = CreateFlatScrollBarStyle();

            public TextScrollViewer()
            {
                Resources.Add(typeof(System.Windows.Controls.Primitives.ScrollBar), FlatScrollBarStyle);
            }

            protected override void OnMouseWheel(MouseWheelEventArgs e)
            {
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
                    return;
                base.OnMouseWheel(e);
            }

            // 空实现：跳过 ScrollViewer 基类的 Focus+Handled，让事件冒泡到窗口级处理拖动/双击
            protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
            {
            }

            private static Style CreateFlatScrollBarStyle()
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
                    return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
                }
                catch (Exception ex)
                {
                    Logger.LogError("应用文本贴图滚动条样式失败", ex);
                    return new Style(typeof(System.Windows.Controls.Primitives.ScrollBar)); // 回退空样式（默认外观）
                }
            }
        }

        private readonly ContentMode _mode;
        private readonly BitmapSource? _source = null;   // 图片模式非空
        private string _text = "";                       // 文本模式非空；编辑落定时更新
        private readonly Image? _image = null;           // 图片模式非空
        private readonly TextBlock? _textBlock = null;   // 文本模式非空（展示态）
        private readonly TextBox? _editBox = null;       // 文本模式非空（编辑态）
        private readonly ScrollViewer? _textScroll = null; // 文本模式非空（展示/编辑切换 Content）
        private bool _isEditing;                         // 文本模式编辑态标志
        private readonly Border _border;
        private Border _hint = null!;                    // 左上角的缩放/透明度提示角标（不响应命中测试，InitChrome 赋值）
        private TextBlock _hintText = null!;
        private DispatcherTimer _hintTimer = null!;

        private readonly System.Drawing.Point _initialPhysicalTopLeft; // 初始位置（物理像素），Loaded 校正 DPI 时用
        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;
        private double _baseWidthDip;  // 基准 DIP 窗口尺寸（zoom=1 时，含描边），仅图片模式使用
        private double _baseHeightDip;
        private double _zoom = 1.0;

        /// <summary>把图片钉为贴图浮窗（1:1 物理像素显示）。</summary>
        /// <param name="image">要钉的图片（应已 Freeze）</param>
        /// <param name="physicalTopLeft">初始位置（虚拟屏物理像素坐标），图片以 1:1 物理像素显示</param>
        public static PinWindow FromImage(BitmapSource image, System.Drawing.Point physicalTopLeft)
            => new(image, physicalTopLeft);

        /// <summary>把文字钉为便签式贴图浮窗（深色底白字，超高可滚动）。text 须非空（调用方已过滤）。</summary>
        /// <param name="text">要钉的文字</param>
        /// <param name="physicalTopLeft">初始位置（虚拟屏物理像素坐标）</param>
        public static PinWindow FromText(string text, System.Drawing.Point physicalTopLeft)
            => new(text, physicalTopLeft);

        private PinWindow(BitmapSource image, System.Drawing.Point physicalTopLeft)
        {
            _mode = ContentMode.Image;
            _source = image;
            _initialPhysicalTopLeft = physicalTopLeft;

            InitChrome();

            // 内容：1 DIP 描边 Border（无圆角，保证图像边缘像素完整）包 Image；左上角叠提示角标
            _image = new Image { Source = image, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = NormalBorderBrush,
                Cursor = Cursors.SizeAll, // 悬停提示可拖动
                Child = _image,
            };
            _border.MouseEnter += (s, e) => _border.BorderBrush = HoverBorderBrush;
            _border.MouseLeave += (s, e) => _border.BorderBrush = NormalBorderBrush;
            var root = new Grid();
            root.Children.Add(_border);
            root.Children.Add(_hint);
            Content = root;

            ContextMenu = BuildContextMenu();

            // 提前创建句柄，保证未显示时也能取到 DPI 做坐标换算（同 ClipboardWindow 先例）
            new WindowInteropHelper(this).EnsureHandle();
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            _baseWidthDip = image.PixelWidth / _dpiScaleX;   // 1:1 物理像素 → 基准 DIP（物理 → DIP 用除法）
            _baseHeightDip = image.PixelHeight / _dpiScaleY;
            Width = _baseWidthDip;
            Height = _baseHeightDip;
            Left = physicalTopLeft.X / _dpiScaleX;
            Top = physicalTopLeft.Y / _dpiScaleY;
            ClampOutsideToNearestScreen();

            Loaded += OnLoadedRecheckDpi;
            RegisterInOpenList($"图片 {image.PixelWidth}x{image.PixelHeight} 像素");
            Show();
        }

        private PinWindow(string text, System.Drawing.Point physicalTopLeft)
        {
            _mode = ContentMode.Text;
            _text = text;
            _initialPhysicalTopLeft = physicalTopLeft;

            InitChrome();

            // 内容：深色底白字（同剪贴板历史预览窗风格），圆角 + 1 DIP 描边，超高由 ScrollViewer 滚动；
            // 滚动条正常显示——贴图窗口可交互，与预览窗（不抢焦点、点击滚动条会激活导致主窗口失焦）刻意隐藏滚动条的理由不同
            _textBlock = new TextBlock
            {
                Text = text,
                FontSize = 13,
                Foreground = Brushes.White,
                TextWrapping = TextWrapping.Wrap,
            };
            _textScroll = new TextScrollViewer
            {
                Content = _textBlock,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, // 折行后无横向滚动
            };
            // 编辑态控件（进入编辑时替换 TextBlock 显示）：多行、折行，外观融入深色底（透明底无边框）；
            // 光标设白色——默认黑色光标在深色底上看不清；Enter 落定 / Shift+Enter 换行 / Esc 取消见
            // OnEditBoxPreviewKeyDown，失焦自动保存
            _editBox = new TextBox
            {
                Text = text,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13,
                Foreground = Brushes.White,
                CaretBrush = Brushes.White,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            _editBox.PreviewKeyDown += OnEditBoxPreviewKeyDown;
            _editBox.LostKeyboardFocus += OnEditBoxLostFocus;
            _border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = NormalBorderBrush,
                CornerRadius = new CornerRadius(TextCornerRadius),
                Background = TextBackgroundBrush,
                Padding = new Thickness(TextPaddingDip),
                Cursor = Cursors.SizeAll, // 悬停提示可拖动
                Child = _textScroll,
            };
            _border.MouseEnter += (s, e) => _border.BorderBrush = HoverBorderBrush;
            _border.MouseLeave += (s, e) => _border.BorderBrush = NormalBorderBrush;
            var root = new Grid();
            root.Children.Add(_border);
            root.Children.Add(_hint);
            Content = root;

            ContextMenu = BuildContextMenu();

            new WindowInteropHelper(this).EnsureHandle();
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            Left = physicalTopLeft.X / _dpiScaleX; // 物理 → DIP 用除法
            Top = physicalTopLeft.Y / _dpiScaleY;
            ApplyTextSize();
            ClampOutsideToNearestScreen();

            Loaded += OnLoadedRecheckDpi;
            RegisterInOpenList($"文本 {text.Length} 字符");
            Show();
        }

        /// <summary>当前已打开的贴图数量。</summary>
        public static int OpenCount => _open.Count;

        /// <summary>关闭所有已打开的贴图（对列表副本逐个关闭）。</summary>
        public static void CloseAll()
        {
            foreach (var w in _open.ToArray())
                w.Close();
        }

        private static Brush Freeze(Brush brush)
        {
            brush.Freeze();
            return brush;
        }

        // 两模式共用的窗口基础设置：chrome、提示角标、输入事件、提示定时器
        private void InitChrome()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent; // 让整窗可命中测试，视觉背景由内容承载
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 弹出不抢焦点；用户点击后自然获得焦点以接收 Esc
            ResizeMode = ResizeMode.NoResize;

            _hintText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            _hint = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(6),
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                IsHitTestVisible = false, // 纯展示：不挡拖动/悬停描边
                Visibility = Visibility.Collapsed,
                Child = _hintText,
            };

            // 输入：左键拖动 / 双击关闭 / Esc 关闭 / 滚轮缩放与调透明度
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            KeyDown += OnKeyDown;

            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HintHideMs) };
            _hintTimer.Tick += (s, e) =>
            {
                _hint.Visibility = Visibility.Collapsed;
                _hintTimer.Stop();
            };
        }

        // 加入已打开贴图静态列表并挂 Closed 移除与日志（两模式共用，仅描述文案不同）
        private void RegisterInOpenList(string contentDesc)
        {
            _open.Add(this);
            Closed += (s, e) =>
            {
                _open.Remove(this);
                Logger.LogInfo($"贴图已关闭：{contentDesc}，剩余 {_open.Count} 个");
            };
            Logger.LogInfo($"贴图已创建：{contentDesc}，初始位置 ({_initialPhysicalTopLeft.X}, {_initialPhysicalTopLeft.Y})（物理像素），当前共 {_open.Count} 个");
        }

        // Loaded 后按最终所在显示器的 DPI 校正一次：若与构造时不同，重算基准尺寸与初始位置
        private void OnLoadedRecheckDpi(object sender, RoutedEventArgs e)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (Math.Abs(dpi.DpiScaleX - _dpiScaleX) < 1e-3 && Math.Abs(dpi.DpiScaleY - _dpiScaleY) < 1e-3)
                return;

            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            if (_mode == ContentMode.Image)
            {
                _baseWidthDip = _source!.PixelWidth / _dpiScaleX;
                _baseHeightDip = _source!.PixelHeight / _dpiScaleY;
                Width = _baseWidthDip * _zoom;
                Height = _baseHeightDip * _zoom;
                Left = _initialPhysicalTopLeft.X / _dpiScaleX;
                Top = _initialPhysicalTopLeft.Y / _dpiScaleY;
                ClampOutsideToNearestScreen();
            }
            else
            {
                // 文本尺寸为 DIP 语义（DPI 无关），但位置需按物理像素重换算、高度钳制依赖 scale 需重算
                Left = _initialPhysicalTopLeft.X / _dpiScaleX;
                Top = _initialPhysicalTopLeft.Y / _dpiScaleY;
                ApplyTextSize();
                ClampOutsideToNearestScreen();
            }
        }

        // 初始位置完全落在虚拟屏外时，拉回到最近屏幕的工作区内（坐标先换算成物理像素判定）
        private void ClampOutsideToNearestScreen()
        {
            double physL = Left * _dpiScaleX;
            double physT = Top * _dpiScaleY;
            double physW = Width * _dpiScaleX;   // DIP → 物理用乘法
            double physH = Height * _dpiScaleY;

            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var b = s.Bounds;
                if (physL < b.Right && physL + physW > b.Left && physT < b.Bottom && physT + physH > b.Top)
                    return; // 与任一屏幕有交叠即可见，不处理
            }

            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)physL, (int)physT)).WorkingArea;
            double newL = physW >= wa.Width ? wa.Left : Math.Clamp(physL, wa.Left, wa.Right - physW);
            double newT = physH >= wa.Height ? wa.Top : Math.Clamp(physT, wa.Top, wa.Bottom - physH);
            Left = newL / _dpiScaleX; // 物理 → DIP 用除法
            Top = newT / _dpiScaleY;
        }

        // 测量折行后的内容尺寸并应用：宽固定 TextWidthDip（受窗口当前所在屏工作区约束）；
        // 高 = 测量值，下限 MinTextHeightDip、上限工作区高 60%，超高由 ScrollViewer 滚动。
        // 构造时、Loaded DPI 校正后、编辑落定后各调一次（构造时 GetDpi 可能取到非目标显示器，一并重算）。
        private void ApplyTextSize()
        {
            // 用窗口当前位置所在屏（贴图可能已被拖到其它屏），物理像素坐标
            var winPt = new System.Drawing.Point((int)(Left * _dpiScaleX), (int)(Top * _dpiScaleY));
            var wa = System.Windows.Forms.Screen.FromPoint(winPt).WorkingArea;
            double maxWDip = Math.Min(TextWidthDip, wa.Width / _dpiScaleX); // 物理 → DIP 用除法
            double textW = maxWDip - BorderDip - TextPaddingDip * 2;        // 扣除两侧描边与内边距，与布局约束一致
            _textBlock!.Measure(new Size(textW, double.PositiveInfinity));
            double maxHDip = wa.Height / _dpiScaleY * TextMaxHeightRatio;
            double textH = Math.Min(Math.Max(_textBlock.DesiredSize.Height, MinTextHeightDip), maxHDip);
            Width = maxWDip;
            Height = textH + BorderDip + TextPaddingDip * 2;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mode == ContentMode.Text && _isEditing)
                return; // 编辑中：鼠标交给 TextBox（光标/选区），点击外部经失焦落定，窗口不拖动、双击不关闭
            if (e.ClickCount == 2) // 双击左键关闭
            {
                Close();
                return;
            }
            try
            {
                DragMove(); // 左键按下拖动
            }
            catch (InvalidOperationException)
            {
                // 按键状态异常时 DragMove 可能抛错，忽略即可
            }
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 编辑态下 Esc 已被 TextBox 的 PreviewKeyDown 拦截（标 Handled 不会到达此处），此判断为防御
            if (e.Key == Key.Escape && !_isEditing)
                Close();
        }

        // 进入编辑态：TextBox 替换 TextBlock 显示，描边变蓝提示编辑中，
        // 窗口 ContextMenu 置空让位给 TextBox 默认的剪切/复制/粘贴菜单
        private void EnterEditMode()
        {
            if (_mode != ContentMode.Text || _isEditing)
                return;
            _isEditing = true;
            _editBox!.Text = _text;
            _textScroll!.Content = _editBox;
            _border.BorderBrush = HoverBorderBrush;
            ContextMenu = null;
            _editBox.Focus();
            Keyboard.Focus(_editBox);
            _editBox.SelectAll();
        }

        // 退出编辑态并落定：cancel=true 丢弃修改恢复原文本；否则保存新文本并重测窗口尺寸（内容增减会改变高度）。
        // 先置 _isEditing=false 再切换 Content，避免 Content 切换引发的 LostKeyboardFocus 重入
        private void ExitEditMode(bool cancel)
        {
            if (!_isEditing)
                return;
            _isEditing = false;
            if (!cancel)
            {
                _text = _editBox!.Text;
                _textBlock!.Text = _text;
                ApplyTextSize();
                Logger.LogInfo($"文本贴图编辑已保存：{_text.Length} 字符");
            }
            _textScroll!.Content = _textBlock;
            _border.BorderBrush = NormalBorderBrush;
            ContextMenu = BuildContextMenu();
        }

        // 编辑态按键：Esc 取消恢复原文、Enter 落定保存（Shift+Enter 放行换行，交给 TextBox 默认处理）。
        // 用 PreviewKeyDown（隧道阶段）拦截并标 Handled：阻止 Enter 被 TextBox 换行处理、Esc 冒泡到窗口级导致关闭
        private void OnEditBoxPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                ExitEditMode(cancel: true);
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                ExitEditMode(cancel: false);
            }
        }

        // 点击别处失焦 = 完成编辑，自动保存
        private void OnEditBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_isEditing)
                ExitEditMode(cancel: false);
        }

        // 滚轮：Ctrl=调透明度（两模式共用）；普通滚轮——图片模式缩放（锚点为鼠标位置），
        // 文本模式已由 TextScrollViewer 消费滚动。Window 级类处理器无视 Handled 总会被调用，
        // 故文本分支不再重复处理（事件已被 TextScrollViewer 标记 Handled）。
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? OpacityStep : -OpacityStep), MinOpacity, 1.0);
                ShowBadge($"{Percent(Opacity)}%");
                e.Handled = true;
                return;
            }

            if (_mode == ContentMode.Image)
            {
                double factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
                ApplyZoom(_zoom * factor, e.GetPosition(_image!));
                e.Handled = true;
            }
        }

        // 应用缩放：窗口尺寸 = 基准 DIP × zoom；缩放后光标下的图像点保持不动——
        // 光标在图像中的相对位置 fx=p/oldImgW 不动 ⇒ Left' = Left + p − fx×newImgW（两侧 1 DIP 描边在等式两边抵消）。
        private void ApplyZoom(double newZoom, System.Windows.Point anchorInImage)
        {
            newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);

            double oldImgW = Width - BorderDip; // 图像可视区（DIP），与 newImgW 同一约定以保证锚点数学自洽
            double oldImgH = Height - BorderDip;
            _zoom = newZoom;
            double newW = _baseWidthDip * _zoom;
            double newH = _baseHeightDip * _zoom;
            double newImgW = newW - BorderDip;
            double newImgH = newH - BorderDip;

            if (oldImgW > 0 && newImgW > 0 && oldImgH > 0 && newImgH > 0)
            {
                Left += anchorInImage.X - anchorInImage.X / oldImgW * newImgW;
                Top += anchorInImage.Y - anchorInImage.Y / oldImgH * newImgH;
            }
            Width = newW;
            Height = newH;
            ShowBadge($"{Percent(_zoom)}%");
        }

        // 「缩放 100%」：恢复 zoom=1 与基准尺寸（位置不动），仅图片模式
        private void ResetZoom()
        {
            _zoom = 1.0;
            Width = _baseWidthDip;
            Height = _baseHeightDip;
            ShowBadge("100%");
        }

        // 左上角短暂显示缩放/透明度提示角标，800ms 后自动隐藏
        private void ShowBadge(string text)
        {
            _hintText.Text = text;
            _hint.Visibility = Visibility.Visible;
            _hintTimer.Stop(); // 连续滚动时重置计时
            _hintTimer.Start();
        }

        private static int Percent(double value) => (int)Math.Round(value * 100);

        private ContextMenu BuildContextMenu()
        {
            var menu = new ContextMenu();
            if (_mode == ContentMode.Image)
            {
                menu.Items.Add(CreateMenuItem("复制图像", CopyImageToClipboard));
                menu.Items.Add(CreateMenuItem("保存为文件…", SaveToFile));
                menu.Items.Add(CreateMenuItem("缩放 100%", ResetZoom));
            }
            else
            {
                menu.Items.Add(CreateMenuItem("编辑", EnterEditMode));
                menu.Items.Add(CreateMenuItem("复制文本", CopyTextToClipboard));
                menu.Items.Add(CreateMenuItem("保存为文件…", SaveTextToFile));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("关闭", () => Close()));
            menu.Items.Add(CreateMenuItem("关闭所有贴图", CloseAll));
            return menu;
        }

        private static MenuItem CreateMenuItem(string header, Action onClick)
        {
            var item = new MenuItem { Header = header };
            item.Click += (s, e) => onClick();
            return item;
        }

        // 复制图像回剪贴板：剪贴板被占用会抛 ExternalException，重试 3 次、每次间隔 50ms
        private void CopyImageToClipboard()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetImage(_source!);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
            Logger.LogWarning("贴图复制到剪贴板失败（重试 3 次仍被占用）");
        }

        // 复制文本回剪贴板：重试策略同 CopyImageToClipboard
        private void CopyTextToClipboard()
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(_text);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
            Logger.LogWarning("贴图文本复制到剪贴板失败（重试 3 次仍被占用）");
        }

        private void SaveToFile()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG 图像|*.png",
                DefaultExt = ".png",
                FileName = $"Pin_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(_source!));
                using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(fs);
                Logger.LogInfo($"贴图已保存：{dlg.FileName}");
            }
            catch (Exception ex)
            {
                Logger.LogError("贴图保存失败", ex);
            }
        }

        private void SaveTextToFile()
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "文本文件|*.txt",
                DefaultExt = ".txt",
                FileName = $"Pin_{DateTime.Now:yyyyMMdd_HHmmss}.txt",
            };
            if (dlg.ShowDialog() != true)
                return;

            try
            {
                File.WriteAllText(dlg.FileName, _text); // 默认 UTF-8 无 BOM
                Logger.LogInfo($"贴图文本已保存：{dlg.FileName}");
            }
            catch (Exception ex)
            {
                Logger.LogError("贴图文本保存失败", ex);
            }
        }
    }
}
