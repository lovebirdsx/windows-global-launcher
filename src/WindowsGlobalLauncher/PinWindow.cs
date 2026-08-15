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
    /// <summary>贴图浮窗：把一张图片钉在屏幕最顶层，可拖动、缩放、调透明度。</summary>
    /// <remarks>
    /// DPI 语义（与项目 PerMonitorV2 约定一致）：构造传入的 physicalTopLeft、以及
    /// System.Windows.Forms.Screen 返回/接收的坐标均为「物理像素」；WPF 的 Left/Top/Width/Height
    /// 为「DIP」。换算方向：物理 = DIP × DpiScale，故物理 → DIP 一律用除法。
    /// 图片初始按 1:1 物理像素显示：基准 DIP 尺寸 = 像素尺寸 ÷ DpiScale，且必须用
    /// PixelWidth/PixelHeight（剪贴板图片的 DPI 元数据会让 BitmapSource.Width/Height 不可靠）。
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
        private const double BorderDip = 2.0; // 双侧各 1 DIP 描边，计算图像可视区时扣除

        private readonly BitmapSource _source;
        private readonly Image _image;
        private readonly Border _border;
        private readonly Border _hint;      // 左上角的缩放/透明度提示角标（不响应命中测试）
        private readonly TextBlock _hintText;
        private readonly DispatcherTimer _hintTimer;

        private readonly System.Drawing.Point _initialPhysicalTopLeft; // 初始位置（物理像素），Loaded 校正 DPI 时用
        private double _dpiScaleX = 1.0;
        private double _dpiScaleY = 1.0;
        private double _baseWidthDip;  // 基准 DIP 窗口尺寸（zoom=1 时，含描边）
        private double _baseHeightDip;
        private double _zoom = 1.0;

        /// <param name="image">要钉的图片（应已 Freeze）</param>
        /// <param name="physicalTopLeft">初始位置（虚拟屏物理像素坐标），图片以 1:1 物理像素显示</param>
        public PinWindow(BitmapSource image, System.Drawing.Point physicalTopLeft)
        {
            _source = image;
            _initialPhysicalTopLeft = physicalTopLeft;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent; // 让整窗可命中测试，视觉背景由内容承载
            Topmost = true;
            ShowInTaskbar = false;
            ShowActivated = false; // 弹出不抢焦点；用户点击后自然获得焦点以接收 Esc
            ResizeMode = ResizeMode.NoResize;

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
            var root = new Grid();
            root.Children.Add(_border);
            root.Children.Add(_hint);
            Content = root;

            ContextMenu = BuildContextMenu();

            // 输入：左键拖动 / 双击关闭 / Esc 关闭 / 滚轮缩放与调透明度
            MouseLeftButtonDown += OnMouseLeftButtonDown;
            KeyDown += OnKeyDown;

            _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HintHideMs) };
            _hintTimer.Tick += (s, e) =>
            {
                _hint.Visibility = Visibility.Collapsed;
                _hintTimer.Stop();
            };

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

            _open.Add(this);
            Closed += (s, e) =>
            {
                _open.Remove(this);
                Logger.LogInfo($"贴图已关闭：{_source.PixelWidth}x{_source.PixelHeight} 像素，剩余 {_open.Count} 个");
            };
            Logger.LogInfo($"贴图已创建：{image.PixelWidth}x{image.PixelHeight} 像素，初始位置 ({physicalTopLeft.X}, {physicalTopLeft.Y})（物理像素），当前共 {_open.Count} 个");

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

        // Loaded 后按最终所在显示器的 DPI 校正一次：若与构造时不同，重算基准尺寸与初始位置
        private void OnLoadedRecheckDpi(object sender, RoutedEventArgs e)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            if (Math.Abs(dpi.DpiScaleX - _dpiScaleX) < 1e-3 && Math.Abs(dpi.DpiScaleY - _dpiScaleY) < 1e-3)
                return;

            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            _baseWidthDip = _source.PixelWidth / _dpiScaleX;
            _baseHeightDip = _source.PixelHeight / _dpiScaleY;
            Width = _baseWidthDip * _zoom;
            Height = _baseHeightDip * _zoom;
            Left = _initialPhysicalTopLeft.X / _dpiScaleX;
            Top = _initialPhysicalTopLeft.Y / _dpiScaleY;
            ClampOutsideToNearestScreen();
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

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
            if (e.Key == Key.Escape)
                Close();
        }

        // 滚轮：缩放（锚点为鼠标位置）；Ctrl+滚轮：调透明度
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            base.OnMouseWheel(e);

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? OpacityStep : -OpacityStep), MinOpacity, 1.0);
                ShowBadge($"{Percent(Opacity)}%");
            }
            else
            {
                double factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
                ApplyZoom(_zoom * factor, e.GetPosition(_image));
            }
            e.Handled = true;
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

        // 「缩放 100%」：恢复 zoom=1 与基准尺寸（位置不动）
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
            menu.Items.Add(CreateMenuItem("复制图像", CopyImageToClipboard));
            menu.Items.Add(CreateMenuItem("保存为文件…", SaveToFile));
            menu.Items.Add(CreateMenuItem("缩放 100%", ResetZoom));
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
                    Clipboard.SetImage(_source);
                    return;
                }
                catch (ExternalException)
                {
                    Thread.Sleep(50);
                }
            }
            Logger.LogWarning("贴图复制到剪贴板失败（重试 3 次仍被占用）");
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
                encoder.Frames.Add(BitmapFrame.Create(_source));
                using var fs = new FileStream(dlg.FileName, FileMode.Create, FileAccess.Write);
                encoder.Save(fs);
                Logger.LogInfo($"贴图已保存：{dlg.FileName}");
            }
            catch (Exception ex)
            {
                Logger.LogError("贴图保存失败", ex);
            }
        }
    }
}
