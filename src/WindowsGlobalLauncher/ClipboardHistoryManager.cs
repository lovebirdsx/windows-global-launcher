using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace CommandLauncher
{
    /// <summary>
    /// 剪贴板历史管理器：通过 AddClipboardFormatListener 监听系统剪贴板变化，
    /// 记录文本与图片，按时间倒序保留最近 <see cref="MaxEntries"/> 条，重复内容置顶去重。
    /// 持久化仿 AppState 模式：元数据写 clipboard-history.json，图片写 clipboard-images\ 目录。
    /// 所有公开方法须在 UI 线程调用（剪贴板 API 要求 STA）。
    /// </summary>
    public class ClipboardHistoryManager : IDisposable
    {
        /// <summary>最多保留的历史条数。</summary>
        public const int MaxEntries = 100;

        // 超过该长度的文本不记录（避免复制大文件内容时撑爆历史文件与内存）
        private const int MaxTextLength = 50_000;

        // 超过该大小的图片（PNG 编码后）不记录
        private const long MaxImageBytes = 5 * 1024 * 1024;

        private const int WM_CLIPBOARDUPDATE = 0x031D;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool AddClipboardFormatListener(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

        private static string HistoryFilePath => Path.Combine(App.BaseDir, "clipboard-history.json");
        private static string ImagesDir => Path.Combine(App.BaseDir, "clipboard-images");
        private static string ImagePath(string id) => Path.Combine(ImagesDir, id + ".png");

        private static ClipboardHistoryManager? _instance;
        public static ClipboardHistoryManager Instance => _instance ??= new ClipboardHistoryManager();

        private readonly List<ClipboardEntry> _entries = [];
        private ClipboardListenerWindow? _window;
        private bool _disposed;

        /// <summary>历史发生变化（新增/置顶/删除）时触发，供窗口刷新。</summary>
        public event Action? HistoryChanged;

        private ClipboardHistoryManager()
        {
            LoadHistory();
        }

        /// <summary>开始监听系统剪贴板（须在 UI 线程调用，幂等）。</summary>
        public void StartListening()
        {
            if (_window != null)
                return;

            // 消息专用隐藏窗口（同 HotKeyListener 的 NativeWindow 做法），仅用于接收 WM_CLIPBOARDUPDATE
            _window = new ClipboardListenerWindow(this);
            _window.CreateHandle(new System.Windows.Forms.CreateParams());

            if (!AddClipboardFormatListener(_window.Handle))
                Logger.LogError("注册剪贴板监听失败", new InvalidOperationException($"AddClipboardFormatListener 失败，错误码 {Marshal.GetLastWin32Error()}"));
            else
                Logger.LogInfo("剪贴板历史监听已启动");
        }

        private void HandleMessage(ref System.Windows.Forms.Message m)
        {
            if (m.Msg == WM_CLIPBOARDUPDATE)
                CaptureClipboardAsync();
        }

        // 仅转发 WM_CLIPBOARDUPDATE 给管理器
        private class ClipboardListenerWindow : System.Windows.Forms.NativeWindow
        {
            private readonly ClipboardHistoryManager _parent;

            public ClipboardListenerWindow(ClipboardHistoryManager parent)
            {
                _parent = parent;
            }

            protected override void WndProc(ref System.Windows.Forms.Message m)
            {
                _parent.HandleMessage(ref m);
                base.WndProc(ref m);
            }
        }

        // 剪贴板可能被其它程序短暂占用，读取失败时重试几次
        private async void CaptureClipboardAsync()
        {
            const int maxAttempts = 5;
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    CaptureOnce();
                    return;
                }
                catch (Exception ex) when (attempt < maxAttempts && ex is ExternalException)
                {
                    // 剪贴板被其它程序短暂占用（COMException 等），稍后重试
                    await Task.Delay(30);
                }
                catch (Exception ex)
                {
                    Logger.LogError("读取剪贴板内容失败", ex);
                    return;
                }
            }
        }

        private void CaptureOnce()
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (string.IsNullOrWhiteSpace(text))
                    return;
                if (text.Length > MaxTextLength)
                {
                    Logger.LogInfo($"跳过超长文本剪贴内容（{text.Length} 字符）");
                    return;
                }
                AddOrPromote(new ClipboardEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IsImage = false,
                    Text = text,
                    Timestamp = DateTime.Now,
                });
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image == null)
                    return;

                byte[] pngBytes;
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                using (var ms = new MemoryStream())
                {
                    encoder.Save(ms);
                    pngBytes = ms.ToArray();
                }

                if (pngBytes.Length > MaxImageBytes)
                {
                    Logger.LogInfo($"跳过超大图片剪贴内容（{pngBytes.Length / 1024} KB）");
                    return;
                }

                var hash = Convert.ToHexString(SHA1.HashData(pngBytes));
                var entry = new ClipboardEntry
                {
                    Id = Guid.NewGuid().ToString("N"),
                    IsImage = true,
                    ContentHash = hash,
                    ImageWidth = image.PixelWidth,
                    ImageHeight = image.PixelHeight,
                    Timestamp = DateTime.Now,
                };

                // 已存在的相同图片直接置顶，不重复写文件
                if (AddOrPromote(entry))
                {
                    Directory.CreateDirectory(ImagesDir);
                    File.WriteAllBytes(ImagePath(entry.Id), pngBytes);
                }
            }
        }

        /// <summary>
        /// 插入新条目；若内容已存在则置顶并刷新时间（去重）。
        /// 返回 true 表示是全新条目（图片条目据此决定是否需要写 PNG 文件）。
        /// </summary>
        private bool AddOrPromote(ClipboardEntry entry)
        {
            var existing = _entries.FirstOrDefault(e => SameContent(e, entry));
            bool isNew = existing == null;
            if (existing != null)
            {
                _entries.Remove(existing);
                existing.Timestamp = DateTime.Now;
                _entries.Insert(0, existing);
            }
            else
            {
                _entries.Insert(0, entry);
            }

            // 超上限淘汰最旧条目，图片条目连同 PNG 文件一起清理
            while (_entries.Count > MaxEntries)
            {
                var evicted = _entries[^1];
                _entries.RemoveAt(_entries.Count - 1);
                DeleteImageFile(evicted);
            }

            SaveHistory();
            HistoryChanged?.Invoke();
            return isNew;
        }

        private static bool SameContent(ClipboardEntry a, ClipboardEntry b)
            => a.IsImage == b.IsImage && (a.IsImage ? a.ContentHash == b.ContentHash : a.Text == b.Text);

        /// <summary>查询历史（时间倒序）。filter 非空时对文本条目做模糊匹配，按匹配度排序；图片条目不参与搜索。</summary>
        public List<ClipboardEntry> Query(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return [.. _entries];

            filter = filter.Trim();
            var scored = new List<(ClipboardEntry Entry, double Score)>();
            foreach (var e in _entries)
            {
                if (e.IsImage)
                    continue;
                // 超长文本只取开头一段参与匹配，避免每次按键都全量扫描
                var target = e.Text.Length > 1000 ? e.Text[..1000] : e.Text;
                var score = FuzzyMatcher.GetMatchScore(filter, target);
                if (score > 0)
                    scored.Add((e, score));
            }
            return scored
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Entry.Timestamp)
                .Select(x => x.Entry)
                .ToList();
        }

        /// <summary>删除一条历史（图片条目连同文件删除）。</summary>
        public void Delete(ClipboardEntry entry)
        {
            if (!_entries.Remove(entry))
                return;
            DeleteImageFile(entry);
            SaveHistory();
            HistoryChanged?.Invoke();
        }

        /// <summary>把条目内容写回系统剪贴板（供粘贴前调用）。</summary>
        public void SetToClipboard(ClipboardEntry entry)
        {
            if (entry.IsImage)
            {
                var bmp = LoadBitmap(entry, decodePixelWidth: 0);
                if (bmp == null)
                    throw new FileNotFoundException("图片文件已不存在", ImagePath(entry.Id));
                Clipboard.SetImage(bmp);
            }
            else
            {
                Clipboard.SetText(entry.Text);
            }
        }

        /// <summary>按需加载图片缩略图（缓存在条目上，跨刷新生效）。</summary>
        public void EnsureThumbnail(ClipboardEntry entry)
        {
            if (!entry.IsImage || entry.Thumbnail != null)
                return;
            entry.Thumbnail = LoadBitmap(entry, decodePixelWidth: 52);
        }

        /// <summary>
        /// 加载图片条目的预览图（缓存在条目上）。按预览允许的最大像素宽度降采样解码，
        /// 避免接近 5MB 上限的大图在 UI 线程全量解码造成长阻塞；仅当原图更宽时才降采样。
        /// 缓存时记录实际解码宽度，复用缓存时保证解码宽度不小于当前需求。
        /// </summary>
        public BitmapImage? LoadFullImage(ClipboardEntry entry, int maxPreviewPixelWidth)
        {
            if (!entry.IsImage)
                return null;
            // 已缓存且缓存解码宽度不小于当前需求，直接复用；否则按更大宽度重新解码
            if (entry.PreviewImage is BitmapImage cached && entry.PreviewImageDecodedWidth >= maxPreviewPixelWidth)
                return cached;
            var bmp = LoadBitmap(entry, maxPreviewPixelWidth);
            entry.PreviewImage = bmp;
            entry.PreviewImageDecodedWidth = bmp?.PixelWidth ?? 0;
            return bmp;
        }

        private static BitmapImage? LoadBitmap(ClipboardEntry entry, int decodePixelWidth)
        {
            var path = ImagePath(entry.Id);
            if (!File.Exists(path))
                return null;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad; // 加载后即释放文件句柄
                bmp.UriSource = new Uri(path);
                // 仅当原图更宽时才降采样解码，避免把小图放大失真
                if (decodePixelWidth > 0 && entry.ImageWidth > decodePixelWidth)
                    bmp.DecodePixelWidth = decodePixelWidth;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"加载剪贴板图片失败: {ex.Message}");
                return null;
            }
        }

        private static void DeleteImageFile(ClipboardEntry entry)
        {
            if (!entry.IsImage)
                return;
            try
            {
                var path = ImagePath(entry.Id);
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"删除剪贴板图片文件失败: {ex.Message}");
            }
        }

        private void LoadHistory()
        {
            if (!File.Exists(HistoryFilePath))
                return;
            try
            {
                var json = File.ReadAllText(HistoryFilePath);
                var entries = JsonSerializer.Deserialize<List<ClipboardEntry>>(json) ?? [];
                foreach (var e in entries)
                {
                    // 图片文件已丢失的条目直接丢弃
                    if (e.IsImage && !File.Exists(ImagePath(e.Id)))
                        continue;
                    _entries.Add(e);
                    if (_entries.Count >= MaxEntries)
                        break;
                }
                Logger.LogInfo($"已加载 {_entries.Count} 条剪贴板历史");
            }
            catch (Exception ex)
            {
                Logger.LogError("读取剪贴板历史失败", ex);
            }
        }

        private void SaveHistory()
        {
            try
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                };
                File.WriteAllText(HistoryFilePath, JsonSerializer.Serialize(_entries, options));
            }
            catch (Exception ex)
            {
                Logger.LogError("保存剪贴板历史失败", ex);
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_window != null)
            {
                RemoveClipboardFormatListener(_window.Handle);
                _window.DestroyHandle();
                _window = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}
