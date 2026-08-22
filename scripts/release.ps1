# scripts/release.ps1 —— 一键发版脚本
#
# 用法示例：
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Bump patch
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 1.2.3
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Bump minor -DryRun
#   powershell -NoProfile -ExecutionPolicy Bypass -File scripts\release.ps1 -Version 1.2.3 -SkipTests
#
# 发版契约（与 .github/workflows/release.yml 一致）：
#   版本号唯一真相源是 src/WindowsGlobalLauncher/WindowsGlobalLauncher.csproj 的 <Version>；
#   推送 v1.2.3 形式的 tag 即触发 CI：跑测试、发布单文件 exe、创建 GitHub Release。
#   版本号必须严格三段（^\d+\.\d+\.\d+$）：客户端 UpdateChecker.CompareVersion 只比
#   Major/Minor/Build，放行四段会让 v1.2.3 与 v1.2.3.1 被判为同一版本，更新提示永远不触发。

param(
    # 显式指定新版本号，形如 1.2.3。省略时按 -Bump 基于 csproj 现有版本递增。
    [string]$Version,

    # 未给 -Version 时的递增级别：patch 递增第三段；minor 递增第二段并把第三段归零；major 递增第一段并把后两段归零。
    [ValidateSet('patch', 'minor', 'major')]
    [string]$Bump = 'patch',

    # 只打印将要执行的动作，不做任何写操作（不改文件、不 fetch、不 commit、不 tag、不 push、不跑测试）。
    [switch]$DryRun,

    # 跳过单元测试（默认会运行）。
    [switch]$SkipTests,

    # 默认只允许在 main 分支发版，加此开关放行任意分支。
    [switch]$AllowAnyBranch,

    # 远端名称，默认 origin。
    [string]$Remote = 'origin'
)

$ErrorActionPreference = 'Stop'

# 进度步数（打印 [n/9] 进度用）。
$TotalSteps = 9

# 记录「已完成到哪一步」，供失败时回滚本地改动（取值语义见 Invoke-Rollback 的注释）。
$script:ReleaseProgress = 'Start'

# csproj 相对仓库根的路径（版本号唯一真相源；发布时 CI 用 -p:Version 覆盖，本脚本负责发版前同步它）。
$script:CsprojRel = 'src/WindowsGlobalLauncher/WindowsGlobalLauncher.csproj'

# 仓库地址（仅用于打印后续指引链接，与 .github/workflows 中的仓库一致）。
$script:RepoSlug = 'lovebirdsx/windows-global-launcher'

# 是否已 Push-Location 进入仓库根（用于 finally 里决定是否 Pop-Location）。
$pushed = $false

# ---------- 辅助函数 ----------

# 打印「[n/9] 正在…」进度横幅。
function Write-Step {
    param([int]$Index, [string]$Text)
    Write-Host "[$Index/$TotalSteps] $Text" -ForegroundColor Cyan
}

# 统一执行 git 命令：DryRun 下只打印不执行；执行后检查 $LASTEXITCODE，非 0 即 throw。
# 返回命令输出（供查询类调用方捕获）。
function Invoke-Git {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$Description = ''
    )
    $cmd = 'git ' + ($Arguments -join ' ')
    if ($DryRun) {
        Write-Host "    [DryRun] 跳过：$cmd" -ForegroundColor DarkGray
        return
    }
    if ($Description) { Write-Host "    $Description" -ForegroundColor Gray }
    & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git 命令失败（退出码 $LASTEXITCODE）：$cmd"
    }
}

# 执行只读 git 查询（DryRun 下也照常执行，因为不会修改任何东西），返回输出并检查退出码。
function Invoke-GitRead {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $output = & git @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git 命令失败（退出码 $LASTEXITCODE）：git $($Arguments -join ' ')"
    }
    return $output
}

# 统一执行 dotnet 命令：DryRun 下只打印不执行；执行后检查退出码。
function Invoke-DotNet {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [string]$Description = ''
    )
    $cmd = 'dotnet ' + ($Arguments -join ' ')
    if ($DryRun) {
        Write-Host "    [DryRun] 跳过：$cmd" -ForegroundColor DarkGray
        return
    }
    if ($Description) { Write-Host "    $Description" -ForegroundColor Gray }
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet 命令失败（退出码 $LASTEXITCODE）：$cmd"
    }
}

