# 本机联网自检：验证场馆接口契约、时钟对齐与抢约规划
# 用法：powershell -ExecutionPolicy Bypass -File hdu-booking\verify.ps1
#   可选：-Token <token> -OpenId <openid>   同时校验登录凭据

param(
    [string]$Token = "",
    [string]$OpenId = ""
)

$ErrorActionPreference = "Continue"
$root = $PSScriptRoot
$exe = Join-Path $root "publish\HduCourtBooker.SelfTest.exe"

if (-not (Test-Path $exe)) {
    Write-Host "找不到自检工具：$exe" -ForegroundColor Red
    Write-Host "请先运行 build.ps1 生成发布产物。" -ForegroundColor Yellow
    exit 1
}

$logDir = Join-Path $root "recon"
New-Item -ItemType Directory -Force -Path $logDir | Out-Null
$log = Join-Path $logDir ("selftest-{0:yyyyMMdd-HHmmss}.txt" -f (Get-Date))

$args = @()
if ($Token) { $args += @("--token", $Token) }
if ($OpenId) { $args += @("--openid", $OpenId) }

Write-Host "== 运行自检（输出同时保存到 $log）==" -ForegroundColor Cyan
& $exe @args 2>&1 | Tee-Object -FilePath $log
$code = $LASTEXITCODE

Write-Host ""
if ($code -eq 0) {
    Write-Host "自检通过：接口契约与规划逻辑均正常。" -ForegroundColor Green
} else {
    Write-Host "自检发现问题，请把上面的输出发给我。" -ForegroundColor Yellow
}
exit $code
