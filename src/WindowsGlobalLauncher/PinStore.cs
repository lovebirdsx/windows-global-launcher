using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace CommandLauncher
{
    /// <summary>
    /// 贴图持久化与恢复：图片贴图与文字便签在应用重启后自动恢复
    /// （位置/缩放/透明度/分类/文本内容/图片内容）。
    /// 持久化仿 <see cref="ClipboardHistoryManager"/> 模式：元数据写 pins.json，
    /// 图片按贴图 Id 存 pins\{Id}.png；交互点（新建/关闭/拖动/编辑/分类/缩放/透明度）
    /// 全部经 500ms 防抖合并保存（见 <see cref="ScheduleSave"/>），
    /// 退出时经 <see cref="Flush"/> 兜底立即保存。
    /// 全部文件 I/O 包 try/catch 记日志，绝不向调用方抛异常。
    /// 除 Save/EnsureImagePng/Load/DecodeImage（纯数据、供测试直接调用，可注入临时目录 dir）
    /// 外，其余方法仅允许在 UI 线程访问。
    /// </summary>
    public static class PinStore
    {
        /// <summary>防抖时间窗：交互点频繁触发（滚轮缩放/调透明度）时合并为一次落盘。</summary>
        private const int SaveDebounceMs = 500;

        /// <summary>便签文本恢复前的持久化长度上限（与 F7 钉文字时的过滤上限语义一致，见 ScreenshotManager.MaxPinTextLength）。</summary>
        private const int MaxPinTextLength = 50_000;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 中文不转义（同 ClipboardHistoryManager）
        };

        // 元数据文件路径。dir 为 null 时直接用 App.BaseDir；非 null（仅测试）时拼接为
        // BaseDir\dir\pins.json —— dir 是临时目录名，测试不碰真实数据目录。
        private static string StoreFilePath(string? dir = null)
            => dir == null
                ? Path.Combine(App.BaseDir, "pins.json")
                : Path.Combine(App.BaseDir, dir, "pins.json");

        // 图片目录路径（与 StoreFilePath 同层级的 pins 子目录），dir 语义同上
        private static string PinsDir(string? dir = null)
            => dir == null
                ? Path.Combine(App.BaseDir, "pins")
                : Path.Combine(App.BaseDir, dir, "pins");

        // 单张贴图的 PNG 路径：pins\{id}.png
        private static string ImagePath(string id, string? dir = null)
            => Path.Combine(PinsDir(dir), id + ".png");

        /// <summary>一张贴图的持久化元数据（图片贴图与文字便签共用，按 <see cref="IsImage"/> 区分有效字段）。</summary>
        public sealed class PinEntry
        {
            /// <summary>贴图唯一标识（Guid N 格式，同时是图片贴图的 PNG 文件名）。</summary>
            public string Id { get; set; } = "";

            /// <summary>true = 图片贴图（图片存 pins\{Id}.png）；false = 文字便签（内容在 <see cref="Text"/>）。</summary>
            public bool IsImage { get; set; }

            /// <summary>仅文字便签：文本内容。</summary>
            public string Text { get; set; } = "";

            /// <summary>仅文字便签：分类名（8 个预设之一，见 PinWindow.NoteCategories）。</summary>
            public string Category { get; set; } = "";

            /// <summary>窗口位置（DIP，WPF Left）。</summary>
            public double LeftDip { get; set; }

            /// <summary>窗口位置（DIP，WPF Top）。</summary>
            public double TopDip { get; set; }

            /// <summary>仅图片贴图：缩放比例（0.1~5.0）。</summary>
            public double Zoom { get; set; } = 1.0;

            /// <summary>窗口透明度（0.2~1.0，两模式共用）。</summary>
            public double Opacity { get; set; } = 1.0;
        }

        // ---- 防抖保存（仅 UI 线程访问）----

        private static DispatcherTimer? _saveTimer;

        /// <summary>
        /// 防抖保存的定时器懒初始化（首次调用时才在 UI 线程创建）：
        /// 每次触发先重置计时，连续交互（如按住滚轮反复缩放）只落盘一次。
        /// </summary>
        private static DispatcherTimer DeferredTimer => _saveTimer ??= CreateTimer();

        private static DispatcherTimer CreateTimer()
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SaveDebounceMs) };
            timer.Tick += (s, e) =>
            {
                timer.Stop();
                SavePins(PinWindow.OpenPins);
            };
            return timer;
        }

        /// <summary>调度一次防抖保存：500ms 内未再触发则落盘；再次触发则重新计时（仅 UI 线程）。</summary>
        public static void ScheduleSave()
        {
            try
            {
                var timer = DeferredTimer;
                timer.Stop();
                timer.Start();
            }
            catch (Exception ex)
            {
                Logger.LogError("调度贴图保存失败", ex);
            }
        }

        /// <summary>
        /// 立即落盘（退出兜底，App.OnExit 调用）：停掉待执行的防抖 timer 并同步保存当前全部贴图。
        /// 平时强杀/崩溃时 OnExit 不执行，靠交互点保存兜底。
        /// </summary>
        public static void Flush()
        {
            try
            {
                _saveTimer?.Stop();
                SavePins(PinWindow.OpenPins);
            }
            catch (Exception ex)
            {
                Logger.LogError("贴图退出兜底保存失败", ex);
            }
        }

        /// <summary>
        /// 保存全部已打开贴图（仅 UI 线程）：映射 PinWindow 列表 → PinEntry，
        /// 图片条目先把 PNG 落盘（缺失才写），再统一写 JSON 元数据。
        /// </summary>
        public static void SavePins(IReadOnlyList<PinWindow> pins)
        {
            try
            {
                var entries = new List<PinEntry>(pins.Count);
                foreach (var pin in pins)
                {
                    var entry = new PinEntry
                    {
                        Id = pin.PinId,
                        IsImage = pin.IsImagePin,
                        Text = pin.IsImagePin ? "" : pin.PinText,
                        Category = pin.IsImagePin ? "" : pin.PinCategory,
                        LeftDip = pin.Left,
                        TopDip = pin.Top,
                        Zoom = pin.IsImagePin ? pin.PinZoom : 1.0,
                        Opacity = pin.Opacity,
                    };
                    if (entry.IsImage && pin.PinImageSource != null)
                        EnsureImagePng(entry.Id, pin.PinImageSource);
                    entries.Add(entry);
                }
                Save(entries);
            }
            catch (Exception ex)
            {
                Logger.LogError("保存贴图状态失败", ex);
            }
        }

        // ---- 底层纯数据操作（可测）：不依赖 PinWindow，dir 参数注入临时目录 ----

        /// <summary>
        /// 把元数据写入 pins.json，并清理孤儿 PNG（pins 目录中不在 entries 里的贴图图片）。
        /// 孤儿清理是防御性最小化：只删文件名（不含扩展名）是合法 Guid N 格式且不在 entries
        /// 中的 png，绝不碰用户自己放进同目录的其它文件。无图片条目时 pins 目录可能根本不存在，跳过。
        /// </summary>
        public static void Save(IEnumerable<PinEntry> entries, string? dir = null)
        {
            try
            {
                var list = new List<PinEntry>(entries);
                File.WriteAllText(StoreFilePath(dir), JsonSerializer.Serialize(list, JsonOptions));

                if (!Directory.Exists(PinsDir(dir)))
                    return;

                var liveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in list)
                {
                    if (e.IsImage)
                        liveIds.Add(e.Id);
                }
                foreach (var png in Directory.GetFiles(PinsDir(dir), "*.png"))
                {
                    var name = Path.GetFileNameWithoutExtension(png);
                    // 只把「合法贴图 Id 且已不在列表中」的 png 判为孤儿
                    if (Guid.TryParseExact(name, "N", out _) && !liveIds.Contains(name))
                        File.Delete(png);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("写入贴图元数据失败", ex);
            }
        }

        /// <summary>
        /// 图片条目 PNG 缺失才写盘（已存在则跳过——同一贴图内容不变，Id 不变就不必重编码）。
        /// 先写临时文件再同卷 rename（与 UpdateInstaller 的 .new 模式同理）：编码中途被强杀
        /// 只留下可清理的 .tmp，不会在原路径留下半个 PNG（半个 PNG 会让恢复时该贴图永久丢失）。
        /// </summary>
        public static void EnsureImagePng(string id, BitmapSource image, string? dir = null)
        {
            var path = ImagePath(id, dir);
            try
            {
                if (File.Exists(path))
                    return;
                Directory.CreateDirectory(PinsDir(dir));
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                var tmp = path + ".tmp";
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                    encoder.Save(fs);
                File.Move(tmp, path);
            }
            catch (Exception ex)
            {
                // 失败时尽力清掉可能写了一半的临时文件（存在即删，不存在忽略）
                try { File.Delete(path + ".tmp"); } catch { }
                Logger.LogError($"写入贴图图片 {id} 失败", ex);
            }
        }

        /// <summary>
        /// 读取贴图元数据：文件不存在 → 空列表；JSON 损坏/条目无效 → 尽量返回有效部分，
        /// 完全无法解析 → 空列表。绝不抛异常。
        /// 图片条目在此即过滤掉 PNG 已丢失的（同 ClipboardHistoryManager 加载先例）。
        /// </summary>
        public static List<PinEntry> Load(string? dir = null)
        {
            var result = new List<PinEntry>();
            try
            {
                var path = StoreFilePath(dir);
                if (!File.Exists(path))
                    return result;

                var json = File.ReadAllText(path);
                var entries = JsonSerializer.Deserialize<List<PinEntry>>(json);
                if (entries == null)
                    return result;

                foreach (var e in entries)
                {
                    if (string.IsNullOrEmpty(e.Id))
                        continue; // 缺 Id 无法定位 PNG/无法追踪，丢弃
                    if (e.IsImage && !File.Exists(ImagePath(e.Id, dir)))
                        continue; // 图片文件已丢失的条目直接丢弃
                    result.Add(e);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("读取贴图元数据失败", ex);
            }
            return result;
        }

        /// <summary>
        /// 按贴图 Id 解码 PNG：BitmapImage + CacheOption.OnLoad（加载后即释放文件句柄）+ Freeze。
        /// 文件缺失/解码失败 → null 并记 WARN（调用方据此丢弃该条目），绝不抛异常。
        /// </summary>
        public static BitmapSource? DecodeImage(string id, string? dir = null)
        {
            var path = ImagePath(id, dir);
            if (!File.Exists(path))
            {
                Logger.LogWarning($"贴图图片文件不存在，已跳过: {id}");
                return null;
            }
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"解码贴图图片失败（跳过该贴图）: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 恢复上次退出时仍打开的贴图（App.OnStartup 调用，仅 UI 线程）：
        /// 逐条校验（图片解码失败/文本为空则丢弃）→ 经 <see cref="PinWindow.RestoreFromEntry"/>
        /// 重建并直接显示。整体隐藏状态不记忆，恢复后一律直接显示。
        /// </summary>
        public static void RestorePins()
        {
            try
            {
                var entries = Load();
                int restored = 0;
                foreach (var entry in entries)
                {
                    // 逐条防护：单条数据异常（手改 JSON 出 NaN/Infinity 等）只丢该条，不阻断其余恢复
                    try
                    {
                        if (entry.IsImage)
                        {
                            var image = DecodeImage(entry.Id);
                            if (image == null)
                            {
                                Logger.LogWarning($"贴图图片不可用时丢弃该条目: {entry.Id}");
                                continue;
                            }
                            PinWindow.RestoreFromEntry(entry, image);
                        }
                        else
                        {
                            if (string.IsNullOrWhiteSpace(entry.Text) || entry.Text.Length > MaxPinTextLength)
                                continue;
                            PinWindow.RestoreFromEntry(entry, null);
                        }
                        restored++;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning($"恢复贴图 {entry.Id} 失败，跳过该条目: {ex.Message}");
                    }
                }
                if (restored > 0)
                    Logger.LogInfo($"已恢复 {restored} 个贴图");
            }
            catch (Exception ex)
            {
                Logger.LogError("恢复贴图失败", ex);
            }
        }
    }
}
