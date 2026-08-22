$ErrorActionPreference = 'Stop'

# 检测 .NET 8 桌面运行时是否已安装：
# 直接在 WindowsDesktop.App 共享目录下查找名字以 8. 开头的子目录，
# 比 `dotnet --list-runtimes` 更可靠（不依赖 PATH 中是否存在 dotnet）。
function Test-DesktopRuntime8 {
    param([string]$Root)
    if (-not (Test-Path $Root)) { return $false }
    return [bool](Get-ChildItem -Path $Root -Directory | Where-Object { $_.Name -like '8.*' })
}

try {
    # 取 64 位的 Program Files 路径。
    # 不能直接用 $env:ProgramFiles：在 32 位 PowerShell 宿主里（部分环境的 Start.cmd 会命中 SysWOW64\powershell.exe）
    # 它指向 "C:\Program Files (x86)"，而 x64 运行时装在 "C:\Program Files" 下，会误判为未安装并重复下载安装。
    # $env:ProgramW6432 在 32 位与 64 位宿主中都指向真正的 64 位目录。
    $programFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
    $desktopAppRoot = Join-Path $programFiles64 'dotnet\shared\Microsoft.WindowsDesktop.App'

    if (-not (Test-DesktopRuntime8 -Root $desktopAppRoot)) {
        Write-Host '未检测到 .NET 8 桌面运行时，即将下载安装（约 55MB）。' -ForegroundColor Yellow

        # 下载安装包（该地址会 301 重定向到官方下载源）
        $installerUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'
        $installerPath = Join-Path $env:TEMP 'windowsdesktop-runtime-win-x64.exe'
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing

        # 静默安装（/install /quiet /norestart），弹 UAC 提示，请点“是”
        Write-Host '正在静默安装 .NET 8 桌面运行时，请在弹出的 UAC 提示中点击“是”。'
        Start-Process -FilePath $installerPath -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait

        # 删除临时安装包（失败静默，不影响后续流程）
        try { Remove-Item -Path $installerPath -Force -ErrorAction SilentlyContinue } catch {}

        # 复检：安装后仍检测不到则视为失败
        if (-not (Test-DesktopRuntime8 -Root $desktopAppRoot)) {
            throw '安装完成后仍未检测到 .NET 8 桌面运行时，自动安装可能失败。'
        }
        Write-Host '.NET 8 桌面运行时安装完成。' -ForegroundColor Green
    } else {
        Write-Host '已检测到 .NET 8 桌面运行时。'
    }

    # 启动程序（app.manifest 声明 requireAdministrator，需管理员权限，故 -Verb RunAs）
    $exePath = Join-Path $PSScriptRoot 'WindowsGlobalLauncher.exe'
    if (-not (Test-Path $exePath)) {
        throw "未找到程序文件：$exePath，请确保 Start.ps1 与 WindowsGlobalLauncher.exe 位于同一目录。"
    }
    Write-Host '正在启动 Windows Global Launcher（首次启动请留意 UAC 提示）...'
    Start-Process -FilePath $exePath -Verb RunAs
} catch {
    Write-Host ''
    Write-Host ('错误：' + $_.Exception.Message) -ForegroundColor Red
    Write-Host '若自动安装失败，请手动下载安装 .NET 8 桌面运行时：' -ForegroundColor Yellow
    Write-Host '  https://dotnet.microsoft.com/download/dotnet/8.0'
    Write-Host '  （在页面中选择“Desktop Runtime x64”对应版本下载并安装）'
    Read-Host '按回车键退出'
}
