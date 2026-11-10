# Copy package ZaloUWP và Certificate sang Lumia 640 XL qua giao thức MTP (USB)
param(
    [string]$AppxPath = "D:\zalo\ZaloUWP_1.3.7.0_ARM.appx"
)

if (-not (Test-Path $AppxPath)) {
    # Tim file appx moi nhat trong thu muc D:\zalo
    $latest = Get-ChildItem -Path "D:\zalo" -Filter "ZaloUWP*ARM.appx" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latest) {
        $AppxPath = $latest.FullName
    } else {
        Write-Error "Khong tim thay file AppX nao tai $AppxPath"
        exit 1
    }
}

$cerPath = "D:\zalo\ZaloDev.cer"
if (-not (Test-Path $cerPath)) {
    $cerPath = "D:\zalo\ZaloUWP\ZaloUWP\ZaloDev.cer"
}

Write-Host ">>> Chuan bi deploy: $AppxPath" -ForegroundColor Cyan
if (Test-Path $cerPath) {
    Write-Host ">>> Certificate kem theo: $cerPath" -ForegroundColor Cyan
}

$shell = New-Object -ComObject Shell.Application
$phone = $shell.Namespace(17).Items() | Where-Object { $_.Name -like '*phone*' -or $_.Name -like '*Lumia*' -or $_.Name -like '*Windows*' } | Select-Object -First 1
if (-not $phone) {
    Write-Warning "Khong tim thay thiet bi Lumia / Windows Mobile qua cong USB (MTP). Hay kiem tra ket noi cap USB!"
    exit 0
}

Write-Host ">>> Phat hien thiet bi: $($phone.Name)" -ForegroundColor Green

# 1. Copy vao Phone\Downloads
$storage = $phone.GetFolder.Items() | Where-Object { $_.Name -eq 'Phone' -or $_.Name -like '*Internal*' } | Select-Object -First 1
if ($storage) {
    $downloads = $storage.GetFolder.Items() | Where-Object { $_.Name -eq 'Downloads' -or $_.Name -eq 'Tải về' } | Select-Object -First 1
    if ($downloads) {
        Write-Host "Dang sao chep vao Phone\Downloads..." -ForegroundColor Yellow
        $downloads.GetFolder.CopyHere($AppxPath, 16)
        if (Test-Path $cerPath) { $downloads.GetFolder.CopyHere($cerPath, 16) }
        Start-Sleep -Seconds 3
        Write-Host "-> Da chep thanh cong vao Phone\Downloads!" -ForegroundColor Green
    }
}

# 2. Copy vao The nho SD (SD card\Downloads)
$sd = $phone.GetFolder.Items() | Where-Object { $_.Name -eq 'SD card' -or $_.Name -like '*SD*' } | Select-Object -First 1
if ($sd) {
    $sdDownloads = $sd.GetFolder.Items() | Where-Object { $_.Name -eq 'Downloads' -or $_.Name -eq 'Tải về' } | Select-Object -First 1
    if ($sdDownloads) {
        Write-Host "Dang sao chep vao SD card\Downloads..." -ForegroundColor Yellow
        $sdDownloads.GetFolder.CopyHere($AppxPath, 16)
        if (Test-Path $cerPath) { $sdDownloads.GetFolder.CopyHere($cerPath, 16) }
        Start-Sleep -Seconds 3
        Write-Host "-> Da chep thanh cong vao SD card\Downloads!" -ForegroundColor Green
    }
}

Write-Host "`n>>> HOAN TAT DEPLOY!" -ForegroundColor Cyan
Write-Host "Luu y neu chua cai Certificate tren dien thoai:" -ForegroundColor Yellow
Write-Host "1. Vao File Explorer (Tep tin) tren Lumia -> vao Downloads -> cham vao ZaloDev.cer de cai dat vao Root." -ForegroundColor Yellow
Write-Host "2. Cham vao file AppX de cai dat ung dung." -ForegroundColor Yellow
