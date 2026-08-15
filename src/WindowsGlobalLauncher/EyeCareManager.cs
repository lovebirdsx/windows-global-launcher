using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace CommandLauncher
{
    /// <summary>护眼模式定义。参数对照 CareUEyes 官方文档（取日间值）。</summary>
    public class EyeCareMode
    {
        /// <summary>模式名（正常/办公）。</summary>
        public required string Name { get; init; }
        /// <summary>启动器命令名（护眼：xxx）。</summary>
        public string CommandName => EyeCareManager.CommandPrefix + Name;
        /// <summary>列表描述（参数 + 适用场景 + 英文别名便于搜索）。</summary>
        public required string Description { get; init; }
        /// <summary>色温（K），6500 表示不调节。</summary>
        public int ColorTempK { get; init; } = 6500;
        /// <summary>亮度系数，1.0 表示不降低。</summary>
        public double Brightness { get; init; } = 1.0;
    }

    /// <summary>
    /// 护眼模式管理：通过 Magnification API 的 MagSetFullscreenColorEffect 全屏颜色矩阵实现。
    /// 选此通道而非 SetDeviceGammaRamp 的原因：
    /// 1. 传统 gamma API 在部分机器（HDR/新驱动）上完全失效；
    /// 2. CareUEyes 本身也加载 Magnification.dll 走同一通道。
    /// 注意：该颜色效果在进程退出后仍残留在系统里，因此程序启动时先还原、退出时再还原。
    /// </summary>
    public static class EyeCareManager
    {
        /// <summary>启动器内置命令前缀（护眼：xxx）。</summary>
        public const string CommandPrefix = "护眼：";

        /// <summary>内置模式表（对照 CareUEyes 官方文档，取日间值）。「正常」即还原。</summary>
        public static readonly IReadOnlyList<EyeCareMode> Modes =
        [
            new() { Name = "正常", ColorTempK = 6500, Brightness = 1.00,
                    Description = "关闭护眼效果，准确色彩显示 (eye pause)" },
            new() { Name = "办公", ColorTempK = 5500, Brightness = 0.85,
                    Description = "色温 5500K，亮度 85%，适合长时间办公 (eye office)" },
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
            Logger.LogInfo($"已应用护眼模式: {mode.Name} (色温 {mode.ColorTempK}K, 亮度 {mode.Brightness:P0})");
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

        /// <summary>
        /// 临时挂起护眼颜色效果（只写恒等矩阵，不改 CurrentModeName 与持久化状态）。
        /// 供截图抓屏前调用，避免成品图被颜色矩阵污染。返回是否实际挂起了效果
        /// （当前已是「正常」或写矩阵失败时返回 false，调用方据此决定是否需要恢复）。
        /// </summary>
        public static bool SuspendEffect()
        {
            if (CurrentModeName == "正常")
            {
                return false;
            }
            try
            {
                ApplyMatrix(BuildIdentity());
                return true;
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"临时挂起护眼颜色矩阵失败，抓屏将包含护眼色彩: {ex.Message}");
                return false;
            }
        }

        /// <summary>恢复 SuspendEffect 挂起前的模式矩阵（按 CurrentModeName 重新应用）。</summary>
        public static void ResumeEffect()
        {
            var mode = Modes.FirstOrDefault(m => m.Name == CurrentModeName);
            if (mode == null || mode.Name == "正常")
            {
                return;
            }
            try
            {
                ApplyMatrix(BuildMatrix(mode));
            }
            catch (Exception ex)
            {
                Logger.LogError($"恢复护眼颜色矩阵失败（模式: {mode.Name}），屏幕将停留在无护眼状态", ex);
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
        /// 色温/亮度过滤：对角增益（R,G,B 各自乘以色温增益 × 亮度系数）。
        /// </summary>
        public static float[] BuildMatrix(EyeCareMode mode)
        {
            var (r, g, b) = KelvinToRgb(mode.ColorTempK);
            float bright = (float)mode.Brightness;

            var m = BuildIdentity();
            m[0] = (float)r * bright;
            m[6] = (float)g * bright;
            m[12] = (float)b * bright;
            return m;
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
