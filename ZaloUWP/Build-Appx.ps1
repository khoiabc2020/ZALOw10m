param(
    [ValidateSet("ARM", "x86", "x64")]
    [string]$Platform = "ARM",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$solutionPath = Join-Path $PSScriptRoot "ZaloUWP.sln"
$projectDir = Join-Path $PSScriptRoot "ZaloUWP"
$pfxPath = Join-Path $projectDir "ZaloUWP_TemporaryKey.pfx"

if (-not (Test-Path -LiteralPath $solutionPath)) {
    throw "Solution not found: $solutionPath"
}
if (-not (Test-Path -LiteralPath $pfxPath)) {
    throw "Signing certificate not found: $pfxPath. Create/select a test certificate in Visual Studio first."
}

$vsWhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
$msbuild = $null
if (Test-Path -LiteralPath $vsWhere) {
    $vsPath = & $vsWhere -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
    if ($vsPath) {
        foreach ($candidate in @(
            (Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"),
            (Join-Path $vsPath "MSBuild\15.0\Bin\MSBuild.exe")
        )) {
            if (Test-Path -LiteralPath $candidate) { $msbuild = $candidate; break }
        }
    }
}
if (-not $msbuild) {
    $command = Get-Command msbuild -ErrorAction SilentlyContinue
    if ($command) { $msbuild = $command.Source }
}
if (-not $msbuild) { throw "MSBuild was not found. Install Visual Studio with UWP build tools." }

$securePassword = Read-Host "Password for the local test signing certificate" -AsSecureString
$passwordPtr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
try {
    $certificatePassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPtr)
    Write-Host "Building signed $Configuration | $Platform AppX..." -ForegroundColor Cyan
    & $msbuild $solutionPath `
        /p:Configuration=$Configuration `
        /p:Platform=$Platform `
        /p:AppxBundle=Never `
        /p:UseDotNetNativeToolchain=false `
        /p:UapAppxPackageBuildMode=SideloadOnly `
        /p:AppxPackageSigningEnabled=true `
        "/p:PackageCertificateKeyFile=$pfxPath" `
        "/p:PackageCertificatePassword=$certificatePassword" `
        /m:1

    if ($LASTEXITCODE -ne 0) { throw "MSBuild failed with exit code $LASTEXITCODE" }
}
finally {
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPtr)
    $certificatePassword = $null
    $securePassword.Dispose()
}

$packageRoot = Join-Path $projectDir "AppPackages"
$package = Get-ChildItem -LiteralPath $packageRoot -Filter "ZaloUWP_*_${Platform}.appx" -File -Recurse |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $package) { throw "Build succeeded, but no ARM AppX was found under $packageRoot" }

$signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin" -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq "x64" } |
    Sort-Object FullName -Descending | Select-Object -First 1
if ($signtool) {
    $verifyOutput = & $signtool.FullName verify /pa /v $package.FullName 2>&1 | Out-String
    if ($verifyOutput -match "No signature found") { throw "The generated AppX is unsigned: $($package.FullName)" }
    if ($LASTEXITCODE -ne 0 -and $verifyOutput -notmatch "root certificate which is not trusted") {
        Write-Warning $verifyOutput
        throw "AppX signature verification failed."
    }
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "Signature is present, but this PC does not trust the self-signed test certificate yet. Install its CER to verify trust."
    }
}
else {
    Write-Warning "signtool.exe was not found; signature presence was not checked."
}

Write-Host "Package ready: $($package.FullName)" -ForegroundColor Green
Write-Host "Architecture: $Platform | Configuration: $Configuration"
