using System;
using System.Drawing;

namespace CommandLauncher
{
    /// <summary>
    /// 一次输入焦点的身份。所有字段都参与值比较（record struct 默认相等语义）：
    /// 只用顶层窗口或进程 ID 无法区分同一 HWND 内的不同输入控件，因此还包含实际焦点 HWND/线程
    /// 以及 UIA RuntimeId。
    /// </summary>
    /// <param name="Foreground">前台窗口句柄。</param>
    /// <param name="FocusHwnd">实际接收键盘焦点的 HWND（可能是前台窗口的子窗口）。</param>
    /// <param name="FocusThreadId">实际焦点 HWND 所属线程 ID。</param>
    /// <param name="ProcessId">实际焦点 HWND 所属进程 ID。</param>
    /// <param name="RuntimeId">
    /// UIA RuntimeId 归一化后的字符串（各段十进制整数用逗号连接）；null 表示 UIA 不可用。
    /// 它用于区分浏览器/Electron 中同一个 HWND 内的不同虚拟输入控件。
    /// </param>
    internal readonly record struct ImeFocusIdentity(
        IntPtr Foreground,
        IntPtr FocusHwnd,
        uint FocusThreadId,
        uint ProcessId,
        string? RuntimeId);

    /// <summary>
    /// 输入法提示的严格焦点快照（纯数据，不含任何 COM 对象）。
    /// <para>
    /// 调用方通过 <see cref="CaretLocator.TryCaptureFocusSnapshot"/> 获取：
    /// 返回 null 表示无有效焦点、前台已变化或查询失败；返回非 null 时
    /// <see cref="IsEditable"/>=false 表示未获可编辑性准入：可能明确不可编辑（按钮、只读文本等），
    /// 也可能证据不足（Diagnostic.Reason=EditableUnknown）；两者都隐藏提示而不继续等待。
    /// </para>
    /// <para>
    /// <see cref="Caret"/> 为 null 只表示「尚未就绪或无法安全定位」，不表示查询失败；
    /// 此时不得用鼠标位置或控件边缘兜底，也不能显示提示。
    /// </para>
    /// </summary>
    /// <param name="Identity">焦点身份（值比较，用于区分焦点会话）。</param>
    /// <param name="IsEditable">该焦点是否为可编辑输入控件。</param>
    /// <param name="Caret">真实插入符的屏幕矩形（物理像素）；null=未就绪/无法安全定位。</param>
    /// <param name="DpiHwnd">
    /// 表达插入符位置的窗口句柄（Win32 插入符取 hwndCaret，UIA 取元素 NativeWindowHandle 或焦点 HWND），
    /// 供角标窗按目标显示器 DPI 换算物理尺寸。
    /// </param>
    /// <param name="Source">最终采用的坐标来源；Caret=null 时为 None。</param>
    /// <param name="Diagnostic">
    /// 本次探测的纯诊断数据（原因/来源/进程/模式可用性等）。不含任何输入内容。
    /// 通过 <see cref="CaretLocator.TryCaptureFocusSnapshot(IntPtr, out ImeProbeDiagnostic)"/>
    /// 获取时总会填充；旧的四参构造路径允许为 null。
    /// </param>
    internal sealed record ImeFocusSnapshot(
        ImeFocusIdentity Identity,
        bool IsEditable,
        Rectangle? Caret,
        IntPtr DpiHwnd,
        ImeCaretSource Source = ImeCaretSource.None,
        ImeProbeDiagnostic? Diagnostic = null);
}
