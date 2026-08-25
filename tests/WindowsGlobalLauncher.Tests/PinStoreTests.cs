using System;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommandLauncher;
using Xunit;

namespace WindowsGlobalLauncher.Tests
{
    /// <summary>
    /// 贴图持久化（PinStore）测试：只走底层纯数据方法（Save/EnsureImagePng/Load/DecodeImage），
    /// 用临时目录隔离，不碰真实数据目录、不创建任何 PinWindow 实例。
    /// </summary>
    public sealed class PinStoreTests : IDisposable
    {
        private readonly string _dir;

        public PinStoreTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, recursive: true);
            }
            catch
            {
                // 清理失败不影响测试结果（临时目录，系统会回收）
            }
        }

        /// <summary>快速造一个条目（图片 id 也是合法 Guid N 格式，便于孤儿清理判定）。</summary>
        private static PinStore.PinEntry NewEntry(bool isImage)
            => new()
            {
                Id = Guid.NewGuid().ToString("N"),
                IsImage = isImage,
                Text = isImage ? "" : "备注：含中文 & 换行\n第二行",
                Category = isImage ? "" : "蓝",
                LeftDip = -123.5,   // 负值也允许（虚拟屏原点可能在副屏右侧）
                TopDip = 456.25,
                Zoom = isImage ? 1.75 : 1.0,
                Opacity = 0.65,
            };

        private static void AssertEntryEqual(PinStore.PinEntry expected, PinStore.PinEntry actual)
        {
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.IsImage, actual.IsImage);
            Assert.Equal(expected.Text, actual.Text);
            Assert.Equal(expected.Category, actual.Category);
            Assert.Equal(expected.LeftDip, actual.LeftDip);
            Assert.Equal(expected.TopDip, actual.TopDip);
            Assert.Equal(expected.Zoom, actual.Zoom);
            Assert.Equal(expected.Opacity, actual.Opacity);
        }

        [Fact]
        public void SaveThenLoad_SingleTextEntry_RoundTrips()
        {
            var entry = NewEntry(isImage: false);

            PinStore.Save(new[] { entry }, _dir);
            var loaded = PinStore.Load(_dir);

            var single = Assert.Single(loaded);
            AssertEntryEqual(entry, single);
        }

        [Fact]
        public void SaveThenLoad_MixedImageAndText_RoundTrips()
        {
            var text = NewEntry(isImage: false);
            var image = NewEntry(isImage: true);
            EnsureTestPng(image.Id); // 图片条目必须有 PNG 文件，否则 Load 会丢弃它

            PinStore.Save(new PinStore.PinEntry[] { text, image }, _dir);
            var loaded = PinStore.Load(_dir);

            Assert.Equal(2, loaded.Count);
            AssertEntryEqual(text, loaded.First(e => e.Id == text.Id));
            AssertEntryEqual(image, loaded.First(e => e.Id == image.Id));
        }

        [Fact]
        public void SaveThenLoad_JsonFile_DoesNotEscapeChinese()
        {
            var entry = NewEntry(isImage: false);

            PinStore.Save(new[] { entry }, _dir);

            // UnsafeRelaxedJsonEscaping：中文与常见符号不被 \uXXXX 转义，直接读文件文本验证。
            // 注意换行等 JSON 控制符仍按 JSON 规范转义（\n），故断言拆分到不含换行的片段。
            var json = File.ReadAllText(Path.Combine(_dir, "pins.json"));
            Assert.Contains("备注：含中文", json);
            Assert.Contains(entry.Category, json);
            Assert.DoesNotContain("\\u5907", json); // 「备」若被转义则出现 备
        }

        [Fact]
        public void Load_ImageEntryMissingPng_IsDropped()
        {
            var image = NewEntry(isImage: true);
            PinStore.Save(new[] { image }, _dir);

            // 此时 PNG 不存在 → Load 直接丢弃图片条目
            Assert.Empty(PinStore.Load(_dir));

            // 补上 PNG 后恢复可见；再删掉又丢弃（与 ClipboardHistoryManager 加载先例一致）
            var pngPath = EnsureTestPng(image.Id);
            Assert.Single(PinStore.Load(_dir));
            File.Delete(pngPath);
            Assert.Empty(PinStore.Load(_dir));
        }

        [Fact]
        public void Load_CorruptedJson_ReturnsEmptyWithoutThrow()
        {
            File.WriteAllText(Path.Combine(_dir, "pins.json"), "{ 这不是合法 JSON …");
            Assert.Empty(PinStore.Load(_dir));
        }

        [Fact]
        public void Load_MissingFile_ReturnsEmpty()
        {
            Assert.Empty(PinStore.Load(Path.Combine(_dir, "不存在")));
        }

        [Fact]
        public void Save_EmptyList_RemovesOrphanPngs()
        {
            // 预置两个带合法 Guid N 文件名的 PNG：一个仍在列表中（存活）、一个不在（孤儿，应被删）
            var live = NewEntry(isImage: true);
            var orphanPath = EnsureTestPng(Guid.NewGuid().ToString("N"));
            var livePath = EnsureTestPng(live.Id);

            PinStore.Save(new[] { live }, _dir);

            Assert.False(File.Exists(orphanPath));
            Assert.True(File.Exists(livePath));

            // 再保存空列表：live 也变成孤儿被删
            PinStore.Save(Array.Empty<PinStore.PinEntry>(), _dir);
            Assert.False(File.Exists(livePath));
            Assert.Empty(PinStore.Load(_dir));
        }

        [Fact]
        public void Save_DoesNotDeleteNonGuidPngFiles()
        {
            // 防御性最小化：文件名不是合法 Guid N 格式的 png 绝不删除（可能是用户自己放的文件）
            var pngDir = Path.Combine(_dir, "pins");
            Directory.CreateDirectory(pngDir);
            var foreignPath = Path.Combine(pngDir, "我的图片.png");
            File.WriteAllBytes(foreignPath, new byte[] { 1, 2, 3 });

            PinStore.Save(Array.Empty<PinStore.PinEntry>(), _dir);

            Assert.True(File.Exists(foreignPath));
        }

        [Fact]
        public void DecodeImage_MissingFile_ReturnsNull()
        {
            Assert.Null(PinStore.DecodeImage(Guid.NewGuid().ToString("N"), _dir));
        }

        [Fact]
        public void EnsureImagePng_ThenDecodeImage_RoundTrips()
        {
            // 1×1 纯红小图，经 EnsureImagePng 编码 → DecodeImage 解码，验证管线闭环
            var id = Guid.NewGuid().ToString("N");
            var source = BitmapSource.Create(
                1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 0, 0, 255, 255 }, 4);

            PinStore.EnsureImagePng(id, source, _dir);
            var pngPath = Path.Combine(_dir, "pins", id + ".png");
            Assert.True(File.Exists(pngPath));

            var decoded = PinStore.DecodeImage(id, _dir);
            Assert.NotNull(decoded);
            Assert.Equal(1, decoded!.PixelWidth);
            Assert.Equal(1, decoded.PixelHeight);
            Assert.True(decoded.IsFrozen); // 解码结果须已 Freeze（可跨线程使用）

            // 重复调用不覆盖已存在的 PNG（内容不变的短路写盘）
            var written = File.ReadAllBytes(pngPath);
            PinStore.EnsureImagePng(id, source, _dir);
            Assert.Equal(written, File.ReadAllBytes(pngPath));
        }

        /// <summary>在临时目录写一个最小的合法 PNG（内容不重要，Load 只校验存在性）。</summary>
        private string EnsureTestPng(string id)
        {
            var pngDir = Path.Combine(_dir, "pins");
            Directory.CreateDirectory(pngDir);
            var path = Path.Combine(pngDir, id + ".png");
            // 1×1 透明 PNG 的最小合法字节序列
            byte[] minimalPng =
            [
                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                0x42, 0x60, 0x82,
            ];
            File.WriteAllBytes(path, minimalPng);
            return path;
        }
    }
}
