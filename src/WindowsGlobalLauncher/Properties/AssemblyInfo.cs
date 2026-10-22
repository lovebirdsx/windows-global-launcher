using System.Runtime.CompilerServices;

// 单元测试需要访问 ImeFocusSessionPolicy / ImeProbeWorker 等 internal 编排类型。
[assembly: InternalsVisibleTo("WindowsGlobalLauncher.Tests")]
