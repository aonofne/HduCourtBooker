# 需要 .NET 8 SDK
# 产物：publish-lite\（框架依赖，约 0.3 MB，推荐）与 publish\（自包含单文件，约 63 MB）
# 用法：powershell -ExecutionPolicy Bypass -File build.ps1
#      powershell -ExecutionPolicy Bypass -File build.ps1 -SkipTests
#      powershell -ExecutionPolicy Bypass -File build.ps1 -LiteOnly

param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [switch]$SkipTests,
    [switch]$LiteOnly
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot                      # 仓库根（本文件所在目录）

# 优先使用上级目录里的本地 SDK（开发环境：<parent>\tools\dotnet）；
# 找不到就退回系统安装的 dotnet —— 克隆本仓库的人只要装了 .NET 8 SDK 即可构建。
$dotnetRoot = Join-Path (Split-Path -Parent $root) "tools\dotnet"
if (-not (Test-Path (Join-Path $dotnetRoot "dotnet.exe"))) {
    $sys = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($sys) {
        $dotnetRoot = Split-Path -Parent $sys.Source
    } else {
        throw "找不到 .NET SDK：本地无 $dotnetRoot\dotnet.exe，PATH 里也没有 dotnet。请先安装 .NET 8 SDK。"
    }
}

$env:DOTNET_ROOT = $dotnetRoot
$env:PATH = "$dotnetRoot;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# 构建缓存放在上级目录（有 tools 时），否则用系统默认位置
$cacheBase = Split-Path -Parent $root
if (Test-Path (Join-Path $cacheBase "tools")) {
    $env:DOTNET_CLI_HOME = Join-Path $cacheBase "tools\dotnet-home"
    $env:APPDATA = Join-Path $cacheBase "tools\appdata"
    $env:NUGET_PACKAGES = Join-Path $cacheBase "tools\nuget-packages"
    New-Item -ItemType Directory -Force -Path $env:DOTNET_CLI_HOME, $env:APPDATA | Out-Null
}

$dotnet = Join-Path $dotnetRoot "dotnet.exe"
$proj = Join-Path $root "src\HduCourtBooker\HduCourtBooker.csproj"
$tests = Join-Path $root "src\HduCourtBooker.Tests\HduCourtBooker.Tests.csproj"
$itest = Join-Path $root "src\HduCourtBooker.IntegrationTest\HduCourtBooker.IntegrationTest.csproj"
$selftest = Join-Path $root "src\HduCourtBooker.SelfTest\HduCourtBooker.SelfTest.csproj"
$fullDir = Join-Path $root "publish"
$liteDir = Join-Path $root "publish-lite"
$singleFile = @(
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none"
)

if (-not $SkipTests) {
    Write-Host "== 1/5 离线单元测试 ==" -ForegroundColor Cyan
    & $dotnet build $tests -c $Configuration -v q --nologo
    & $dotnet (Join-Path $root "src\HduCourtBooker.Tests\bin\$Configuration\net8.0-windows\HduCourtBooker.Tests.dll")
    if ($LASTEXITCODE -ne 0) { throw "单元测试未通过" }

    Write-Host "== 2/5 端到端集成测试（本地仿真服务端）==" -ForegroundColor Cyan
    & $dotnet build $itest -c $Configuration -v q --nologo
    & $dotnet (Join-Path $root "src\HduCourtBooker.IntegrationTest\bin\$Configuration\net8.0-windows\HduCourtBooker.IntegrationTest.dll") |
        Where-Object { $_ -notmatch '\[stub\]|\[http\]|\[diag\]' }
    if ($LASTEXITCODE -ne 0) { throw "集成测试未通过" }
} else {
    Write-Host "== 跳过测试（-SkipTests）==" -ForegroundColor Yellow
}

# ---------------- 框架依赖版（小体积，推荐） ----------------
Write-Host "== 3/5 发布框架依赖版 publish-lite ==" -ForegroundColor Cyan
if (Test-Path $liteDir) { Remove-Item $liteDir -Recurse -Force }
& $dotnet publish $proj -c $Configuration -r $Runtime --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -p:RollForward=LatestMinor `
    -o $liteDir --nologo -v q
Copy-Item (Join-Path $root "启动.bat") $liteDir -Force

# ---------------- 自包含单文件（大体积，免装 .NET） ----------------
if (-not $LiteOnly) {
    Write-Host "== 4/5 发布自包含版 publish ==" -ForegroundColor Cyan
    if (Test-Path $fullDir) { Remove-Item $fullDir -Recurse -Force }
    & $dotnet publish $proj -c $Configuration -r $Runtime --self-contained true @singleFile -o $fullDir --nologo -v q
} else {
    Write-Host "== 跳过自包含版（-LiteOnly）==" -ForegroundColor Yellow
}

# ---------------- 自检工具 ----------------
Write-Host "== 5/5 发布自检工具 ==" -ForegroundColor Cyan
$tmpSt = Join-Path $root "publish-selftest"
if (Test-Path $tmpSt) { Remove-Item $tmpSt -Recurse -Force }
& $dotnet publish $selftest -c $Configuration -r $Runtime --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=none -p:RollForward=LatestMinor `
    -o $tmpSt --nologo -v q
Copy-Item (Join-Path $tmpSt "HduCourtBooker.SelfTest.exe") $liteDir -Force
Remove-Item $tmpSt -Recurse -Force

Write-Host ""
Write-Host "发布完成" -ForegroundColor Green
foreach ($d in @($liteDir, $fullDir)) {
    if (-not (Test-Path $d)) { continue }
    Write-Host "`n$d" -ForegroundColor Cyan
    Get-ChildItem $d | Select-Object Name, @{n = "MB"; e = { [math]::Round($_.Length / 1MB, 2) } } | Format-Table -AutoSize
}
