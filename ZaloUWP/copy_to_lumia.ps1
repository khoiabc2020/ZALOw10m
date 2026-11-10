$shell = New-Object -ComObject Shell.Application
$phone = ($shell.Namespace(17).Items() | Where-Object { $_.Name -like '*phone*' -or $_.Name -like '*Lumia*' -or $_.Name -like '*Windows*' })
if ($phone) {
    $phoneFolder = $phone.GetFolder
    
    $storage = ($phoneFolder.Items() | Where-Object { $_.Name -eq 'Phone' })
    if ($storage) {
        $downloads = ($storage.GetFolder.Items() | Where-Object { $_.Name -eq 'Downloads' })
        if ($downloads) {
            Write-Host 'Copying ZaloUWP_1.1.6.0_ARM.appx to Phone\Downloads...'
            $downloads.GetFolder.CopyHere('D:\zalo\ZaloUWP_1.1.6.0_ARM.appx', 16)
            Start-Sleep -Seconds 3
        }
    }

    $sd = ($phoneFolder.Items() | Where-Object { $_.Name -eq 'SD card' })
    if ($sd) {
        $sddownloads = ($sd.GetFolder.Items() | Where-Object { $_.Name -eq 'Downloads' })
        if ($sddownloads) {
            Write-Host 'Copying ZaloUWP_1.1.6.0_ARM.appx to SD card\Downloads...'
            $sddownloads.GetFolder.CopyHere('D:\zalo\ZaloUWP_1.1.6.0_ARM.appx', 16)
            Start-Sleep -Seconds 3
        }
    }
    Write-Host 'Done copying ZaloUWP_1.1.6.0_ARM.appx to Lumia.'
} else {
    Write-Host 'Phone not found via MTP.'
}
