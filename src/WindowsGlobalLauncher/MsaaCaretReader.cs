using System;
using System.Drawing;
using System.Runtime.InteropServices;
using Accessibility;

namespace CommandLauncher
{
    /// <summary>
    /// MSAA（Microsoft Active Accessibility）自绘光标读取：通过实际焦点 HWND 的
    /// <c>AccessibleObjectFromWindow(OBJID_CARET)</c> 取 <c>IAccessible.accLocation</c>。
    /// <para>
    /// 适用于 Chromium/Electron 等没有 Win32 系统插入符、但通过 MSAA 暴露 caret 对象的控件。
    /// 该读取器本身不做「是否可编辑」判断：调用方（<see cref="UiaCaretReader.Query"/>）必须先用
    /// UIA 证明控件可编辑、非 Terminal 且 RuntimeId 稳定；MSAA 只补坐标/caret 证据，
    /// 绝不由几何形状反向放行只读、禁用或可编辑性未知的控件。
    /// </para>
    /// <para>
    /// 所有 COM 调用都留在调用线程（现有 ImeProbeWorker 的固定后台 MTA 线程），
    /// 不得在 UI 线程或低级钩子回调里调用。只读角色、状态与屏幕矩形，
    /// 不读取 Name/Value/任何文本。
    /// </para>
    /// </summary>
    internal static class MsaaCaretReader
    {
        /// <summary>OBJID_CARET：系统为自绘光标对象保留的固定对象 ID。</summary>
        private const uint OBJID_CARET = 0xFFFFFFF8;

        /// <summary>CHILDID_SELF：查询对象自身的角色/状态/位置。</summary>
        private const int CHILDID_SELF = 0;

        // MSAA 角色：按批准计划只接受系统 caret 对象，不接受 ROLE_SYSTEM_TEXT
        //（Text 角色还覆盖普通静态文本，无法证明它是插入符）。
        internal const int RoleSystemCaret = 0x7;

        // MSAA 状态位
        internal const int StateSystemUnavailable = 0x00000001;
        internal const int StateSystemInvisible = 0x00008000;
        internal const int StateSystemOffscreen = 0x00010000;

        private static readonly Guid IidIAccessible = new("618736E0-3C3D-11CF-810C-00AA00389B71");

        [DllImport("oleacc.dll")]
        private static extern int AccessibleObjectFromWindow(
            IntPtr hwnd, uint dwId, ref Guid riid, out IntPtr ppvObject);

        /// <summary>
        /// 从实际焦点 HWND 读取 MSAA caret 屏幕矩形（物理像素）。
        /// </summary>
        /// <param name="focusHwnd">当前 Win32 实际焦点窗口（不是顶层前台窗口）。</param>
        /// <param name="focusedElementBounds">
        /// 由 UIA 焦点元素提供的边界矩形；必须存在，否则无法证明光标属于当前可视输入区域。
        /// </param>
        /// <param name="caret">成功时的光标矩形。</param>
        /// <param name="reason">失败时的具体原因（MsaaUnavailable/MsaaRejected）。</param>
        /// <param name="hresult">COM 失败时的 HRESULT；0 表示无特定失败码。</param>
        internal static bool TryReadCaret(
            IntPtr focusHwnd,
            Rectangle? focusedElementBounds,
            out Rectangle caret,
            out ImeProbeReason reason,
            out int hresult)
        {
            caret = default;
            reason = ImeProbeReason.MsaaUnavailable;
            hresult = 0;

            if (focusHwnd == IntPtr.Zero)
                return false;

            IntPtr accessiblePtr = IntPtr.Zero;
            IAccessible? accessible = null;
            // 静态 readonly 字段不能被 ref 传入 P/Invoke（CS0199），复制到局部变量再传。
            Guid iidAccessible = IidIAccessible;

            try
            {
                hresult = AccessibleObjectFromWindow(
                    focusHwnd, OBJID_CARET, ref iidAccessible, out accessiblePtr);

                if (hresult != 0 || accessiblePtr == IntPtr.Zero)
                {
                    reason = ImeProbeReason.MsaaUnavailable;
                    return false;
                }

                accessible = Marshal.GetTypedObjectForIUnknown(accessiblePtr, typeof(IAccessible))
                    as IAccessible;
                if (accessible == null)
                {
                    reason = ImeProbeReason.MsaaUnavailable;
                    return false;
                }

                // 标准 Accessibility.IAccessible 声明（Accessibility.dll）：
                // accRole/accState 在元数据中是带一个索引参数的属性（DISPID -5006/-5007），
                // 使用属性访问语法调用框架提供的 getter，而不是手写 IDispatch/vtable。
                object? roleValue = accessible.accRole[CHILDID_SELF];
                object? stateValue = accessible.accState[CHILDID_SELF];
                if (!TryToInt(roleValue, out int role) || !TryToInt(stateValue, out int state))
                {
                    // 角色/状态无法证明 -> 拒绝，绝不把未知对象当 caret
                    reason = ImeProbeReason.MsaaRejected;
                    return false;
                }

                accessible.accLocation(
                    out int left, out int top, out int width, out int height, CHILDID_SELF);

                Rectangle? accepted = AcceptCaretRect(
                    role, state, left, top, width, height, focusedElementBounds);

                if (accepted is not { } rect)
                {
                    reason = ImeProbeReason.MsaaRejected;
                    return false;
                }

                caret = rect;
                reason = ImeProbeReason.None;
                return true;
            }
            catch (COMException ex)
            {
                hresult = ex.HResult;
                reason = ImeProbeReason.MsaaUnavailable;
                return false;
            }
            catch
            {
                reason = ImeProbeReason.MsaaUnavailable;
                return false;
            }
            finally
            {
                if (accessible != null)
                {
                    try
                    {
                        if (Marshal.IsComObject(accessible))
                            Marshal.ReleaseComObject(accessible);
                    }
                    catch
                    {
                        // best effort：释放失败不能影响查询结果
                    }
                }

                if (accessiblePtr != IntPtr.Zero)
                    Marshal.Release(accessiblePtr);
            }
        }

