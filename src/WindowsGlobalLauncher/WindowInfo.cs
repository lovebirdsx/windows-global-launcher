using System;
using System.Windows.Media;

namespace CommandLauncher
{
    /// <summary>
    /// Alt+Tab 切换器中的单个窗口条目模型。
    /// </summary>
    public class WindowInfo
    {
        public IntPtr Hwnd { get; set; }
        public string Title { get; set; } = "";
        public ImageSource? Icon { get; set; }
        public string ProcessName { get; set; } = "";
    }
}
