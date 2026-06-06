using System;
using System.ComponentModel;
using System.Windows.Media;

namespace CommandLauncher
{
    /// <summary>
    /// Alt+Tab 切换器中的单个窗口条目模型。
    /// </summary>
    public class WindowInfo : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public IntPtr Hwnd { get; set; }
        public string Title { get; set; } = "";
        public ImageSource? Icon { get; set; }
        public string ProcessName { get; set; } = "";

        private bool _hasNotification;
        /// <summary>窗口正在通过 FlashWindowEx 请求注意时为 true（任务栏按钮闪烁）。</summary>
        public bool HasNotification
        {
            get => _hasNotification;
            set
            {
                if (_hasNotification != value)
                {
                    _hasNotification = value;
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNotification)));
                }
            }
        }
    }
}
