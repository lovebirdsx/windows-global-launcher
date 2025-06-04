# 删除旧的发布目录
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "bin\Release"
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue "dist"

# 编译，该exe会依赖.net 8运行时
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true windows-global-launcher.csproj

# 将编译后的文件移动到发布目录
$publishDir = "dist"
if (-Not (Test-Path -Path $publishDir)) {
    New-Item -ItemType Directory -Path $publishDir | Out-Null
}

# 拷贝exe到发布目录
Copy-Item -Path "bin\Release\net8.0-windows\win-x64\publish\windows-global-launcher.exe" -Destination $publishDir
