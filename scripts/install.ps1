# 安装 Windows Global Launcher 到「当前用户目录」（按用户安装，无需管理员改动系统目录）。
#
# 为什么装到 %LOCALAPPDATA%\Programs（而不是 Program Files）：
#   它是 Windows 上「按用户安装」的约定位置（VS Code、Git 等众多工具默认都装在这里），
#   当前用户对它天然有写权限——程序的自动更新要原地替换自身 exe（UpdateInstaller 会先做
#   写权限预检），装在这里自替换才能成功；装到受系统保护的 Program Files 会因无写权限被
#   更新流程直接拒绝。
#
# 用法（默认参数见 param 声明）：
#   .\Install.ps1                        # 安装到默认目录，配置开机自启并启动程序
#   .\Install.ps1 -NoAutoStart           # 跳过开机自启配置
#   .\Install.ps1 -DesktopShortcut       # 额外在桌面创建快捷方式
#   .\Install.ps1 -NoLaunch              # 安装完成后不启动程序
#   .\Install.ps1 -Source <目录> -Dest <目录>   # 自定义源目录与安装目录
param(
    # 源目录（需包含 WindowsGlobalLauncher.exe）；release zip 解压后 Install.ps1 与 exe 同层，
    # 因此默认取脚本自身所在目录即可。
    [string]$Source = $PSScriptRoot,
    # 安装目录：%LOCALAPPDATA%\Programs\WindowsGlobalLauncher（理由见文件头部注释）。
    [string]$Dest = "$env:LOCALAPPDATA\Programs\WindowsGlobalLauncher",
    # 默认配置开机自启；加此开关则跳过。
    [switch]$NoAutoStart,
    # 默认不创建桌面快捷方式；加此开关则在桌面也建一个。
    [switch]$DesktopShortcut,
    # 默认安装完成后启动程序；加此开关则不启动。
    [switch]$NoLaunch
)

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
    Write-Host '================================================'
    Write-Host '  Windows Global Launcher 安装程序'
    Write-Host '================================================'
    Write-Host ''

    # 1. 校验源目录中的程序文件是否存在。
    #    先规范化路径（展开相对路径、去掉末尾斜杠等），兼作下面「Source 与 Dest 同目录」的比较基准。
    $Source = [System.IO.Path]::GetFullPath($Source)
    $Dest   = [System.IO.Path]::GetFullPath($Dest)
    $sourceExe = Join-Path $Source 'WindowsGlobalLauncher.exe'
    $destExe   = Join-Path $Dest 'WindowsGlobalLauncher.exe'
    if (-not (Test-Path $sourceExe)) {
        throw "未找到程序文件：$sourceExe，请确保安装脚本与 WindowsGlobalLauncher.exe 位于同一目录。"
    }
    Write-Host "安装源：$Source"

    # 2. 检测 .NET 8 桌面运行时。
    #    取 64 位的 Program Files 路径。不能直接用 $env:ProgramFiles：在 32 位 PowerShell 宿主里
    #    （部分环境的 Install.cmd 会命中 SysWOW64\powershell.exe）它指向 "C:\Program Files (x86)"，
    #    而 x64 运行时装在 "C:\Program Files" 下，会误判为未安装并重复下载安装。
    #    $env:ProgramW6432 在 32 位与 64 位宿主中都指向真正的 64 位目录。
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

    # 3. 停止正在运行的实例（否则下面复制 exe 会因文件被占用而失败）。
    $running = Get-Process -Name 'WindowsGlobalLauncher' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host '检测到正在运行的 Windows Global Launcher，正在停止...' -ForegroundColor Yellow
        $running | Stop-Process -Force
        # 等待进程真正退出（最多 10 秒），避免文件仍被占用导致复制失败
        $deadline = (Get-Date).AddSeconds(10)
        do {
            Start-Sleep -Milliseconds 200
            $still = Get-Process -Name 'WindowsGlobalLauncher' -ErrorAction SilentlyContinue
        } while ($still -and (Get-Date) -lt $deadline)
        if ($still) {
            throw '程序仍在运行，无法停止，安装中止。请手动结束后重试。'
        }
        Write-Host '已停止正在运行的实例。'
    }

    # 4. 创建安装目录并复制程序文件。
    #    Source 与 Dest 为同一目录时跳过复制，避免自己覆盖自己报错。
    if ($Source -eq $Dest) {
        Write-Host '源目录与安装目录相同，跳过复制。' -ForegroundColor Yellow
    } else {
        Write-Host "正在复制到安装目录：$Dest"
        New-Item -ItemType Directory -Force -Path $Dest | Out-Null
        Copy-Item -Path $sourceExe -Destination $destExe -Force
        # 若源目录还带着兜底启动脚本与说明，一并复制（存在才复制）
        foreach ($name in @('Start.ps1', 'Start.cmd', 'README-first.txt')) {
            $src = Join-Path $Source $name
            if (Test-Path $src) {
                Copy-Item -Path $src -Destination $Dest -Force
            }
        }
    }

    # 5. 创建开始菜单快捷方式（指向安装后的 exe）。
    $shell = New-Object -ComObject WScript.Shell
    $startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    $startMenuLnk = Join-Path $startMenuDir 'Windows Global Launcher.lnk'
    $lnk = $shell.CreateShortcut($startMenuLnk)
    $lnk.TargetPath = $destExe
    $lnk.WorkingDirectory = $Dest
    $lnk.IconLocation = "$destExe,0"
    $lnk.Description = 'Windows Global Launcher 全局命令启动器'
    $lnk.Save()
    Write-Host "已创建开始菜单快捷方式：$startMenuLnk"

    # -DesktopShortcut 时额外在桌面创建快捷方式
    $desktopLnk = $null
    if ($DesktopShortcut) {
        $desktopDir = [Environment]::GetFolderPath('Desktop')
        $desktopLnk = Join-Path $desktopDir 'Windows Global Launcher.lnk'
        $lnk2 = $shell.CreateShortcut($desktopLnk)
        $lnk2.TargetPath = $destExe
        $lnk2.WorkingDirectory = $Dest
        $lnk2.IconLocation = "$destExe,0"
        $lnk2.Description = 'Windows Global Launcher 全局命令启动器'
        $lnk2.Save()
        Write-Host "已创建桌面快捷方式：$desktopLnk"
    }

    # 6. 配置开机自启（未加 -NoAutoStart 时）。
    #    计划任务的 XML 细节只在 C# 里实现一份（--install-autostart 开关），脚本不重复造轮子，
    #    直接调用程序自身来注册/注销。
    $autostartConfigured = $false
    if ($NoAutoStart) {
        Write-Host '已按 -NoAutoStart 跳过开机自启配置。'
    } else {
        Write-Host '正在配置开机自启（请在弹出的 UAC 提示中点击“是”）...'
        try {
            $proc = Start-Process -FilePath $destExe -ArgumentList '--install-autostart' -Verb RunAs -Wait -PassThru
            if ($proc.ExitCode -eq 0) {
                $autostartConfigured = $true
                Write-Host '开机自启已配置。' -ForegroundColor Green
            } else {
                Write-Host "开机自启配置失败（程序退出码 $($proc.ExitCode)）。" -ForegroundColor Yellow
                Write-Host '  可稍后在程序托盘菜单里勾选“开机自动启动”。' -ForegroundColor Yellow
            }
        } catch {
            # 用户在 UAC 提示中点“否”等场景会抛异常，同样不中止整个安装
            Write-Host ('开机自启配置失败：' + $_.Exception.Message) -ForegroundColor Yellow
            Write-Host '  可稍后在程序托盘菜单里勾选“开机自动启动”。' -ForegroundColor Yellow
        }
    }

    # 7. 启动程序（未加 -NoLaunch 时）。
    if ($NoLaunch) {
        Write-Host '已按 -NoLaunch 跳过启动。'
    } else {
        Write-Host '正在启动 Windows Global Launcher（首次启动请留意 UAC 提示）...'
        Start-Process -FilePath $destExe -Verb RunAs
    }

    # 8. 安装摘要
    Write-Host ''
    Write-Host '安装完成！' -ForegroundColor Green
    Write-Host "  安装目录：$Dest"
    Write-Host "  开始菜单快捷方式：$startMenuLnk"
    if ($DesktopShortcut) {
        Write-Host "  桌面快捷方式：$desktopLnk"
    }
    if ($NoAutoStart) {
        Write-Host '  开机自启：未配置（按 -NoAutoStart 跳过）'
    } elseif ($autostartConfigured) {
        Write-Host '  开机自启：已配置'
    } else {
        Write-Host '  开机自启：未配置成功，可稍后在托盘菜单里勾选“开机自动启动”'
    }
    Write-Host '  热键提示：Ctrl+Shift+I 唤出命令面板'
    Write-Host ''
} catch {
    Write-Host ''
    Write-Host ('错误：' + $_.Exception.Message) -ForegroundColor Red
    Read-Host '按回车键退出'
}