        /// <summary>
        /// 纯决策：MSAA caret 矩形是否可信。
        /// <list type="bullet">
        /// <item>角色必须是 ROLE_SYSTEM_CARET（不接受 ROLE_SYSTEM_TEXT 宽松放行）；</item>
        /// <item>不得带 UNAVAILABLE/INVISIBLE/OFFSCREEN 状态；</item>
        /// <item>宽高必须为正，且通过通用「窄而高」插入符阈值（拒绝整行/控件宽矩形）；</item>
        /// <item>必须落在当前焦点 UIA 元素的可信边界内（同一顶层窗口不足以证明）；</item>
        /// <item>没有元素边界时拒绝，不猜。</item>
        /// </list>
        /// </summary>
        internal static Rectangle? AcceptCaretRect(
            int role, int state, int left, int top, int width, int height,
            Rectangle? focusedElementBounds)
        {
            if (role != RoleSystemCaret)
                return null;

            if ((state & (StateSystemUnavailable | StateSystemInvisible | StateSystemOffscreen)) != 0)
                return null;

            if (width <= 0 || height <= 0)
                return null;

            if (!CaretLocator.IsTrustedCaretRect(new Rectangle(left, top, width, height)))
                return null;

            if (!focusedElementBounds.HasValue)
                return null;

            var rect = new Rectangle(left, top, width, height);
            var allowed = focusedElementBounds.Value;
            allowed.Inflate(2, 2);
            if (!allowed.Contains(rect))
                return null;

            return rect;
        }

        private static bool TryToInt(object? value, out int result)
        {
            switch (value)
            {
                case int i:
                    result = i;
                    return true;
                case short s:
                    result = s;
                    return true;
                case byte b:
                    result = b;
                    return true;
                case uint u when u <= int.MaxValue:
                    result = (int)u;
                    return true;
                case long l when l is >= int.MinValue and <= int.MaxValue:
                    result = (int)l;
                    return true;
                default:
                    result = 0;
                    return false;
            }
        }

        // 这里不再手写 IAccessible：直接使用 Windows Desktop 自带的
        // Accessibility.IAccessible（System.Windows.Forms 依赖的 Accessibility.dll）。
        // 该标准声明把 accRole/accState 暴露为 get_accRole/get_accState 索引 propget，
        // 与 IDL 的 DISPID -5006/-5007 一致，避免自定义 IDispatch 声明把属性访问
        // 错当成普通方法（会缺少 DISPATCH_PROPERTYGET）。
    }
}
