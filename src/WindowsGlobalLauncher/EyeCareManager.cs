using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>护眼模式效果类型。</summary>
    public enum EyeCareEffect
    {
        /// <summary>色温 + 亮度过滤（正常模式也走此类型，参数为 6500K/100% 即恒等）。</summary>
        ColorFilter,
        /// <summary>颜色反转（编辑模式），适合弱光环境。</summary>
        Invert,
        /// <summary>灰度（阅读模式），类墨水屏。</summary>
        Grayscale,
    }

    /// <summary>护眼模式定义。参数对照 CareUEyes 官方文档（取日间值）。</summary>
    public class EyeCareMode
    {
        /// <summary>模式名（正常/智能/办公/游戏/电影/编辑/阅读）。</summary>
        public required string Name { get; init; }
        /// <summary>启动器命令名（护眼：xxx）。</summary>
        public string CommandName => EyeCareManager.CommandPrefix + Name;
        /// <summary>列表描述（参数 + 适用场景 + 英文别名便于搜索）。</summary>
        public required string Description { get; init; }
        /// <summary>色温（K），6500 表示不调节。</summary>
        public int ColorTempK { get; init; } = 6500;
        /// <summary>亮度系数，1.0 表示不降低。</summary>
        public double Brightness { get; init; } = 1.0;
        public EyeCareEffect Effect { get; init; } = EyeCareEffect.ColorFilter;
    }

    /// <summary>
    /// 护眼模式管理：通过 Magnification API 的 MagSetFullscreenColorEffect 全屏颜色矩阵实现。
    /// 选此通道而非 SetDeviceGammaRamp 的原因：
    /// 1. 传统 gamma API 在部分机器（HDR/新驱动）上完全失效；
    /// 2. 反色/灰度效果 gamma ramp 无法表达，颜色矩阵可以；
    /// 3. CareUEyes 本身也加载 Magnification.dll 走同一通道。
    /// 注意：该颜色效果在进程退出后仍残留在系统里，因此程序启动时先还原、退出时再还原。
    /// </summary>
    public static class EyeCareManager
    {
        /// <summary>启动器内置命令前缀（护眼：xxx）。</summary>
        public const string CommandPrefix = "护眼：";

        /// <summary>内置模式表（对照 CareUEyes 官方文档，取日间值）。「正常」即还原。</summary>
        public static readonly IReadOnlyList<EyeCareMode> Modes =
        [
            new() { Name = "正常", ColorTempK = 6500, Brightness = 1.00, Effect = EyeCareEffect.ColorFilter,
                    Description = "关闭护眼效果，准确色彩显示 (eye pause)" },
            new() { Name = "智能", ColorTempK = 5000, Brightness = 0.90, Effect = EyeCareEffect.ColorFilter,
                    Description = "色温 5000K，亮度 90%，最大蓝光过滤护眼 (eye health)" },
            new() { Name = "办公", ColorTempK = 5500, Brightness = 0.85, Effect = EyeCareEffect.ColorFilter,
                    Description = "色温 5500K，亮度 85%，适合长时间办公 (eye office)" },
            new() { Name = "游戏", ColorTempK = 6500, Brightness = 0.90, Effect = EyeCareEffect.ColorFilter,
                    Description = "色温 6500K，亮度 90%，保持游戏画质 (eye game)" },
            new() { Name = "电影", ColorTempK = 6000, Brightness = 0.90, Effect = EyeCareEffect.ColorFilter,
                    Description = "色温 6000K，亮度 90%，优化观影体验 (eye movie)" },
            new() { Name = "编辑", ColorTempK = 6500, Brightness = 0.85, Effect = EyeCareEffect.Invert,
                    Description = "颜色反转，亮度 85%，适合弱光环境 (eye editing)" },
            new() { Name = "阅读", ColorTempK = 5500, Brightness = 0.85, Effect = EyeCareEffect.Grayscale,
                    Description = "灰度墨水屏，色温 5500K，亮度 85% (eye reading)" },
        ];

        private static bool _magInitialized;

        /// <summary>当前生效的模式名（内存态；持久化在 AppState）。</summary>
        public static string CurrentModeName { get; private set; } = "正常";

        /// <summary>按命令名（护眼：xxx）查找模式，找不到返回 null。</summary>
        public static EyeCareMode? FindByCommandName(string commandName)
        {
            if (!commandName.StartsWith(CommandPrefix))
            {
                return null;
            }
            var name = commandName[CommandPrefix.Length..];
            return Modes.FirstOrDefault(m => m.Name == name);
        }

        /// <summary>应用指定模式（写矩阵 + 持久化到 AppState）。</summary>
        public static void ApplyMode(EyeCareMode mode)
        {
            var matrix = BuildMatrix(mode);
            ApplyMatrix(matrix);
            CurrentModeName = mode.Name;
            AppState.Instance.SetEyeCareMode(mode.Name);
            Logger.LogInfo($"已应用护眼模式: {mode.Name} (色温 {mode.ColorTempK}K, 亮度 {mode.Brightness:P0}, 效果 {mode.Effect})");
        }

        /// <summary>启动时调用：先还原单位矩阵（清理上次异常退出的残留），再恢复上次保存的模式。</summary>
        public static void RestoreLastMode()
        {
            ResetEffect();
            var savedName = AppState.Instance.GetEyeCareMode();
            var mode = Modes.FirstOrDefault(m => m.Name == savedName);
            if (mode != null && mode.Name != "正常")
            {
                ApplyMode(mode);
            }
        }

        /// <summary>还原为单位矩阵（关闭护眼效果）。程序退出时调用，避免颜色效果残留。</summary>
        public static void ResetEffect()
        {
            try
            {
                ApplyMatrix(BuildIdentity());
                CurrentModeName = "正常";
            }
            catch (Exception ex)
            {
                Logger.LogError("还原护眼颜色矩阵失败", ex);
            }
        }

        private static void ApplyMatrix(float[] matrix)
        {
            if (!_magInitialized)
            {
                if (!MagInitialize())
                {
                    throw new InvalidOperationException("MagInitialize 失败，无法调节屏幕颜色");
                }
                _magInitialized = true;
            }

            var effect = new MAGCOLOREFFECT { Transform = matrix };
            if (!MagSetFullscreenColorEffect(ref effect))
            {
                throw new InvalidOperationException("MagSetFullscreenColorEffect 失败，无法调节屏幕颜色");
            }
        }

        /// <summary>5×5 单位矩阵（恒等变换）。</summary>
        public static float[] BuildIdentity()
        {
            var m = new float[25];
            m[0] = m[6] = m[12] = m[18] = m[24] = 1f;
            return m;
        }

        /// <summary>
        /// 构造模式对应的 5×5 颜色矩阵（行主序，输入向量为 R,G,B,A,1）。
        /// 色温/亮度：对角增益；反色：out = b*(1-in)；灰度：亮度加权后乘色温增益。
        /// </summary>
        public static float[] BuildMatrix(EyeCareMode mode)
        {
            var (r, g, b) = KelvinToRgb(mode.ColorTempK);
            float bright = (float)mode.Brightness;
            float rf = (float)r, gf = (float)g, bf = (float)b;

            switch (mode.Effect)
            {
                case EyeCareEffect.Invert:
                {
                    // out = bright * (1 - in)
                    var m = BuildIdentity();
                    for (int i = 0; i < 3; i++)
                    {
                        m[i * 5 + i] = -bright; // 对角取反
                        m[i * 5 + 4] = bright;  // 平移 +bright
                    }
                    return m;
                }
                case EyeCareEffect.Grayscale:
                {
                    // 灰度 = 0.299R + 0.587G + 0.114B，再乘色温增益与亮度
                    var m = BuildIdentity();
                    float[] gains = [rf * bright, gf * bright, bf * bright];
                    float[] lum = [0.299f, 0.587f, 0.114f];
                    for (int row = 0; row < 3; row++)
                    {
                        for (int col = 0; col < 3; col++)
                        {
                            m[row * 5 + col] = lum[col] * gains[row];
                        }
                    }
                    return m;
                }
                default: // ColorFilter
                {
                    var m = BuildIdentity();
                    m[0] = rf * bright;
                    m[6] = gf * bright;
                    m[12] = bf * bright;
                    return m;
                }
            }
        }

        /// <summary>
        /// Tanner Helland 黑体辐射近似算法：色温(K) → RGB 增益 (0~1)。
        /// 结果按 6500K 归一化，使 6500K 对应 (1,1,1)（即不调节）。
        /// </summary>
        public static (double R, double G, double B) KelvinToRgb(int kelvin)
        {
            kelvin = Math.Clamp(kelvin, 1000, 40000);
            var (r, g, b) = KelvinToRgbRaw(kelvin);
            var (r0, g0, b0) = KelvinToRgbRaw(6500);
            return (Math.Min(r / r0, 1.0), Math.Min(g / g0, 1.0), Math.Min(b / b0, 1.0));
        }

        private static (double R, double G, double B) KelvinToRgbRaw(int kelvin)
        {
            double temp = kelvin / 100.0;
            double r, g, b;

            if (temp <= 66)
            {
                r = 255;
                g = 99.4708025861 * Math.Log(temp) - 161.1195681661;
                b = temp <= 19 ? 0 : 138.5177312231 * Math.Log(temp - 10) - 305.0447927307;
            }
            else
            {
                r = 329.698727446 * Math.Pow(temp - 60, -0.1332047592);
                g = 288.1221695283 * Math.Pow(temp - 60, -0.0755148492);
                b = 255;
            }

            return (Math.Clamp(r, 0, 255) / 255, Math.Clamp(g, 0, 255) / 255, Math.Clamp(b, 0, 255) / 255);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MAGCOLOREFFECT
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 25)]
            public float[] Transform;
        }

        [DllImport("Magnification.dll")]
        private static extern bool MagInitialize();

        [DllImport("Magnification.dll")]
        private static extern bool MagSetFullscreenColorEffect(ref MAGCOLOREFFECT pEffect);
    }
}
