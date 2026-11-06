# 构建 SOLARA 单文件 exe
$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Push-Location (Join-Path $root "app")
try {
    Write-Host "==> rebuild" -ForegroundColor Cyan
    dotnet build -c Release -t:Rebuild

    Write-Host "==> publish" -ForegroundColor Cyan
    dotnet publish -c Release -r win-x64 --self-contained true -o dist --no-build

    $exe = Join-Path (Get-Location) "dist\SOLARA.exe"
    if (Test-Path $exe) {
        $mb = [math]::Round((Get-Item $exe).Length / 1MB, 1)
        Write-Host "==> done: $exe ($mb MB)" -ForegroundColor Green
    } else {
        throw "publish 失败，没有找到 dist\SOLARA.exe"
    }
}
finally {
    Pop-Location
}
