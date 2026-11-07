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
    /// <summary>切换剪贴板条目置顶状态的结果。</summary>
    public enum PinToggleResult
    {
        /// <summary>已成功切换并保存。</summary>
        Toggled,
        /// <summary>置顶数量已达上限，本次未切换。</summary>
        LimitReached,
        /// <summary>条目不在历史中（可能已被删除）。</summary>
        NotFound,
    }

    /// <summary>
    /// 剪贴板历史管理器：通过 AddClipboardFormatListener 监听系统剪贴板变化，
    /// 记录文本与图片，按时间倒序保留最近 <see cref="MaxEntries"/> 条（置顶条目免淘汰），重复内容移到最前（去重）。
    /// 持久化仿 AppState 模式：元数据写 clipboard-history.json，图片写 clipboard-images\ 目录。
    /// 所有公开方法须在 UI 线程调用（剪贴板 API 要求 STA）。
    /// </summary>
    public class ClipboardHistoryManager : IDisposable
    {
        /// <summary>最多保留的历史总条数；超限时只淘汰未置顶条目，置顶条目不会被该上限自动清理。</summary>
        public const int MaxEntries = 1000;

        /// <summary>最多可置顶的条目数（达到后拒绝新的置顶，取消置顶不受限制）。</summary>
        public const int MaxPinnedEntries = 100;

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

        /// <summary>历史发生变化（新增/去重置前/置顶状态切换/删除）时触发，供窗口刷新。</summary>
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

                // 已存在的相同图片直接移到最前，不重复写文件
                if (AddOrPromote(entry))
                {
                    Directory.CreateDirectory(ImagesDir);
                    File.WriteAllBytes(ImagePath(entry.Id), pngBytes);
                }
            }
        }

        /// <summary>
        /// 插入新条目；若内容已存在则移到最前并刷新时间（去重，保留原有置顶状态）。
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

            // 超上限时只淘汰最旧的未置顶条目（置顶条目永不淘汰），图片条目连同 PNG 文件一起清理。
            // 刚插入/刚提升的条目必须受保护：外部编辑可能把历史改成「超过上限且几乎全部置顶」，
            // 若不保护，从尾部扫描到最后会把刚复制的内容立刻淘汰（图片还会留下孤儿 PNG）。
            var inserted = existing ?? entry;
            foreach (var evicted in SelectEvictions(_entries, inserted))
            {
                _entries.Remove(evicted);
                DeleteImageFile(evicted);
            }
            if (_entries.Count > MaxEntries)
            {
                // 正常运行时受 MaxPinnedEntries 约束不会出现（置顶数远小于 MaxEntries）；
                // 只有外部编辑把置顶数改到超限时才会走到这里，保留全部置顶、仅提示
                Logger.LogWarning($"剪贴板历史超出上限 {MaxEntries} 条且无可淘汰的未置顶条目，当前共 {_entries.Count} 条（置顶 {_entries.Count(e => e.IsPinned)} 条）");
            }

            SaveHistory();
            HistoryChanged?.Invoke();
            return isNew;
        }

        private static bool SameContent(ClipboardEntry a, ClipboardEntry b)
            => a.IsImage == b.IsImage && (a.IsImage ? a.ContentHash == b.ContentHash : a.Text == b.Text);

        /// <summary>
        /// 查询历史。filter 为空时按 <paramref name="pinnedFirst"/> 选择排序：
        /// true = 置顶优先（组内时间倒序），false = 纯时间倒序（置顶项也参与时间排序）。
        /// filter 非空时对文本条目做模糊匹配，命中结果**始终置顶优先**、组内按粘贴时间倒序，
        /// 忽略 <paramref name="pinnedFirst"/>（搜索行为与旧版一致）；图片条目不参与搜索。
        /// </summary>
        public List<ClipboardEntry> Query(string filter, bool pinnedFirst)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return pinnedFirst ? SortForDisplay(_entries) : SortByTimestampDescending(_entries);

            return MatchFilter(_entries, filter.Trim());
        }

        /// <summary>
        /// 按文本模糊匹配过滤（图片条目不参与），命中结果**始终置顶优先**（与 Tab 排序模式无关）、组内按 Timestamp 倒序；
        /// filter 须已 Trim 且非空。匹配分数只用于判定是否命中、不参与排序（纯静态函数，供单测）。
        /// </summary>
        public static List<ClipboardEntry> MatchFilter(IReadOnlyList<ClipboardEntry> entries, string filter)
        {
            var matched = new List<ClipboardEntry>();
            foreach (var e in entries)
            {
                if (e.IsImage)
                    continue;
                // 超长文本只取开头一段参与匹配，避免每次按键都全量扫描
                var target = e.Text.Length > 1000 ? e.Text[..1000] : e.Text;
                if (FuzzyMatcher.GetMatchScore(filter, target) > 0)
                    matched.Add(e);
            }
            return SortForDisplay(matched);
        }

        /// <summary>
        /// 置顶优先排序（「置顶优先」模式与搜索结果共用）：置顶条目在前，组内按 Timestamp 倒序。
        /// LINQ OrderByDescending 为稳定排序：Timestamp 相同时保持传入顺序（_entries 头部最新）。
        /// 不要改用 List.Sort（不稳定，同毫秒时间戳会乱序）。
        /// </summary>
        public static List<ClipboardEntry> SortForDisplay(IEnumerable<ClipboardEntry> entries)
            => entries.OrderByDescending(e => e.IsPinned)
                      .ThenByDescending(e => e.Timestamp)
                      .ToList();

        /// <summary>
        /// 纯时间倒序（默认「最近优先」模式使用）：置顶与未置顶条目一视同仁，只按 Timestamp 降序。
        /// LINQ 稳定排序：Timestamp 相同时保持传入顺序（_entries 头部最新）；配套单测见 ClipboardHistoryManagerTests。
        /// </summary>
        public static List<ClipboardEntry> SortByTimestampDescending(IEnumerable<ClipboardEntry> entries)
            => entries.OrderByDescending(e => e.Timestamp).ToList();

        /// <summary>判断置顶数量是否已达上限（只统计已置顶条目；取消置顶永远允许）。</summary>
        public static bool IsPinLimitReached(IReadOnlyList<ClipboardEntry> entries)
            => entries.Count(e => e.IsPinned) >= MaxPinnedEntries;

        /// <summary>
        /// 切换条目置顶状态。条目不在历史中返回 <see cref="PinToggleResult.NotFound"/>；
        /// 未置顶且已达上限返回 <see cref="PinToggleResult.LimitReached"/>（不改状态、记 INFO 日志）；
        /// 否则翻转状态、保存并通知刷新。
        /// </summary>
        public PinToggleResult TogglePin(ClipboardEntry entry)
        {
            if (!_entries.Contains(entry))
                return PinToggleResult.NotFound;

            if (!entry.IsPinned && IsPinLimitReached(_entries))
            {
                Logger.LogInfo($"剪贴板置顶数量已达上限（{MaxPinnedEntries} 条），忽略本次置顶请求");
                return PinToggleResult.LimitReached;
            }

            entry.IsPinned = !entry.IsPinned;
            SaveHistory();
            HistoryChanged?.Invoke();
            return PinToggleResult.Toggled;
        }

        /// <summary>
        /// 选出需要淘汰的条目：从列表尾部（最旧）向前，只收集未置顶条目，最多为溢出量条。
        /// 置顶条目永不淘汰；未置顶条目不足时返回全部未置顶条目，由调用方判断并记 WARN（纯静态函数，供单测）。
        /// </summary>
        /// <param name="protectedEntry">
        /// 刚插入/刚提升的条目，永不被选为淘汰对象。极端数据（外部编辑成全置顶且超限）下，
        /// 它可能是列表中唯一的未置顶条目，保护它可以避免刚复制的内容被立即丢弃、图片留下孤儿 PNG。
        /// </param>
        public static List<ClipboardEntry> SelectEvictions(IReadOnlyList<ClipboardEntry> entries, ClipboardEntry? protectedEntry = null)
        {
            var evictions = new List<ClipboardEntry>();
            int overflow = entries.Count - MaxEntries;
            if (overflow <= 0)
                return evictions;

            for (int i = entries.Count - 1; i >= 0 && evictions.Count < overflow; i--)
            {
                if (ReferenceEquals(entries[i], protectedEntry))
                    continue;
                if (!entries[i].IsPinned)
                    evictions.Add(entries[i]);
            }
            return evictions;
        }

        /// <summary>
        /// 删除一条历史（图片条目连同文件删除）。置顶条目不会被删除并返回 false（数据层兜底，
        /// UI 侧应提示用户先取消置顶）；非置顶条目删除成功返回 true。
        /// </summary>
        public bool Delete(ClipboardEntry entry)
        {
            if (entry.IsPinned)
                return false;
            if (!_entries.Remove(entry))
                return false;
            DeleteImageFile(entry);
            SaveHistory();
            HistoryChanged?.Invoke();
            return true;
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

        /// <summary>
        /// 按需加载图片缩略图（缓存在条目上，跨刷新生效，不持久化）。
        /// 由 ClipboardWindow.ThumbnailConverter 在行被实例化 / 容器被复用时调用（UI 线程）。
        /// 不要改回在 ClipboardWindow.RefreshList 中全量调用——上限 1000 时会在唤出时同步解码上千张图、冻结 UI。
        /// </summary>
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
                }

                // 先加载全部有效条目，再从内存中移除超限的最旧非置顶条目；
                // 加载阶段不删除 PNG 文件（与既有行为一致：超出上限的图片文件本就留在磁盘上）
                foreach (var evicted in SelectEvictions(_entries))
                    _entries.Remove(evicted);

                int pinnedCount = _entries.Count(e => e.IsPinned);
                Logger.LogInfo($"已加载 {_entries.Count} 条剪贴板历史（置顶 {pinnedCount} 条）");
                if (pinnedCount > MaxPinnedEntries)
                {
                    // 外部编辑可能把置顶数改到超过上限：全部保留、不静默取消置顶，仅阻止继续新增
                    Logger.LogWarning($"剪贴板历史置顶条目数（{pinnedCount}）超过上限 {MaxPinnedEntries}，已全部保留；取消部分置顶前无法新增置顶");
                }
                if (_entries.Count > MaxEntries)
                {
                    Logger.LogWarning($"剪贴板历史超出上限 {MaxEntries} 条且无可淘汰的未置顶条目，当前共 {_entries.Count} 条");
                }
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
