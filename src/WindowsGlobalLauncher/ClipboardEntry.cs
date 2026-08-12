using System;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace CommandLauncher
{
    /// <summary>
    /// 一条剪贴板历史记录（文本或图片）。
    /// 图片内容不落 JSON，按 Id 存为 clipboard-images\{Id}.png，本类只记元数据。
    /// </summary>
    public class ClipboardEntry
    {
        /// <summary>唯一 ID；图片条目同时作为 PNG 文件名。</summary>
        public string Id { get; set; } = "";

        public bool IsImage { get; set; }

        /// <summary>文本内容（图片条目为空串）。</summary>
        public string Text { get; set; } = "";

        /// <summary>图片 PNG 字节哈希，用于去重；文本条目为空串（直接比较 Text 去重）。</summary>
        public string ContentHash { get; set; } = "";

        public int ImageWidth { get; set; }

        public int ImageHeight { get; set; }

        public DateTime Timestamp { get; set; }

        /// <summary>单行预览文本（列表显示用，运行期计算，不持久化）。</summary>
        [JsonIgnore]
        public string Preview => IsImage
            ? $"[图片 {ImageWidth}×{ImageHeight}]"
            : string.Join(' ', Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        /// <summary>图片缩略图（由窗口按需加载，不持久化）。</summary>
        [JsonIgnore]
        public ImageSource? Thumbnail { get; set; }

        /// <summary>图片原图（预览面板用，按需加载并缓存，不持久化）。</summary>
        [JsonIgnore]
        public ImageSource? PreviewImage { get; set; }
    }
}
