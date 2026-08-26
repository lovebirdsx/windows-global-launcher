using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
    /// 文本贴图尺寸为 DIP 语义（DPI 无关）：宽高均按内容测量后钳制——宽在
    /// [MinTextWidthDip, TextWidthDip] 且不超所在屏工作区宽，高在 [MinTextHeightDip, 工作区高 60%]。
    /// 构造时读一次 GetDpi，Loaded 后再读一次，若不同（目标显示器 DPI 与初始不同）则按新值校正一次；
    /// 拖动结束时也刷新一次（贴图可能被拖到另一 DPI 的显示器上）。
    /// 窗口矩形 ≠ 内容矩形：窗口四周留有一圈 ShadowMarginDip 透明边距供悬停阴影绘制，
    /// 涉及「内容可见位置/大小」的代码必须按内容矩形换算——详见 ShadowMarginDip 常量处注释。
    /// </remarks>
    public sealed class PinWindow : Window
    {
        // ---- Win32 P/Invoke（点击空白/Esc 取消选中：WindowFromPoint/GetForegroundWindow 取窗口，GetWindowThreadProcessId 判进程归属）----
        [DllImport("user32.dll")]
        private static extern IntPtr WindowFromPoint(System.Drawing.Point point);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // ---- 已打开贴图的静态跟踪（仅 UI 线程访问）：构造加入、Closed 移除 ----
        private static readonly List<PinWindow> _open = new();

        // 贴图整体隐藏状态标记（仅 UI 线程访问）：HideAll 置位、ShowAll 复位；
        // _open 清空时在 Closed 处理器中复位，维持不变式「_allHidden == true ⇒ _open 非空且全部隐藏」
        private static bool _allHidden;

        // 框选选中的贴图集合（仅 UI 线程访问）：ApplyBoxSelection 落定、ClearSelection 清空、Closed 移除
        private static readonly List<PinWindow> _selected = new();
        // 框选遮罩是否已打开（防重入）
        private static bool _boxSelecting;
        // 全局鼠标钩子（懒加载常驻）：框选选中态下监听「点击空白取消选中」，见 GlobalMouseHook
        private static GlobalMouseHook? _globalMouseHook;
        // 选中描边：加粗到 2。颜色继承归属色——图片贴图亮白、文本便签继承分类色（见 ApplySelectedVisual，
        // 不再统一白色，让选中描边与内容归属一眼对应）
        private static readonly Brush SelectedBorderBrush = Freeze(new SolidColorBrush(Colors.White));
        private const double SelectedBorderThickness = 2.0;
        private const double NormalBorderThickness = 1.0;
        // 选中时边框「向外」加粗：Border 边框向内绘制，厚度 1→2 会把内容区向内压 1 DIP、改变文本折行。
        // 故选中时 Margin 缩进同步减 1（16→15），使内容区位置 = Margin + BorderThickness 保持 17 不变，
        // 边框向外扩 1 DIP、不挤压内容。
        private static readonly Thickness SelectedBorderMargin = new(ShadowMarginDip - (SelectedBorderThickness - NormalBorderThickness));

        // 描边画刷：常态白色半透明（冻结以便跨实例复用）。悬停表达改用阴影浮起（PinHoverShadow），
        // 不再变蓝描边——蓝色描边与「蓝」便签分类描边同色、无法区分；HoverBorderBrush 保留给编辑态提示用
        private static readonly Brush NormalBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)));
        private static readonly Brush HoverBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0, 120, 212)));

        // 悬停阴影（冻结以便跨实例复用）：阴影垂在元素正下方，「浮起」层次感，不动描边颜色
        private static readonly DropShadowEffect PinHoverShadow = CreateHoverShadow();

        // 悬停阴影的外围余量（DIP）：DropShadowEffect 绘制在元素**外侧**（向下偏 ShadowDepth +
        // 四周铺开 BlurRadius），客户区之外的像素会被窗口裁掉——窗口尺寸精确贴合内容时阴影
        // 100% 不可见。故窗口四周留一圈透明边距供阴影绘制：窗口变大、_border 在其中缩进。
        // 本值必须 ≥ 阴影的 BlurRadius + ShadowDepth（12 + 3 = 15，取 16 留 1 DIP 富余），
        // 改 CreateHoverShadow 的参数时要同步复核，否则阴影仍会被裁。
        //
        // 由此引入一条新不变式：「窗口矩形 ≠ 内容矩形」——内容矩形（_border 承载的图片/文本
        // 及提示角标）相对窗口矩形四周各缩进 ShadowMarginDip。凡涉及「内容在屏幕上的可见位置/
        // 大小」一律用内容矩形，凡涉及「窗口本身几何」（Left/Top/Width/Height 直接读写、拖动平移）
        // 仍用窗口矩形。必须用内容矩形的地方（漏掉任何一处都会错位）：
        //   ① PhysicalBounds —— 框选命中测试，否则会命中看不见的透明边距；
        //   ② 持久化 LeftDip/TopDip（ContentLeft/ContentTop）—— 存内容左上角，改本常量不偏移；
        //   ③ ClampOutsideToNearestScreen 的可见性判定与钳制、GetMaxContentSize(anchored) 的
        //      「到工作区右/下边缘」尺寸上限（编辑态放大锚定左上角）；
        //   ④ _initialPhysicalTopLeft —— 内容左上角物理像素，Loaded DPI 校正重算 Left/Top 的依据。
        // 反向换算：内容左上角 = 窗口 Left/Top + ShadowMarginDip，窗口宽高 = 内容宽高 + 2 × 本值。
        private const double ShadowMarginDip = 16.0;

        private static DropShadowEffect CreateHoverShadow()
        {
            var s = new DropShadowEffect
            {
                BlurRadius = 12,
                Direction = 270,
                ShadowDepth = 3,
                Opacity = 0.5,
                Color = Colors.Black,
            };
            s.Freeze();
            return s;
        }

        // 便签分类：8 个固定预设，仅描边颜色区分（分类名即颜色名，不可自定义）。
        // 灰为默认分类；画刷冻结后跨实例复用（同 NormalBorderBrush/HoverBorderBrush 先例）
        private static readonly (string Name, Brush Brush)[] NoteCategories =
        {
            ("红", Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23)))),
            ("橙", Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)))),
            ("黄", Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB9, 0x00)))),
            ("绿", Freeze(new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)))),
            ("青", Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0xB7, 0xC3)))),
            ("蓝", Freeze(new SolidColorBrush(Color.FromRgb(0x00, 0x78, 0xD4)))),
            ("紫", Freeze(new SolidColorBrush(Color.FromRgb(0x88, 0x17, 0x98)))),
            ("灰", Freeze(new SolidColorBrush(Color.FromRgb(0x76, 0x76, 0x76)))),
        };
        private const string DefaultCategory = "灰";

        // 会话内上次使用的分类（仅 UI 线程访问，同 _open 约定；不持久化）
        private static string _lastCategory = DefaultCategory;

        // 按分类名取冻结画刷，非法名防御性回退灰
        private static Brush CategoryBrush(string name)
        {
            foreach (var c in NoteCategories)
                if (c.Name == name)
                    return c.Brush;
            return NoteCategories[^1].Brush; // 灰
        }

        private const double MinZoom = 0.1;   // 缩放下限 10%
        private const double MaxZoom = 5.0;   // 缩放上限 500%
        private const double ZoomStep = 1.1;  // 滚轮缩放步进：×1.1 / ÷1.1
        private const double OpacityStep = 0.05;
        private const double MinOpacity = 0.2;
        private const int HintHideMs = 800;   // 缩放/透明度提示角标的显示时长
        private const double BorderDip = 2.0; // 双侧各 1 DIP 描边，计算内容可视区时扣除

        // ---- 文本贴图参数 ----
        private const double TextWidthDip = 480.0;     // 文本贴图宽度上限（DIP），受所在屏工作区约束
        private const double TextMaxHeightRatio = 0.6; // 文本贴图高度上限 = 所在屏工作区高的 60%
        private const double TextPaddingDip = 10.0;    // 文本内容内边距
        private const double TextCornerRadius = 8.0;   // 文本贴图圆角（图片贴图保持 0 保证像素完整）
        private const double MinTextHeightDip = 20.0;  // 极短文本的最小内容高度
        private const double MinTextWidthDip = 120.0;  // 极短文本的最小内容宽度（容得下几个汉字，也不至于被提示角标挡满）
        private const double CaretSlackDip = 2.0;      // 编辑态测量宽度余量：TextBox 比 TextBlock 多占一个插入符宽度
        private const double ScrollBarWidthDip = 8.0;  // 竖向滚动条宽度，与 TextScrollViewer 扁平样式里的 Width="8" 对应，改一处要同步另一处
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

        // 贴图唯一标识（Guid N 格式，构造生成；持久化时同时作为图片贴图的 PNG 文件名，见 PinStore）
        private readonly string _id = Guid.NewGuid().ToString("N");

        private readonly ContentMode _mode;
        private string _category = DefaultCategory; // 便签分类名（仅文本模式使用；图片模式不赋值、永不使用）
        private readonly BitmapSource? _source = null;   // 图片模式非空
        private string _text = "";                       // 文本模式非空；编辑落定时更新
        private readonly Image? _image = null;           // 图片模式非空
        private readonly TextBlock? _textBlock = null;   // 文本模式非空（展示态）
        private readonly TextBox? _editBox = null;       // 文本模式非空（编辑态）
        private readonly ScrollViewer? _textScroll = null; // 文本模式非空（展示/编辑切换 Content）
        // 测量专用 TextBlock（文本模式非空）：故意永不加入任何可视/逻辑树。
        // WPF 对已从可视树摘下的元素调 Measure 是 no-op（只留 dirty 标记、DesiredSize 停在旧值），
        // 而 _textBlock 在编辑态恰好被 _editBox 换了出去——用它测量会拿到过期尺寸（历史 bug：
        // 编辑保存后窗口尺寸纹丝不动）。改用这个从未入树的块，测量与「元素在不在树上」彻底解耦。
        private readonly TextBlock? _measureBlock = null;
        private bool _isEditing;                         // 文本模式编辑态标志
        private double _editMinWidthDip;                 // 编辑态宽度下限（= 进入编辑时的窗口宽），编辑中只增不减
        private readonly Border _border;
        private Border _hint = null!;                    // 左上角的缩放/透明度提示角标（不响应命中测试，InitChrome 赋值）
        private TextBlock _hintText = null!;
        private DispatcherTimer _hintTimer = null!;

        // ---- 拖动状态（见 OnMouseLeftButtonDown 注释：刻意不用 Window.DragMove）----
        private bool _dragPending;                       // 左键已按下、尚未松开
        private bool _dragging;                          // 已越过拖动阈值、正在移动窗口
        private System.Drawing.Point _dragStartCursor;   // 按下时的光标位置（物理像素）
        private double _dragStartLeft, _dragStartTop;    // 按下时的窗口位置（DIP）
        private double _dragScaleX = 1.0, _dragScaleY = 1.0; // 按下时的 DPI 缩放快照，整段拖动锁定同一值

        // ---- 自己做的双击判定（见 OnMouseLeftButtonDown 注释：不能信 e.ClickCount）----
        private const int MinHumanDoubleClickMs = 50;    // 人手双击的最快间隔下限，低于它的必是合成输入
        private long _lastPressTicks = -100_000;         // 上一次「被认可的」左键按下时刻
        private int _lastPressTimestamp = int.MinValue;  // 上一次按下的 WPF 消息时间戳（同值 = 同一次输入被重报）
        private System.Drawing.Point _lastPressCursor;   // 上一次按下时的光标（物理像素）
        private bool _sawReleaseSincePress = true;       // 上次按下之后是否见过释放（初始 true，首击不受影响）

        // 初始位置（物理像素，**内容左上角**——不是窗口左上角，窗口左上角 = 它 − 阴影边距），
        // Loaded 校正 DPI 时用。非 readonly：恢复持久化状态时需要回写它，
        // 否则 OnLoadedRecheckDpi 会按旧值把恢复位置弹回去（见 ApplyRestoredState）
        private System.Drawing.Point _initialPhysicalTopLeft;
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
        /// <param name="category">便签分类名（8 个预设之一）；null 表示沿用会话上次分类</param>
        public static PinWindow FromText(string text, System.Drawing.Point physicalTopLeft, string? category = null)
        {
            category ??= _lastCategory; // 无显式分类 → 沿用会话上次分类（首贴即灰）
            _lastCategory = category;   // 创建即记住，F7 连续钉同类便签免重选
            return new PinWindow(text, physicalTopLeft, category);
        }

        private PinWindow(BitmapSource image, System.Drawing.Point physicalTopLeft)
        {
            _mode = ContentMode.Image;
            _source = image;
            _initialPhysicalTopLeft = physicalTopLeft;

            InitChrome();

            // 内容：1 DIP 描边 Border（无圆角，保证图像边缘像素完整）包 Image；左上角叠提示角标。
            // _border 四周留 ShadowMarginDip 透明边距给悬停阴影（见常量注释），图像显示尺寸不变
            _image = new Image { Source = image, Stretch = Stretch.Fill };
            RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
            _border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = NormalBorderBrush,
                Cursor = Cursors.SizeAll, // 悬停提示可拖动
                Margin = new Thickness(ShadowMarginDip), // 缩进让阴影画在窗口内
                Child = _image,
            };
            _border.MouseEnter += (s, e) => _border.Effect = PinHoverShadow;
            _border.MouseLeave += (s, e) => _border.Effect = null;
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
            Width = _baseWidthDip + ShadowMarginDip * 2;     // 窗口 = 内容 + 两侧阴影边距
            Height = _baseHeightDip + ShadowMarginDip * 2;
            // physicalTopLeft 是内容左上角（物理像素），窗口左上角 = 内容左上角 − 边距，
            // 否则内容视觉位置会整体右下偏移 ShadowMarginDip
            Left = physicalTopLeft.X / _dpiScaleX - ShadowMarginDip;
            Top = physicalTopLeft.Y / _dpiScaleY - ShadowMarginDip;
            ClampOutsideToNearestScreen();

            Loaded += OnLoadedRecheckDpi;
            RegisterInOpenList($"图片 {image.PixelWidth}x{image.PixelHeight} 像素");
            ShowInitially();
        }

        private PinWindow(string text, System.Drawing.Point physicalTopLeft, string category)
        {
            _mode = ContentMode.Text;
            _text = text;
            _category = category;
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
            // 测量副本：排版相关属性与 _textBlock 保持一致（FontFamily 两者同为继承默认值，不显式设）
            _measureBlock = new TextBlock
            {
                FontSize = 13,
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
            _editBox.TextChanged += OnEditBoxTextChanged;
            _border = new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = CategoryBrush(_category),
                CornerRadius = new CornerRadius(TextCornerRadius),
                Background = TextBackgroundBrush,
                Padding = new Thickness(TextPaddingDip),
                Cursor = Cursors.SizeAll, // 悬停提示可拖动
                Margin = new Thickness(ShadowMarginDip), // 缩进让阴影画在窗口内
                Child = _textScroll,
            };
            _border.MouseEnter += (s, e) => _border.Effect = PinHoverShadow;
            _border.MouseLeave += (s, e) => _border.Effect = null; // 描边保持分类色，悬停只加阴影（蓝描边与「蓝」分类重复，已弃用）
            var root = new Grid();
            root.Children.Add(_border);
            root.Children.Add(_hint);
            Content = root;

            ContextMenu = BuildContextMenu();

            new WindowInteropHelper(this).EnsureHandle();
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            // physicalTopLeft 是内容左上角（物理像素），窗口左上角再减阴影边距（同图片模式）
            Left = physicalTopLeft.X / _dpiScaleX - ShadowMarginDip; // 物理 → DIP 用除法
            Top = physicalTopLeft.Y / _dpiScaleY - ShadowMarginDip;
            ApplyTextSize();
            ClampOutsideToNearestScreen();

            Loaded += OnLoadedRecheckDpi;
            RegisterInOpenList($"文本 {text.Length} 字符");
            ShowInitially();
        }

        /// <summary>当前已打开的贴图数量。</summary>
        public static int OpenCount => _open.Count;

        /// <summary>关闭所有已打开的贴图（对列表副本逐个关闭）。</summary>
        public static void CloseAll()
        {
            foreach (var w in _open.ToArray())
                w.Close();
        }

        /// <summary>贴图是否处于整体隐藏状态（托盘菜单文案/置灰用）。</summary>
        public static bool IsAllHidden => _allHidden;

        /// <summary>隐藏所有已打开贴图（窗口保留在 _open，Show 可恢复原位置/尺寸/透明度）。</summary>
        public static void HideAll()
        {
            if (_open.Count == 0)
            {
                Logger.LogInfo("当前无贴图，无需隐藏");
                return;
            }
            ClearSelection(); // 整体隐藏后选中无意义，避免 ShowAll 后残留选中描边
            foreach (var w in _open.ToArray()) // 副本遍历，同 CloseAll 先例
                w.Hide();
            _allHidden = true;
            Logger.LogInfo($"全部贴图已隐藏，共 {_open.Count} 个");
        }

        /// <summary>恢复显示所有被整体隐藏的贴图（含调用方新创建尚未显示的实例）。</summary>
        public static void ShowAll()
        {
            _allHidden = false;
            foreach (var w in _open.ToArray())
                w.Show();
            Logger.LogInfo($"全部贴图已恢复显示，共 {_open.Count} 个");
        }

        /// <summary>
        /// 切换全部贴图的显示/隐藏：整体隐藏 → 全部恢复；否则全部隐藏。
        /// 无贴图时忽略（仅记日志，不置状态）。
        /// </summary>
        public static void ToggleAllVisibility()
        {
            if (_open.Count == 0)
            {
                Logger.LogInfo("当前无贴图，忽略隐藏/显示切换");
                return;
            }
            if (_allHidden)
                ShowAll();
            else
                HideAll();
        }

        /// <summary>
        /// 进入框选态（热键动作 PinBoxSelect / 托盘菜单入口）：弹出全屏橡皮筋遮罩，
        /// 松手后选中与框相交的可见贴图，之后拖动任一选中贴图即整体移动。
        /// 无贴图 / 全部贴图已整体隐藏 / 已在框选 / 截图会话进行中（两个全屏窗口叠加无意义）时忽略记日志。
        /// </summary>
        public static void StartBoxSelect()
        {
            if (_boxSelecting)
            {
                Logger.LogWarning("框选遮罩已打开，忽略重复触发（PinBoxSelect）");
                return;
            }
            if (_open.Count == 0)
            {
                Logger.LogInfo("当前无贴图，忽略框选移动（PinBoxSelect）");
                return;
            }
            if (_allHidden)
            {
                // 隐藏中的贴图不参与命中（EnumerateSelectablePins 按 IsVisible 过滤），
                // 此时弹出遮罩必然选中 0 个，直接忽略并提示
                Logger.LogInfo("全部贴图处于整体隐藏状态，忽略框选移动（PinBoxSelect）");
                return;
            }
            if (ScreenshotManager.IsCapturing)
            {
                Logger.LogWarning("截图会话进行中，忽略框选移动（PinBoxSelect）");
                return;
            }
            _boxSelecting = true;
            // 进入框选流程即确保全局鼠标钩子就绪（懒加载一次）：选中态下「点击空白取消选中」依赖它
            EnsureGlobalMouseHook();
            try
            {
                var overlay = new PinSelectOverlayWindow();
                overlay.Closed += (s, e) => _boxSelecting = false;
                overlay.Show();
            }
            catch (Exception ex)
            {
                // 遮罩构造/显示失败时复位标志，避免 _boxSelecting 卡死、后续框选全部被拒
                _boxSelecting = false;
                Logger.LogError("框选遮罩创建失败", ex);
            }
        }

        /// <summary>应用框选结果：先清空旧选中，再收集与选择框（虚拟屏物理像素）相交的可见贴图。</summary>
        internal static void ApplyBoxSelection(System.Drawing.RectangleF selectRect)
        {
            ClearSelection();
            foreach (var (pin, phys) in EnumerateSelectablePins())
            {
                if (!selectRect.IntersectsWith(phys))
                    continue;
                _selected.Add(pin);
                pin.ApplySelectedVisual();
                // 整体移动的数据源是这里落定的快照：被选成员收不到本次 MouseDown，
                // 拖动广播要按「框选确定时的位置 + 本次拖动位移」平移它们
                pin._dragStartLeft = pin.Left;
                pin._dragStartTop = pin.Top;
                pin._dragScaleX = pin._dpiScaleX;
                pin._dragScaleY = pin._dpiScaleY;
            }
            Logger.LogInfo($"框选选中 {_selected.Count} 个贴图");
        }

        /// <summary>清空选中并还原描边（HideAll/重新框选/Esc 取消/Closed 前调用）。</summary>
        private static void ClearSelection()
        {
            foreach (var pin in _selected)
                pin.ApplyDefaultBorder();
            _selected.Clear();
        }

        /// <summary>是否有框选选中的贴图（供全局鼠标/键盘钩子查询，仅 UI 线程）。</summary>
        internal static bool IsAnySelected => _selected.Count > 0;

        /// <summary>是否有贴图处于文本编辑态（全局 Esc 需排除，避免吞掉编辑态的「取消编辑」）。</summary>
        internal static bool IsAnyEditing => _open.Any(p => p._isEditing);

        /// <summary>
        /// 前台窗口是否属于本进程（全局 Esc 需排除，避免吞掉本进程其它窗口——命令面板/剪贴板历史/
        /// 截图遮罩/框选遮罩/贴图等的 Esc，让它们走各自的窗口级 Esc 处理；贴图自身 OnKeyDown 已会
        /// 「有选中 → 取消选中」，语义不变）。
        /// </summary>
        internal static bool IsForegroundOwnedByThisProcess()
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero)
                return false;
            GetWindowThreadProcessId(fg, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        /// <summary>懒加载安装全局鼠标钩子（仅 UI 线程；第一次框选时调用，之后常驻）。</summary>
        private static void EnsureGlobalMouseHook()
        {
            if (_globalMouseHook == null)
            {
                _globalMouseHook = new GlobalMouseHook();
                _globalMouseHook.Install();
            }
        }

        /// <summary>全局 Esc 取消选中（KeyboardHook 回调经 Dispatcher.BeginInvoke 调用，仅 UI 线程）。</summary>
        internal static void CancelSelectionFromGlobal()
        {
            ClearSelection();
            Logger.LogInfo("按 Esc 取消框选选中");
        }

        /// <summary>
        /// 全局左键按下（GlobalMouseHook 回调，仅 UI 线程）：框选选中态下点击**非本进程**窗口
        /// （桌面/外部应用 = 空白）即取消选中；点击本进程窗口（贴图内容、右键菜单、框选遮罩等）
        /// 则放行，交给其自身处理。用「进程归属」而非「贴图枚举命中」判定空白，避免把本进程的
        /// 右键菜单误判为空白而破坏「批量复制选中文本」（CopySelectedTextsToClipboard 依赖 _selected）。
        /// </summary>
        internal static void OnGlobalLeftButtonDown(System.Drawing.Point pt)
        {
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero)
                return;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == (uint)Environment.ProcessId)
                return; // 本进程窗口：交给其自身处理
            ClearSelection();
            Logger.LogInfo("点击空白处取消框选选中");
        }

        /// <summary>单选「仅自己」（单击任意贴图时调用，仅 UI 线程；已单选则幂等）。</summary>
        private void SelectOnly()
        {
            if (_selected.Count == 1 && ReferenceEquals(_selected[0], this))
                return;
            ClearSelection();
            _selected.Add(this);
            ApplySelectedVisual();
            Logger.LogInfo("单击贴图，改为单选");
        }

        // 选中视觉：描边加粗到 2，颜色继承各自归属色——图片贴图用亮白（在任意图片上醒目），
        // 文本便签用其分类色（8 分类色即身份色，选中后仍一眼对应归属，不再统一白色）。
        // 加粗时同步减小 Margin（向外扩），内容区位置不变、不挤压文本折行。
        private void ApplySelectedVisual()
        {
            _border.BorderBrush = _mode == ContentMode.Image ? SelectedBorderBrush : CategoryBrush(_category);
            _border.BorderThickness = new Thickness(SelectedBorderThickness);
            _border.Margin = SelectedBorderMargin;
        }

        // 还原常态描边：图片 → NormalBorderBrush，文本 → 分类描边；厚度回 1、Margin 回常态缩进
        // （选中加粗时 Margin 曾减 1 向外扩，这里一并恢复）。
        // 编辑态守卫：编辑中的贴图可能仍留在 _selected（先单选再 F2 编辑），此时被
        // ClearSelection 还原描边会把编辑蓝描边抹成分类色——编辑中保持蓝描边直接返回。
        private void ApplyDefaultBorder()
        {
            if (_isEditing)
            {
                _border.BorderBrush = HoverBorderBrush;
                _border.BorderThickness = new Thickness(NormalBorderThickness);
                _border.Margin = new Thickness(ShadowMarginDip);
                return;
            }
            _border.BorderBrush = _mode == ContentMode.Image ? NormalBorderBrush : CategoryBrush(_category);
            _border.BorderThickness = new Thickness(NormalBorderThickness);
            _border.Margin = new Thickness(ShadowMarginDip);
        }

        // 供框选命中测试枚举（internal）：隐藏中（IsVisible==false）与编辑中（_isEditing）的贴图不参与
        internal static List<(PinWindow Pin, System.Drawing.RectangleF Phys)> EnumerateSelectablePins()
        {
            var result = new List<(PinWindow, System.Drawing.RectangleF)>();
            foreach (var pin in _open)
            {
                if (!pin.IsVisible || pin._isEditing)
                    continue;
                result.Add((pin, pin.PhysicalBounds));
            }
            return result;
        }

        // ---- 持久化/框选取数用的只读视图（internal，供 PinStore 与后续框选功能共用；仅 UI 线程访问）----

        /// <summary>是否图片贴图（false = 文字便签）。</summary>
        internal bool IsImagePin => _mode == ContentMode.Image;

        /// <summary>仅文字便签：当前文本内容（编辑落定时更新）。</summary>
        internal string PinText => _text;

        /// <summary>仅文字便签：分类名（8 个预设之一）。</summary>
        internal string PinCategory => _category;

        /// <summary>仅图片贴图：当前缩放比例。</summary>
        internal double PinZoom => _zoom;

        /// <summary>仅图片贴图：图片源（持久化 PNG 编码用）。</summary>
        internal BitmapSource? PinImageSource => _source;

        /// <summary>贴图唯一标识（构造生成，同 PinStore.PinEntry.Id）。</summary>
        internal string PinId => _id;

        /// <summary>内容左上角 X（DIP，窗口 Left + 阴影边距）。持久化用——存内容而非窗口坐标，
        /// 语义与 ShadowMarginDip 解耦（改边距常量不会让老数据整体偏移），见常量注释 ②。</summary>
        internal double ContentLeft => Left + ShadowMarginDip;

        /// <summary>内容左上角 Y（DIP），语义同 <see cref="ContentLeft"/>。</summary>
        internal double ContentTop => Top + ShadowMarginDip;

        /// <summary>内容矩形（虚拟屏物理像素）：窗口矩形四周缩进 ShadowMarginDip 后换算——
        /// 框选命中测试必须用内容矩形，否则会命中贴图周围看不见的透明边距（见常量注释 ①）。</summary>
        internal System.Drawing.RectangleF PhysicalBounds => new(
            (float)((Left + ShadowMarginDip) * _dpiScaleX), (float)((Top + ShadowMarginDip) * _dpiScaleY),
            (float)((Width - ShadowMarginDip * 2) * _dpiScaleX), (float)((Height - ShadowMarginDip * 2) * _dpiScaleY));

        /// <summary>当前全部已打开贴图（仅 UI 线程访问；供 PinStore 枚举保存）。</summary>
        internal static IReadOnlyList<PinWindow> OpenPins => _open;

        /// <summary>
        /// 按持久化条目重建贴图并直接显示（PinStore.RestorePins 调用，仅 UI 线程）。
        /// 文字便签刻意走私营构造而非 FromText——不污染会话「上次分类」记忆。
        /// </summary>
        internal static PinWindow RestoreFromEntry(PinStore.PinEntry entry, BitmapSource? image)
        {
            // 构造需要内容左上角的物理像素坐标，先用 DIP 原值充当近似物理点（构造里的位置随后会被
            // ApplyRestoredState 按持久化 DIP 覆写），只为让构造走完初始化流程
            var approx = new System.Drawing.Point((int)entry.LeftDip, (int)entry.TopDip);
            // 手改 JSON 可能出现非法分类名：CategoryBrush 虽回退灰描边，但非法名会原样进入
            // _category 并随持久化带回、右键分类子菜单也无勾选——恢复时直接回退默认分类
            var category = NoteCategories.Any(c => c.Name == entry.Category) ? entry.Category : DefaultCategory;
            var w = entry.IsImage ? new PinWindow(image!, approx) : new PinWindow(entry.Text, approx, category);
            w.ApplyRestoredState(entry);
            return w;
        }

        // 把持久化的位置/缩放/透明度覆写到构造出的新实例上：
        // 关键是回写 _initialPhysicalTopLeft——它是 Loaded 后 DPI 校正重算位置的依据，
        // 不回写的话恢复位置会在 OnLoadedRecheckDpi 里被弹回构造时的近似点
        private void ApplyRestoredState(PinStore.PinEntry entry)
        {
            if (_mode == ContentMode.Image)
            {
                _zoom = Math.Clamp(entry.Zoom, MinZoom, MaxZoom);
                Width = _baseWidthDip * _zoom + ShadowMarginDip * 2; // 窗口 = 内容 + 两侧阴影边距
                Height = _baseHeightDip * _zoom + ShadowMarginDip * 2;
            }
            // 持久化存的是内容左上角（ContentLeft/ContentTop），窗口左上角反向减边距
            Left = entry.LeftDip - ShadowMarginDip;
            Top = entry.TopDip - ShadowMarginDip;
            Opacity = Math.Clamp(entry.Opacity, MinOpacity, 1.0);
            // _initialPhysicalTopLeft 语义是内容左上角物理像素（见常量注释 ④），取内容坐标换算；
            // 四舍五入比向零截断少亚像素漂移（仅 DPI 变化触发重算时用得到）
            _initialPhysicalTopLeft = new System.Drawing.Point(
                (int)Math.Round((Left + ShadowMarginDip) * _dpiScaleX), (int)Math.Round((Top + ShadowMarginDip) * _dpiScaleY));
            if (_mode == ContentMode.Text)
                ApplyTextSize();
            ClampOutsideToNearestScreen();
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
            // 视觉背景由内容（_border）承载，窗口本身透明。
            // 注意 alpha=0 的像素在分层窗口（AllowsTransparency=true）下鼠标穿透，所以自
            // ShadowMarginDip 引入后，_border 四周那圈边距**未悬停时是穿透区**（点击/滚轮落到
            // 下层窗口）——这不影响使用：那圈本就不可见，而一旦鼠标进入 _border 触发悬停阴影，
            // 阴影像素令边距变为可命中，连阴影区域也能拖动。
            // 同一 alpha=0 机制在 PinSelectOverlayWindow 那里是致命的（整窗都收不到输入），
            // 详见该文件构造函数注释；此处刻意保留 Transparent。
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 弹出不抢焦点；用户点击后自然获得焦点以接收 Esc
            ResizeMode = ResizeMode.NoResize;

            _hintText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
            _hint = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                // 相对内容区（_border 内缩后的可见区域）左上角定位：仅 +6 会落在透明边距里
                Margin = new Thickness(ShadowMarginDip + 6),
                Padding = new Thickness(6, 2, 6, 2),
                Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
                IsHitTestVisible = false, // 纯展示：不挡拖动/悬停描边
                Visibility = Visibility.Collapsed,
                Child = _hintText,
            };

            // 输入：左键拖动 / 双击关闭 / Esc 关闭 / 滚轮缩放与调透明度
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            MouseMove += OnMouseMoveDrag;
            MouseLeftButtonUp += OnMouseLeftButtonUpDrag;
            LostMouseCapture += (s, e) => EndDrag();
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
                _selected.Remove(this); // 关闭的贴图同时退出框选选中集合（若在其中的话）
                if (_open.Count == 0)
                    _allHidden = false; // 贴图全部关闭后整体隐藏状态自然结束
                Logger.LogInfo($"贴图已关闭：{contentDesc}，剩余 {_open.Count} 个");
                PinStore.ScheduleSave(); // 关闭后集合已不含它，保存最新列表
            };
            Logger.LogInfo($"贴图已创建：{contentDesc}，初始内容左上角 ({_initialPhysicalTopLeft.X}, {_initialPhysicalTopLeft.Y})（物理像素），当前共 {_open.Count} 个");
            PinStore.ScheduleSave(); // 新建即保存（恢复重启前关闭的贴图丢失的防御）
        }

        // 初始显示：整体隐藏状态下新钉贴图时，先恢复全部隐藏贴图——本窗此刻已在 _open，
        // ShowAll 遍历列表把它一并显示（新贴图直接显示且旧贴图一起恢复）。
        private void ShowInitially()
        {
            if (_allHidden)
                ShowAll();
            else
                Show();
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
                Width = _baseWidthDip * _zoom + ShadowMarginDip * 2; // 窗口 = 内容 + 两侧阴影边距
                Height = _baseHeightDip * _zoom + ShadowMarginDip * 2;
                // _initialPhysicalTopLeft 是内容左上角物理像素，窗口左上角再减边距
                Left = _initialPhysicalTopLeft.X / _dpiScaleX - ShadowMarginDip;
                Top = _initialPhysicalTopLeft.Y / _dpiScaleY - ShadowMarginDip;
                ClampOutsideToNearestScreen();
            }
            else
            {
                // 文本尺寸为 DIP 语义（DPI 无关），但位置需按物理像素重换算、高度钳制依赖 scale 需重算
                Left = _initialPhysicalTopLeft.X / _dpiScaleX - ShadowMarginDip;
                Top = _initialPhysicalTopLeft.Y / _dpiScaleY - ShadowMarginDip;
                ApplyTextSize();
                ClampOutsideToNearestScreen();
            }
        }

        // 内容完全落在虚拟屏外时，拉回到最近屏幕的工作区内（坐标先换算成物理像素判定）。
        // 判定与钳制对象是内容矩形（用户关心内容可见，不关心透明阴影边距）——窗口矩形四周
        // 各缩进 ShadowMarginDip 换算，拉回后窗口位置 = 内容目标位置 − 边距（见常量注释 ③）
        private void ClampOutsideToNearestScreen()
        {
            double physL = (Left + ShadowMarginDip) * _dpiScaleX;
            double physT = (Top + ShadowMarginDip) * _dpiScaleY;
            double physW = (Width - ShadowMarginDip * 2) * _dpiScaleX;   // DIP → 物理用乘法
            double physH = (Height - ShadowMarginDip * 2) * _dpiScaleY;

            foreach (var s in System.Windows.Forms.Screen.AllScreens)
            {
                var b = s.Bounds;
                if (physL < b.Right && physL + physW > b.Left && physT < b.Bottom && physT + physH > b.Top)
                    return; // 与任一屏幕有交叠即可见，不处理
            }

            var wa = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)physL, (int)physT)).WorkingArea;
            double newL = physW >= wa.Width ? wa.Left : Math.Clamp(physL, wa.Left, wa.Right - physW);
            double newT = physH >= wa.Height ? wa.Top : Math.Clamp(physT, wa.Top, wa.Bottom - physH);
            Left = newL / _dpiScaleX - ShadowMarginDip; // 物理 → DIP 用除法；内容位置 − 边距 = 窗口位置
            Top = newT / _dpiScaleY - ShadowMarginDip;
        }

        /// <summary>
        /// 按内容重算并应用文本贴图的窗口尺寸（宽高都自适应）。
        /// 宽：内容自然宽度，钳在 [MinTextWidthDip, TextWidthDip] 且不超所在屏工作区宽；
        /// 高：折行后内容高度，钳在 [MinTextHeightDip, 工作区高 60%]，超出由 ScrollViewer 滚动。
        /// 构造时、Loaded DPI 校正后、编辑中每次输入、编辑落定/取消后各调一次
        /// （构造时 GetDpi 可能取到非目标显示器，一并重算）。
        /// 测量固定走永不入树的 _measureBlock，故不受「_textBlock 此刻在不在可视树上」影响。
        /// 最终宽高经 SnapUpToPixel 向上取整到整数物理像素，见该函数注释。
        /// </summary>
        /// <param name="editingText">编辑态传 TextBox 的当前文本；null 表示按已落定的 _text 测量</param>
        private void ApplyTextSize(string? editingText = null)
        {
            // 编辑态（editingText != null）用锚定上限：上限 = 到工作区右/下边缘的剩余空间。这一处
            // 必须跟 EnterEditMode 用同一口径——编辑中每敲一字都会走到这里（OnEditBoxTextChanged），
            // 若这里仍按整块工作区算，会把刚锚定好的窗口重新撑回溢出尺寸（尤其下面那行高度下限）。
            // 非编辑态（落定/取消/新钉/恢复）仍用整块工作区上限，见 GetMaxContentSize 注释。
            var (maxContentW, maxContentH) = GetMaxContentSize(anchored: editingText != null);

            double chromeW = BorderDip + TextPaddingDip * 2; // 两侧描边 + 内边距，内容区之外的固定开销
            double chromeH = chromeW;

            // 编辑态测量宽度留出插入符余量：TextBox 比 TextBlock 多占一点宽，宁可算宽/算高也不裁内容
            double measureW = editingText == null ? maxContentW : Math.Max(maxContentW - CaretSlackDip, 1);
            _measureBlock!.Text = editingText ?? _text;
            _measureBlock.Measure(new Size(measureW, double.PositiveInfinity));

            var content = ScreenshotGeometry.FitTextPinContent(
                _measureBlock.DesiredSize,
                MinTextWidthDip, maxContentW,
                MinTextHeightDip, maxContentH,
                ScrollBarWidthDip,
                out bool needsScroll);

            double contentW = content.Width;
            if (editingText != null)
                // 测量时扣掉的插入符余量必须加回来，否则 TextBox 拿到的宽度只够 TextBlock 用，
                // 会比测量结果提前一个词折行、凭空多出一行
                contentW = Math.Min(contentW + CaretSlackDip, maxContentW);

            // 完整窗口宽 = 内容 + chrome + 两侧阴影边距（_editMinWidthDip 同为完整窗口宽，量纲一致）
            double width = contentW + chromeW + ShadowMarginDip * 2;
            if (editingText != null)
                width = Math.Max(width, _editMinWidthDip); // 编辑中宽度只增不减，避免退格删字时窗口左右跳、光标跟着抖

            // 整体（含边距）做 SnapUpToPixel：窗口总尺寸最终经 SetWindowPos 落成整数物理像素，
            // 小数被抹掉会连带内容区短一截、ScrollViewer 误判装不下弹出多余滚动条（历史坑，见
            // SnapUpToPixel 注释）——只把内容部分 Snap 而边距留小数的话，窗口整体抹小数同样会复发
            Width = SnapUpToPixel(width, _dpiScaleX);
            // 编辑中窗口保持最大尺寸（进入编辑时已放大到 maxContentH + chromeH + 边距），内容超高由
            // ScrollViewer 滚动；落定后（editingText == null）按内容缩回自适应高度
            double height = SnapUpToPixel(content.Height + chromeH + ShadowMarginDip * 2, _dpiScaleY);
            if (editingText != null)
                height = Math.Max(height, SnapUpToPixel(maxContentH + chromeH + ShadowMarginDip * 2, _dpiScaleY));
            Height = height;

            // 滚动条只在真的钳过高度时才出现，不交给 ScrollViewer 自己按浮点比较去猜；
            // Hidden 仍保留滚轮/键盘滚动能力。编辑态两层（外层 ScrollViewer 与 TextBox 自身）都要管。
            var bar = needsScroll ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden;
            _textScroll!.VerticalScrollBarVisibility = bar;
            _editBox!.VerticalScrollBarVisibility = bar;
        }

        /// <summary>
        /// 取所在屏工作区并算出内容区尺寸上限（DIP，不含 chrome，也不含阴影边距）。
        /// 与 ApplyTextSize 既有口径一致：winPt 用内容左上角当前位置 × _dpiScale 取物理像素
        /// （贴图可能已被拖到其它屏），maxContentW = min(TextWidthDip, 工作区宽 DIP − 两侧阴影边距)
        /// − chrome，maxContentH = 工作区高 DIP × 60% − 两侧阴影边距 − chrome，
        /// 下限 Math.Max(x, 1)（锚定分支的下限另见下段，是更严的 Min* 地板）。边距必须计入：窗口比
        /// 内容大 2 × ShadowMarginDip，
        /// 不扣的话编辑态放大后的窗口会超出工作区。编辑态放大（EnterEditMode）与自适应测量
        /// （ApplyTextSize）共用。
        /// <para>
        /// anchored=true（编辑态放大）时上限进一步收窄为「内容左上角到工作区右/下边缘的剩余空间」
        /// 并落到 MinTextWidthDip/MinTextHeightDip 地板（几何抽到
        /// <see cref="ScreenshotGeometry.AnchorPinMaxContentSize"/> 便于单测）——编辑态锚定左上角
        /// 不动，装不下就以到边尺寸为准、内容超出交给 ScrollViewer 滚动。
        /// </para>
        /// <para>
        /// 锚定语义**只给编辑态**，非编辑态（新钉/持久化恢复/编辑落定后缩回）刻意保持原样：新便签
        /// 的初始位置就是鼠标光标处（ScreenshotManager.PinFromClipboard），用户常钉在屏幕右/下缘，
        /// 若非编辑态也按到边限尺寸，刚钉出来的便签会被压到最小尺寸地板，重启恢复贴边便签同样会
        /// 变小——都是用户可见回归。非编辑态本就允许内容右/下边缘溢出工作区（480 宽便签钉在右缘
        /// 即溢出，ClampOutsideToNearestScreen 只兜「完全出屏」），不是本次要改的不变式。
        /// </para>
        /// </summary>
        /// <param name="anchored">true = 编辑态放大，上限收窄为到工作区右/下边缘的剩余空间。
        /// 刻意不给默认值：两态语义差别正是本方法的要点，新调用点必须显式表态选哪一种</param>
        private (double MaxContentW, double MaxContentH) GetMaxContentSize(bool anchored)
        {
            // 用内容左上角当前位置所在屏（贴图可能已被拖到其它屏），物理像素坐标
            var winPt = new System.Drawing.Point((int)((Left + ShadowMarginDip) * _dpiScaleX), (int)((Top + ShadowMarginDip) * _dpiScaleY));
            var wa = System.Windows.Forms.Screen.FromPoint(winPt).WorkingArea;

            double chromeW = BorderDip + TextPaddingDip * 2; // 两侧描边 + 内边距，内容区之外的固定开销
            double chromeH = chromeW;
            double maxContentW = Math.Min(TextWidthDip, wa.Width / _dpiScaleX - ShadowMarginDip * 2) - chromeW;  // 物理 → DIP 用除法
            double maxContentH = wa.Height / _dpiScaleY * TextMaxHeightRatio - ShadowMarginDip * 2 - chromeH;
            // 地板放在分叉之前、两个分支共用：锚定分支只对「到边剩余空间」取 Min* 地板，基础上限本身
            // 若为负（工作区比 chrome + 两侧边距还小，现实中不会发生）会被原样返回成负宽高
            maxContentW = Math.Max(maxContentW, 1);
            maxContentH = Math.Max(maxContentH, 1);

            // 锚定分支自带 Min* 地板（比 1 更严），返回值无需再兜底
            if (anchored)
                return ScreenshotGeometry.AnchorPinMaxContentSize(
                    (Left + ShadowMarginDip) * _dpiScaleX, (Top + ShadowMarginDip) * _dpiScaleY,
                    _dpiScaleX, _dpiScaleY,
                    wa.Right, wa.Bottom,
                    maxContentW, maxContentH,
                    chromeW, chromeH,
                    MinTextWidthDip, MinTextHeightDip);

            return (maxContentW, maxContentH);
        }

        /// <summary>
        /// DIP 尺寸向上取整到整数物理像素。
        /// 窗口尺寸最终要经 SetWindowPos 落成整数设备像素，小数部分会被抹掉——内容区随之短那么
        /// 零点几像素，恰好贴合内容的布局就变成了「装不下」，ScrollViewer 于是弹出多余的滚动条。
        /// 宽度早先没暴露这个问题，只是因为它在 FitTextPinContent 里已经 Math.Ceiling 过。
        /// </summary>
        private static double SnapUpToPixel(double dip, double scale)
            => scale > 0 ? Math.Ceiling(dip * scale) / scale : Math.Ceiling(dip);

        // 左键按下：双击关闭，单击开始拖动。三处刻意的反直觉做法，改动前请先读完：
        //
        // ① 双击判定**不能用 e.ClickCount**。贴图是 ShowActivated=false 弹出的，刚钉出来时是
        //    非活动窗口，用户第一次点它属于「激活点击」——WPF 输入层对 provider 处于 !_active
        //    时收到的输入有一整套补偿逻辑（HwndMouseInputProvider 补报 Activate 并同步按键状态），
        //    这一次物理按下会被 MouseDevice 计成两次 press，e.ClickCount 直接变成 2、命中关闭
        //    分支——表现为「刚钉出来的贴图点一下就没了」。而同时开着两个贴图时，先关掉的那个把
        //    激活权交给了兄弟窗口，剩下那个已是活动窗口、点击计数正常，于是「两个里总有一个
        //    关不掉」。改为自己配对判定：只有「按下 → 释放 → 再按下」这样一个完整循环、且间隔
        //    落在人手做得到的区间内，才算双击（实测幽灵按下的间隔是 0~16ms）。
        // ② 拖动刻意不用 Window.DragMove()：它是阻塞的 Win32 模态移动循环
        //    （WM_SYSCOMMAND/SC_MOUSEMOVE），会把真正的 WM_LBUTTONUP 吃在循环里，WPF 的
        //    MouseDevice 从未看到这次释放，同样会污染点击计数。改为鼠标捕获 + 手动移位后，
        //    WPF 能收到完整的 down → move → up。
        // ③ **按下时不 CaptureMouse**，推迟到真正越过拖动阈值时（见 OnMouseMoveDrag）：
        //    `IMouseInputProvider.CaptureMouse()` 里正有一段 `!_active` 的补偿代码，在窗口尚未
        //    激活时调用它就是上面那个幽灵按下的触发点。等真正开始拖动时窗口早已激活，不会触发。
        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_mode == ContentMode.Text && _isEditing)
                return; // 编辑中：鼠标交给 TextBox（光标/选区），点击外部经失焦落定，窗口不拖动、双击不关闭

            long now = Environment.TickCount64;
            var cur = System.Windows.Forms.Cursor.Position; // 物理像素（PerMonitorV2）

            if (_dragPending)
            {
                // 上一次按下还没等到释放就又来一次。紧随其后（幽灵按下实测 0~16ms）的必是激活补偿
                // 灌进来的合成按下，丢弃；隔了很久才来则说明上一次的 MouseUp 落到了窗口外、
                // 状态陈旧（按下时不捕获鼠标，见注释 ③），就地复位继续处理，避免卡成永远关不掉。
                if (now - _lastPressTicks <= MinHumanDoubleClickMs)
                {
                    Logger.LogInfo("贴图忽略未成对的重复按下（窗口激活时的合成点击）");
                    return;
                }
                Logger.LogInfo("贴图检测到陈旧的未完成按下状态，已复位");
                _dragPending = false;
                _dragging = false;
                _sawReleaseSincePress = true;
            }

            var slop = System.Windows.Forms.SystemInformation.DoubleClickSize;
            long elapsed = now - _lastPressTicks;
            bool isDoubleClick =
                _sawReleaseSincePress &&
                e.Timestamp != _lastPressTimestamp &&          // 同一消息时间戳 = 同一次输入被重报
                elapsed >= MinHumanDoubleClickMs &&            // 快过人手极限的必是合成输入
                elapsed <= System.Windows.Forms.SystemInformation.DoubleClickTime &&
                Math.Abs(cur.X - _lastPressCursor.X) <= slop.Width &&
                Math.Abs(cur.Y - _lastPressCursor.Y) <= slop.Height;

            _lastPressTicks = now;
            _lastPressTimestamp = e.Timestamp;
            _lastPressCursor = cur;
            _sawReleaseSincePress = false;

            if (isDoubleClick)
            {
                Logger.LogInfo($"贴图双击关闭（两次按下间隔 {elapsed}ms）");
                Close();
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            _dragScaleX = dpi.DpiScaleX;
            _dragScaleY = dpi.DpiScaleY;
            _dragStartCursor = cur;
            _dragStartLeft = Left;
            _dragStartTop = Top;

            // 框选整体移动：拖动任一选中贴图前，把全体选中成员的快照刷新为「此刻」的位置/DPI。
            // 快照若只在框选落定时拍一次，第二次拖动会用旧值平移成员、整体队形错乱（成员
            // 收不到本次 MouseDown，必须由按下贴图代为刷新——广播公式见 OnMouseMoveDrag）
            if (_selected.Contains(this))
            {
                foreach (var other in _selected)
                {
                    if (ReferenceEquals(other, this))
                        continue;
                    other._dragStartLeft = other.Left;
                    other._dragStartTop = other.Top;
                    other._dragScaleX = other._dpiScaleX;
                    other._dragScaleY = other._dpiScaleY;
                }
            }

            _dragPending = true; // 见 ③：此处刻意不 CaptureMouse
            _dragging = false;
        }

        // 拖动中：越过系统拖动阈值才真正移动，避免双击时的手抖把窗口挪走。
        // 用「按下时的光标/窗口位置 + 当前光标」绝对定位，不累积误差；DPI 用按下时的快照，
        // 跨不同 DPI 显示器拖动时也不会中途跳变。
        private void OnMouseMoveDrag(object sender, MouseEventArgs e)
        {
            // 兜底（不依赖 _dragPending）：捕获丢失后真正的 MouseUp 可能落在别的窗口上，
            // 靠这里恢复「已见过释放」，否则双击判定会卡死成永远关不掉
            if (!_sawReleaseSincePress && e.LeftButton != MouseButtonState.Pressed)
                _sawReleaseSincePress = true;

            if (!_dragPending)
                return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                EndDrag();
                return;
            }

            var cur = System.Windows.Forms.Cursor.Position;
            double dx = cur.X - _dragStartCursor.X;
            double dy = cur.Y - _dragStartCursor.Y;
            if (!_dragging)
            {
                // 阈值是 DIP 语义的系统参数，与物理位移比较前换算成物理像素
                if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * _dragScaleX &&
                    Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * _dragScaleY)
                    return;
                _dragging = true;
                // 到这里窗口早已激活，CaptureMouse 不会再踩 WPF 的 !_active 补偿块
                // （见 OnMouseLeftButtonDown 注释 ③）；失败也继续，拖动时光标基本还在窗口内
                CaptureMouse();
            }

            Left = _dragStartLeft + dx / _dragScaleX; // 物理 → DIP 用除法
            Top = _dragStartTop + dy / _dragScaleY;

            // 框选整体移动：拖动任一选中贴图时，其余选中成员同步移动。成员用「框选落定时」的快照
            // （它们收不到本次 MouseDown），只设 Left/Top，不碰其拖动状态机（幽灵按下/双击判定不受影响）
            if (_selected.Contains(this))
            {
                foreach (var other in _selected)
                {
                    if (ReferenceEquals(other, this) || other._isEditing)
                        continue;
                    other.Left = other._dragStartLeft + dx / other._dragScaleX;
                    other.Top = other._dragStartTop + dy / other._dragScaleY;
                }
            }
        }

        // 真正的左键释放 —— 唯一可信的「用户松手」信号，无条件标记（不看 _dragPending）。
        // 单击（按下且未越过拖动阈值、非双击）→ 单选被点击的贴图（任何时刻点击即选中，不要求先框选）；
        // 拖动整体移动时 _dragging 为 true、wasClick=false，不受影响；双击关闭已在
        // OnMouseLeftButtonDown 提前 return，此处 _dragPending 为 false。
        // 已知副作用（已接受）：双击关闭贴图时，首击会先单选、次击才关闭，净效果是其余成员失去选中——
        // 属「单击单选 + 双击关闭」组合语义的自然结果（首击无法预知次击会来）。
        private void OnMouseLeftButtonUpDrag(object sender, MouseButtonEventArgs e)
        {
            _sawReleaseSincePress = true;
            bool wasClick = _dragPending && !_dragging;
            EndDrag();
            if (wasClick)
                SelectOnly();
        }

        // 结束拖动并归还捕获。**刻意不在这里置 _sawReleaseSincePress**：本方法也挂在
        // LostMouseCapture 上，而丢失捕获不等于用户松手——第二轮修复正是栽在这里
        // （激活流程夺走捕获 → 误标「已释放」→ 紧随其后的幽灵按下被判成双击 → 单击关窗）。
        // 顺带刷新 DPI 缩放：贴图可能被拖到另一 DPI 的显示器上，
        // 后续 ApplyTextSize / ClampOutsideToNearestScreen 的物理 ↔ DIP 换算要用新值。
        private void EndDrag()
        {
            if (!_dragPending)
                return;
            _dragPending = false;
            _dragging = false;
            if (IsMouseCaptured)
                ReleaseMouseCapture();

            RefreshDpiSnapshot();

            // 整体移动结束：跨 DPI 屏拖动后刷新各成员 DPI 快照，并钳制完全出屏的成员（不钳制跟手拖动中的成员）
            if (_selected.Contains(this))
            {
                foreach (var other in _selected)
                {
                    if (ReferenceEquals(other, this))
                        continue;
                    other.RefreshDpiSnapshot();
                    other.ClampOutsideToNearestScreen();
                }
            }

            PinStore.ScheduleSave(); // 拖动只在结束时存一次（拖动中不反复落盘）；排在成员钳制之后，钳制后的位置也一并落盘
        }

        // 刷新 DPI 缩放快照：贴图可能被拖到另一 DPI 的显示器上，
        // 后续 ApplyTextSize / ClampOutsideToNearestScreen 的物理 ↔ DIP 换算要用新值
        private void RefreshDpiSnapshot()
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 编辑态下 Esc 已被 TextBox 的 PreviewKeyDown 拦截（标 Handled 不会到达此处），此判断为防御
            if (e.Key == Key.Escape && !_isEditing)
            {
                // 有框选选中时 Esc 只取消选中不再关闭贴图（窗口级 KeyDown 只在活动窗口收按键，
                // 非活动贴图按 Esc 落不到本进程，与既有「Esc 关闭」同一边界）
                if (_selected.Count > 0)
                {
                    ClearSelection();
                    Logger.LogInfo("已取消框选选中");
                }
                else
                {
                    Close();
                }
            }
            // F2 进入编辑（仅文本模式、非编辑态）。窗口级 KeyDown 只在窗口是活动窗口时收到按键，
            // 与「F2 只要在窗口是活动窗口时响应即可」的语义一致；非活动窗口的贴图按 F2 落不到本进程。
            else if (e.Key == Key.F2 && !_isEditing)
            {
                EnterEditMode();
                e.Handled = true;
            }
        }

        // 进入编辑态：TextBox 替换 TextBlock 显示，描边变蓝提示编辑中，
        // 窗口 ContextMenu 置空让位给 TextBox 默认的剪切/复制/粘贴菜单
        private void EnterEditMode()
        {
            if (_mode != ContentMode.Text || _isEditing)
                return;
            _isEditing = true;

            // 进入编辑即放大，但**左上角固定不动**：上限 = min(内容上限, 内容左上角到工作区右/下
            // 边缘的剩余空间)（GetMaxContentSize(anchored) 已把边距从上限里扣掉，这里加回才是完整
            // 窗口尺寸）。刻意不再把窗口钳回工作区——钳制会写回 Left/Top，靠近屏幕右/下缘的便签
            // 一进编辑就被挪走，用户看到的是「位置变了」而不是「变大了」。剩余空间装不下时窗口就
            // 停在到边尺寸（下限是 MinTextWidthDip/MinTextHeightDip），内容超出由 ScrollViewer 滚动。
            // 退出编辑时 ApplyTextSize 按内容缩回，位置全程不动。
            var (maxContentW, maxContentH) = GetMaxContentSize(anchored: true);
            Width = SnapUpToPixel(maxContentW + BorderDip + TextPaddingDip * 2 + ShadowMarginDip * 2, _dpiScaleX);
            Height = SnapUpToPixel(maxContentH + BorderDip + TextPaddingDip * 2 + ShadowMarginDip * 2, _dpiScaleY);

            _editMinWidthDip = Width; // 编辑中宽度只增不减的下限（取到的就是放大后的最大宽，含阴影边距，与 ApplyTextSize 的 width 同量纲）
            _editBox!.Text = _text;
            _textScroll!.Content = _editBox;
            _border.BorderBrush = HoverBorderBrush;
            ContextMenu = null;

            // 贴图是 ShowActivated=false 弹出的非活动窗口，Keyboard.Focus 在非活动窗口上不会向
            // Win32 要键盘焦点（只记为「激活后待聚焦」），表现为 TextBox 收不到任何按键（方向键、
            // 打字全落到原前台窗口）。先借 ForegroundActivator 把窗口切到前台再聚焦——与命令
            // 启动器/剪贴板历史同一套 AttachThreadInput 绕前台锁定的激活路径。
            // previousForeground 传此刻的前台窗口即可（编辑是用户主动触发，此刻前台就是要借
            // 输入队列的那个窗口）。
            var hwnd = new WindowInteropHelper(this).Handle;
            ForegroundActivator.ForceForeground(hwnd, ForegroundActivator.GetForeground(), "文本贴图");

            // 聚焦推迟到 Dispatcher 下一拍：SetForegroundWindow 成功后，WPF 感知窗口激活
            // 依赖 WM_ACTIVATE 消息异步回流，同一拍里 Keyboard.Focus 仍按「窗口未激活」走记账
            // 路径，TextBox 拿不到真实键盘焦点（全选状态下按键无响应、必须鼠标点一下才出光标）。
            // 等 WM_ACTIVATE 回流后再聚焦，Keyboard.Focus 才会真正向 Win32 要键盘焦点。
            // SelectAll 也一并推迟——它依赖焦点已落定（否则全选状态会被聚焦过程冲掉）。
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_isEditing) // 下一拍到来前用户可能已 Esc/失焦退出编辑
                    return;
                _editBox.Focus();
                Keyboard.Focus(_editBox);
                _editBox.SelectAll();
            }), DispatcherPriority.Input);
        }

        // 退出编辑态并落定：cancel=true 丢弃修改恢复原文本；否则保存新文本。
        // 两种情况都要重测窗口尺寸——编辑期间窗口已按 TextBox 内容伸缩过，取消时同样得还原成原文对应的尺寸。
        // 先置 _isEditing=false 再切换 Content，避免 Content 切换引发的 LostKeyboardFocus 重入
        private void ExitEditMode(bool cancel)
        {
            if (!_isEditing)
                return;
            _isEditing = false;
            _editMinWidthDip = 0; // 解除「只增不减」下限，落定后允许按内容缩窄
            if (!cancel)
            {
                _text = _editBox!.Text;
                _textBlock!.Text = _text;
                Logger.LogInfo($"文本贴图编辑已保存：{_text.Length} 字符");
                PinStore.ScheduleSave(); // 文本内容变化，保存最新文本
            }
            ApplyTextSize();
            _textScroll!.Content = _textBlock;
            // 描边按选中态恢复：被选中的便签落定后仍显示选中视觉（分类色描边 + 厚 2），
            // 否则回常态（分类描边 + 厚 1）——两者现在同为分类色、仅厚度区分，直接设常态会丢掉选中厚度
            if (_selected.Contains(this))
                ApplySelectedVisual();
            else
                ApplyDefaultBorder();
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

        // 编辑中每次输入都按当前内容重算窗口尺寸（宽只增不减、高实时跟随，到上限后由 ScrollViewer 滚动）
        private void OnEditBoxTextChanged(object sender, TextChangedEventArgs e)
        {
            if (_isEditing)
                ApplyTextSize(_editBox!.Text);
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
                PinStore.ScheduleSave(); // 透明度经防抖合并（连续滚轮只落盘一次）
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

        // 应用缩放：窗口尺寸 = 基准 DIP × zoom + 两侧阴影边距；缩放后光标下的图像点保持不动——
        // 光标在图像中的相对位置 fx=p/oldImgW 不动 ⇒ Left' = Left + p − fx×newImgW（两侧 1 DIP 描边在等式两边抵消）。
        private void ApplyZoom(double newZoom, System.Windows.Point anchorInImage)
        {
            newZoom = Math.Clamp(newZoom, MinZoom, MaxZoom);

            // 图像可视区（DIP）= 窗口 − 两侧阴影边距 − 两侧描边；与 newImgW 同一约定以保证锚点数学自洽
            double oldImgW = Width - ShadowMarginDip * 2 - BorderDip;
            double oldImgH = Height - ShadowMarginDip * 2 - BorderDip;
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
            Width = newW + ShadowMarginDip * 2; // 窗口 = 图像基准 DIP + 两侧阴影边距
            Height = newH + ShadowMarginDip * 2;
            ShowBadge($"{Percent(_zoom)}%");
            PinStore.ScheduleSave(); // 缩放经防抖合并（连续滚轮只落盘一次）
        }

        // 「缩放 100%」：恢复 zoom=1 与基准尺寸（位置不动），仅图片模式
        private void ResetZoom()
        {
            _zoom = 1.0;
            Width = _baseWidthDip + ShadowMarginDip * 2; // 窗口 = 图像基准 DIP + 两侧阴影边距
            Height = _baseHeightDip + ShadowMarginDip * 2;
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

                // 分类子菜单：8 个固定预设，当前分类打勾。菜单实例跨多次打开复用
                // （构造建一次、ExitEditMode 重建一次），Opened 时按 _category 刷新勾选防陈旧
                var catMenu = new MenuItem { Header = "分类" };
                foreach (var c in NoteCategories)
                {
                    var name = c.Name; // 显式捕获循环变量（foreach 闭包共享同一变量的经典陷阱）
                    var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = _category == name };
                    item.Click += (s, e) => SetCategory(name);
                    catMenu.Items.Add(item);
                }
                menu.Items.Add(catMenu);
                menu.Opened += (s, e) =>
                {
                    foreach (MenuItem it in catMenu.Items)
                        it.IsChecked = it.Header is string h && h == _category;
                };
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

        // 设置便签分类：更新当前分类、会话记忆与描边颜色（仅文本模式「分类」子菜单调用）
        private void SetCategory(string name)
        {
            _category = name;
            _lastCategory = name; // 同步会话记忆，F7 连续钉同类便签免重选
            _border.BorderBrush = CategoryBrush(name);
            PinStore.ScheduleSave(); // 分类变化，保存最新分类
            Logger.LogInfo($"文本贴图分类已设为：{name}");
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

        // 复制文本回剪贴板：框选选中多个文本便签时，改为复制所有选中便签的文本（按屏幕位置排序、
        // 换行连接），而非仅复制当前这一个——这是「框选 → 批量复制文本」的入口；否则维持单便签行为。
        private void CopyTextToClipboard()
        {
            if (_selected.Contains(this) && _selected.Count(p => !p.IsImagePin) >= 2)
            {
                CopySelectedTextsToClipboard();
                return;
            }
            SetTextWithRetry(_text, "贴图文本复制到剪贴板失败（重试 3 次仍被占用）");
        }

        // 复制所有选中文本便签的文本：按屏幕位置（从上到下、从左到右）排序，每个便签文本用换行符连接。
        // 排序用 PhysicalBounds（内容矩形·虚拟屏物理像素，与框选命中测试同坐标系），保证「框选选中哪些」
        // 和「按什么顺序复制」使用同一套位置语义；编辑中的便签不参与（与 EnumerateSelectablePins 同口径）。
        private static void CopySelectedTextsToClipboard()
        {
            var texts = _selected
                .Where(p => !p.IsImagePin && !p._isEditing)
                .OrderBy(p => p.PhysicalBounds.Y)
                .ThenBy(p => p.PhysicalBounds.X)
                .Select(p => p.PinText)
                .ToList();
            if (texts.Count == 0)
            {
                Logger.LogInfo("选中集合不含文本便签，跳过批量复制");
                return;
            }
            SetTextWithRetry(string.Join("\n", texts), "贴图选中文本复制到剪贴板失败（重试 3 次仍被占用）");
            Logger.LogInfo($"已复制 {texts.Count} 个文本便签的文本到剪贴板");
        }

        // 写文本到剪贴板：ExternalException 重试 3 次、每次间隔 50ms（同 CopyImageToClipboard 口径）
        private static void SetTextWithRetry(string text, string failMessage)
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
            Logger.LogWarning(failMessage);
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