# 按 $script:ReleaseProgress 回滚本次发版产生的本地改动。
#
# 为什么这样回滚是安全的：
#   - 脚本开始前已校验工作区干净，因此所有本地改动都只来自本脚本自身：
#     'VersionWritten' 阶段用 git checkout -- <csproj> 丢弃文件改动 = 精确还原到启动前状态。
#   - 'Committed' 之后 HEAD 上只有本脚本产生的这一个 commit（此前工作区干净、没有任何别的改动被一起提交），
#     故 HEAD~1 就是启动前状态；git reset --hard HEAD~1 同时还原 index 与工作区。
#   - 本地 tag 只在本脚本内新建（此前已校验 v<ver> 在本地与远端都不存在），git tag -d 删除它不会影响远端。
#   - 一旦分支已 push（'BranchPushed'），commit 已在远端，此时不再回滚本地——否则会让本地与远端不一致；
#     改为仅提示、交给用户手动处理。这正对应「已 push 的内容不回滚，只回滚本地」的原则。
function Invoke-Rollback {
    param([string]$Reason)

    Write-Host ''
    Write-Host ('正在回滚本次发版产生的本地改动（原因：' + $Reason + '）') -ForegroundColor Yellow

    if ($script:ReleaseProgress -eq 'BranchPushed') {
        Write-Host '  分支已推送到远端，本地 commit 与 tag 不回滚（避免本地与远端不一致）。' -ForegroundColor Yellow
        Write-Host ('  如 tag 未推送成功，可手动执行：git push ' + $Remote + ' v' + $script:NewVersion) -ForegroundColor Yellow
        return
    }

    # tag 指向即将被 reset 掉的 commit，必须先删 tag 再 reset。
    if ($script:ReleaseProgress -eq 'Tagged') {
        Invoke-Git @('tag', '-d', ('v' + $script:NewVersion)) -Description ('删除本地 tag v' + $script:NewVersion)
    }
    if ($script:ReleaseProgress -eq 'Committed' -or $script:ReleaseProgress -eq 'Tagged') {
        Invoke-Git @('reset', '--hard', 'HEAD~1') -Description '撤销本次 commit（还原到发版前）'
    }
    if ($script:ReleaseProgress -eq 'VersionWritten') {
        # 必须显式指定 HEAD 作为还原源：'git checkout -- <path>' 是从暂存区恢复工作区，
        # 而这个阶段可能已经执行过 git add（add 成功、commit 失败就会停在 VersionWritten），
        # 那时暂存区里正是要回滚掉的新版本号，不带 HEAD 的写法会把工作区也刷成新版本、
        # 暂存区还残留改动，却照常打印「回滚完成」。带 HEAD 则同时还原暂存区与工作区。
        Invoke-Git @('checkout', 'HEAD', '--', $script:CsprojRel) -Description '还原 csproj 文件改动'
    }
    Write-Host '回滚完成。' -ForegroundColor Green
}

# ---------- 主流程 ----------

