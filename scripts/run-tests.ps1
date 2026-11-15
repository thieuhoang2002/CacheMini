# Chạy toàn bộ unit test của CacheMini.
#
# Vì sao không dùng thẳng `dotnet test`?
# Trong workspace này (do môi trường sandbox gắn nhãn integrity/ACL lên thư mục), tiến trình
# `testhost.exe` nằm trong `tests/**/bin` chết ngay khi khởi động với mã 0xC0000142
# (STATUS_DLL_INIT_FAILED). Cùng file đó copy sang %TEMP% thì chạy bình thường, nên đây là
# vấn đề vị trí thư mục chứ không phải code. Script này build binary test ra %TEMP% rồi chạy
# vstest từ đó — kết quả y hệt `dotnet test`, chỉ khác chỗ đặt output.
#
# Trên máy không có sandbox (ví dụ CI hoặc terminal thường), `dotnet test CacheMini.slnx`
# vẫn dùng được và nên dùng.

param(
    [string]$Configuration = 'Debug',
    [string]$Filter
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'tests/CacheMini.Tests/CacheMini.Tests.csproj'
$out = Join-Path $env:TEMP 'cachemi-test-out'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "Build test binary ra: $out"
& dotnet build $project -c $Configuration -o $out --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "Build thất bại (exit $LASTEXITCODE)." }

$dll = Join-Path $out 'CacheMini.Tests.dll'
$vstestArgs = @($dll, '--logger:console;verbosity=normal')
if ($Filter) { $vstestArgs += "--TestCaseFilter:$Filter" }

& dotnet vstest @vstestArgs
exit $LASTEXITCODE
