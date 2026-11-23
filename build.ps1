param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$Output = "."
)

$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "Optimizer.csproj"
$outputPath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $Output))

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK 8.0.x not found. Install it from https://dotnet.microsoft.com/download/dotnet/8.0"
}

Write-Host "Publishing GameOptimizer ($Configuration / $Runtime)..." -ForegroundColor Cyan

dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $outputPath

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$exe = Join-Path $outputPath "GameOptimizer.exe"
$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Done: $exe ($size MB)" -ForegroundColor Green