try {
    # [1/9] 定位仓库根并校验 git 环境
    Write-Step 1 '定位仓库根并校验 git 环境'
    $repoRoot = Split-Path -Parent $PSScriptRoot
    Push-Location $repoRoot
    $pushed = $true

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        throw '未找到 git 命令，请确认已安装 Git 并加入 PATH。'
    }
    & git rev-parse --is-inside-work-tree 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "当前目录不是 git 仓库：$repoRoot"
    }

    # [2/9] 校验工作区干净与当前分支
    Write-Step 2 '校验工作区与当前分支'
    $statusOutput = Invoke-GitRead @('status', '--porcelain')
    if ($statusOutput) {
        throw '工作区不干净（存在未提交或未暂存的改动）。请先提交或暂存改动后再发版。'
    }

    $script:Branch = (Invoke-GitRead @('rev-parse', '--abbrev-ref', 'HEAD') | Select-Object -First 1)
    if ($script:Branch -ne 'main' -and -not $AllowAnyBranch) {
        throw "当前分支为 $($script:Branch)，默认只允许在 main 上发版。确要在此分支发版请加 -AllowAnyBranch。"
    }
    Write-Host "    当前分支：$($script:Branch)"

    # [3/9] 同步远端并校验本地未落后
    Write-Step 3 '同步远端并校验本地是否落后'
    if ($DryRun) {
        Write-Host '    [DryRun] 跳过 git fetch，无法校验远端领先情况。' -ForegroundColor DarkGray
    } else {
        Invoke-Git @('fetch', $Remote, '--tags') -Description "fetch $Remote --tags（同步远端分支与 tag）"
        $behindCount = [int](Invoke-GitRead @('rev-list', '--count', ("HEAD..$Remote/" + $script:Branch)))
        if ($behindCount -ne 0) {
            throw "本地分支 $($script:Branch) 落后远端 $behindCount 个提交，请先 pull 再发版。"
        }
        Write-Host "    本地分支 $($script:Branch) 未落后远端。"
    }

    # [4/9] 读取 csproj 当前版本并计算新版本
    Write-Step 4 '计算新版本号'
    $csprojPath = Join-Path $repoRoot $script:CsprojRel
    if (-not (Test-Path $csprojPath)) {
        throw "未找到 csproj 文件：$csprojPath"
    }
    $csprojText = [System.IO.File]::ReadAllText($csprojPath)
    $versionMatch = [regex]::Match($csprojText, '<Version>(.*?)</Version>')
    if (-not $versionMatch.Success) {
        throw "在 $($script:CsprojRel) 中未找到 <Version> 节点，无法确定当前版本。"
    }
    $script:OldVersion = $versionMatch.Groups[1].Value.Trim()
    try {
        $oldVersionObj = [version]$script:OldVersion
    } catch {
        throw "csproj 中的版本号 $($script:OldVersion) 不是合法版本号。"
    }

    if ($Version) {
        $script:NewVersion = $Version.Trim()
    } else {
        # 自动递增要求当前版本至少是完整三段（否则 Build 段为 -1，无法递增）。
        if ($oldVersionObj.Build -lt 0) {
            throw "csproj 当前版本 $($script:OldVersion) 不足三段，无法自动递增，请用 -Version 显式指定新版本。"
        }
        $parts = @([int]$oldVersionObj.Major, [int]$oldVersionObj.Minor, [int]$oldVersionObj.Build)
        switch ($Bump) {
            'patch' { $parts[2] = $parts[2] + 1 }
            'minor' { $parts[1] = $parts[1] + 1; $parts[2] = 0 }
            'major' { $parts[0] = $parts[0] + 1; $parts[1] = 0; $parts[2] = 0 }
        }
        $script:NewVersion = ($parts -join '.')
    }

    # 严格三段版本号校验（与 release.yml / UpdateChecker.CompareVersion 口径一致）。
    if ($script:NewVersion -notmatch '^\d+\.\d+\.\d+$') {
        throw "新版本号格式非法：$($script:NewVersion)（应为 1.2.3 这样的三段版本号）"
    }
    $newVersionObj = [version]$script:NewVersion
    if ($newVersionObj.CompareTo($oldVersionObj) -le 0) {
        throw "新版本号 $($script:NewVersion) 未大于当前版本 $($script:OldVersion)，无法发版。"
    }
    $script:Tag = 'v' + $script:NewVersion
    Write-Host "    当前版本：$($script:OldVersion) -> 新版本：$($script:NewVersion)（tag：$($script:Tag)）"

    # [5/9] 校验 tag 在本地与远端都不存在
    Write-Step 5 '校验 tag 是否可用'
    $localTag = Invoke-GitRead @('tag', '-l', $script:Tag)
    if ($localTag) {
        throw "本地已存在 tag $($script:Tag)，请先删除或更换版本号。"
    }
    if ($DryRun) {
        Write-Host '    [DryRun] 跳过远端 tag 存在性检查（需要联网）。' -ForegroundColor DarkGray
    } else {
        # ls-remote 输出形如「<sha>\trefs/tags/v1.0.1」或「<sha>\trefs/tags/v1.0.1^{}」，
        # 用精确正则（含结尾锚点）避免 v1.0.1 误匹配 v1.0.10。
        $tagRefPattern = 'refs/tags/' + [regex]::Escape($script:Tag) + '(\^\{\})?$'
        $remoteTags = Invoke-GitRead @('ls-remote', '--tags', $Remote)
        foreach ($line in $remoteTags) {
            if ($line -match $tagRefPattern) {
                throw "远端 $Remote 已存在 tag $($script:Tag)，请先删除或更换版本号。"
            }
        }
    }
    Write-Host "    tag $($script:Tag) 在本地与远端均不存在。"

    # [6/9] 运行单元测试
    Write-Step 6 '运行单元测试'
    if ($SkipTests) {
        Write-Host '    [跳过] 已指定 -SkipTests，跳过单元测试。' -ForegroundColor Gray
    } else {
        Invoke-DotNet @('test', 'WindowsGlobalLauncher.sln', '-c', 'Release') -Description 'dotnet test WindowsGlobalLauncher.sln -c Release'
    }

    # [7/9] 写回 csproj 的 <Version>
    Write-Step 7 '写回 csproj 版本号'
    # 重新读取一次（拿到 dotnet test 之后的最新内容），只替换第一个 <Version> 节点的内容，其余内容不变。
    # 用 ReadAllText/WriteAllText 显式配 UTF8 无 BOM，避免 PowerShell 默认编码（带 BOM / 系统 ANSI）把文件写坏。
    $csprojText = [System.IO.File]::ReadAllText($csprojPath)
    $versionMatch = [regex]::Match($csprojText, '<Version>(.*?)</Version>')
    if (-not $versionMatch.Success) {
        throw "在 $($script:CsprojRel) 中未找到 <Version> 节点（写回前复检失败）。"
    }
    $newCsprojText = $csprojText.Substring(0, $versionMatch.Groups[1].Index) +
                     $script:NewVersion +
                     $csprojText.Substring($versionMatch.Groups[1].Index + $versionMatch.Groups[1].Length)

    if ($DryRun) {
        Write-Host ("    [DryRun] 跳过写回 csproj（将把 <Version> 从 " + $script:OldVersion + " 改为 " + $script:NewVersion + "）。") -ForegroundColor DarkGray
    } else {
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($csprojPath, $newCsprojText, $utf8NoBom)
        $script:ReleaseProgress = 'VersionWritten'
        Write-Host "    已把 <Version> 写为 $($script:NewVersion)。"
    }

    # [8/9] commit + 打 tag + push
    Write-Step 8 '提交、打 tag 并推送'
    Invoke-Git @('add', $script:CsprojRel) -Description '暂存 csproj 改动'
    Invoke-Git @('commit', '-m', ('chore: release v' + $script:NewVersion)) -Description ('提交：chore: release v' + $script:NewVersion)
    if (-not $DryRun) { $script:ReleaseProgress = 'Committed' }
    Invoke-Git @('tag', '-a', $script:Tag, '-m', $script:Tag) -Description ('打本地 tag ' + $script:Tag)
    if (-not $DryRun) { $script:ReleaseProgress = 'Tagged' }
    Invoke-Git @('push', $Remote, $script:Branch) -Description ('推送分支 ' + $script:Branch + ' 到 ' + $Remote)
    if (-not $DryRun) { $script:ReleaseProgress = 'BranchPushed' }
    Invoke-Git @('push', $Remote, $script:Tag) -Description ('推送 tag ' + $script:Tag + ' 到 ' + $Remote)

    # [9/9] 打印后续指引
    Write-Step 9 '完成'
    Write-Host ''
    if ($DryRun) {
        Write-Host '[DryRun] 以上为将要执行的动作，未做任何实际改动。' -ForegroundColor Yellow
    } else {
        Write-Host ('已推送 tag ' + $script:Tag + '，GitHub Actions 将自动运行测试、发布单文件 exe 并创建 Release。') -ForegroundColor Green
        Write-Host ('查看流水线进度：https://github.com/' + $script:RepoSlug + '/actions')
        Write-Host ('查看 Release：https://github.com/' + $script:RepoSlug + '/releases/tag/' + $script:Tag)
    }
}
catch {
    Write-Host ''
    Write-Host ('发版失败：' + $_.Exception.Message) -ForegroundColor Red
    if ($script:ReleaseProgress -ne 'Start' -and -not $DryRun) {
        try {
            Invoke-Rollback -Reason $_.Exception.Message
        } catch {
            Write-Host ('回滚也失败了，请手动检查 git 状态：' + $_.Exception.Message) -ForegroundColor Red
        }
    }
    exit 1
}
finally {
    if ($pushed) { Pop-Location }
}
