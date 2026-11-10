# Script tự động Build, Patch VCLibs, Đóng gói, Ký số và Deploy Zalo UWP cho Windows 10 Mobile ARM
import os
import sys
import shutil
import subprocess
import zipfile
import re

ROOT_DIR = r"d:\zalo"
PROJECT_DIR = os.path.join(ROOT_DIR, "ZaloUWP", "ZaloUWP")
CSPROJ_PATH = os.path.join(PROJECT_DIR, "ZaloUWP.csproj")
MANIFEST_PATH = os.path.join(PROJECT_DIR, "Package.appxmanifest")
MSBUILD_PATH = r"D:\VS\MSBuild\Current\Bin\MSBuild.exe"
MAKEAPPX_PATH = r"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
SIGNTOOL_PATH = r"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
CERT_PATH = os.path.join(PROJECT_DIR, "ZaloUWP_TemporaryKey.pfx")
CERT_PASS = "123456"

def get_app_version():
    with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
        content = f.read()
    match = re.search(r'Version="([0-9\.]+)"', content)
    if match:
        return match.group(1)
    return "1.2.3.0"

def check_splash_screen():
    splash_path = os.path.join(PROJECT_DIR, "Assets", "SplashScreen.png")
    backup_splash = os.path.join(PROJECT_DIR, "bin", "x64", "Release", "Assets", "SplashScreen.png")
    if os.path.exists(splash_path):
        try:
            from PIL import Image
            with Image.open(splash_path) as im:
                if im.size != (620, 300):
                    print(f"[WARN] Assets/SplashScreen.png size {im.size} != (620, 300). Restoring 620x300...")
                    if os.path.exists(backup_splash):
                        shutil.copyfile(backup_splash, splash_path)
                    else:
                        resized = im.resize((620, 300), Image.Resampling.LANCZOS)
                        resized.save(splash_path, 'PNG')
                    print("[OK] Restored valid 620x300 SplashScreen.png.")
        except Exception as e:
            print("[WARN] SplashScreen check error:", e)

def build():
    check_splash_screen()
    version = get_app_version()
    print(f"=== 1. Building ZaloUWP v{version} (ARM Release) ===")
    cmd = [
        MSBUILD_PATH,
        CSPROJ_PATH,
        "/p:Configuration=Release",
        "/p:Platform=ARM",
        "/p:AppxBundle=Never",
        "/p:UseDotNetNativeToolchain=false",
        "/p:AppxPackageSigningEnabled=true",
        f"/p:PackageCertificateKeyFile={CERT_PATH}",
        f"/p:PackageCertificatePassword={CERT_PASS}",
        "/m:1",
        "/v:m"
    ]
    res = subprocess.run(cmd)
    if res.returncode != 0:
        print("[ERROR] MSBuild failed.")
        sys.exit(1)
    print("[OK] MSBuild build succeeded.")
    return version

def patch_and_repack(version):
    print("=== 2. Patching VCLibs & CoreRuntime & Repacking ===")
    test_dir = os.path.join(PROJECT_DIR, "AppPackages", f"ZaloUWP_{version}_ARM_Test")
    raw_appx = os.path.join(test_dir, f"ZaloUWP_{version}_ARM.appx")
    
    if not os.path.exists(raw_appx):
        print(f"[ERROR] Source appx not found: {raw_appx}")
        sys.exit(1)
        
    temp_dir = os.path.join(ROOT_DIR, f"temp_repack_{version}")
    if os.path.exists(temp_dir):
        shutil.rmtree(temp_dir)
    os.makedirs(temp_dir)

    print(f"Extracting {raw_appx}...")
    with zipfile.ZipFile(raw_appx, 'r') as z:
        z.extractall(temp_dir)

    # Patch AppxManifest.xml
    pkg_manifest = os.path.join(temp_dir, "AppxManifest.xml")
    with open(pkg_manifest, "r", encoding="utf-8") as f:
        manifest_text = f.read()

    # 1. Patch VCLibs MinVersion to 14.0.22929.0
    manifest_text = re.sub(r'Name="Microsoft\.VCLibs\.140\.00"\s+MinVersion="14\.0\.\d+\.\d+"', 'Name="Microsoft.VCLibs.140.00" MinVersion="14.0.22929.0"', manifest_text)

    # 2. Patch MaxVersionTested to 10.0.15254.0 (Windows 10 Mobile 1709 max build)
    manifest_text = re.sub(r'MaxVersionTested="10\.0\.\d+\.\d+"', 'MaxVersionTested="10.0.15254.0"', manifest_text)

    # 3. Ensure Microsoft.NET.CoreRuntime.1.1 is in Dependencies
    if 'Microsoft.NET.CoreRuntime.1.1' not in manifest_text:
        core_dep = '<PackageDependency Name="Microsoft.NET.CoreRuntime.1.1" MinVersion="1.1.27004.0" Publisher="CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US" />'
        manifest_text = manifest_text.replace('</Dependencies>', '    ' + core_dep + '\n  </Dependencies>')

    with open(pkg_manifest, "w", encoding="utf-8") as f:
        f.write(manifest_text)

    # Delete signature and blockmap so makeappx recalculates them cleanly
    for meta_file in ["[Content_Types].xml", "AppxBlockMap.xml", "AppxSignature.p7x"]:
        p = os.path.join(temp_dir, meta_file)
        if os.path.exists(p):
            os.remove(p)

    out_appx = os.path.join(ROOT_DIR, f"ZaloUWP_{version}_ARM.appx")
    print(f"Packing to {out_appx}...")
    res_pack = subprocess.run([
        MAKEAPPX_PATH, "pack", "/d", temp_dir, "/p", out_appx, "/l", "/h", "SHA256", "/o"
    ], capture_output=True, text=True)
    if res_pack.returncode != 0:
        print("[ERROR] makeappx failed:\n", res_pack.stdout, res_pack.stderr)
        sys.exit(1)

    print(f"Signing AppX with {CERT_PATH} (Subject: CN=ZaloDev)...")
    res_sign = subprocess.run([
        SIGNTOOL_PATH, "sign", "/fd", "SHA256", "/a", "/f", CERT_PATH, "/p", CERT_PASS, out_appx
    ], capture_output=True, text=True)
    if res_sign.returncode != 0:
        print("[ERROR] signtool failed:", res_sign.stderr)
        sys.exit(1)

    # Also overwrite the one in test_dir so Device Portal or local script sees the signed & patched package
    shutil.copyfile(out_appx, raw_appx)

    # Clean temp dir
    if os.path.exists(temp_dir):
        shutil.rmtree(temp_dir)

    print(f"[OK] Successfully built, patched VCLibs & signed: {out_appx} ({os.path.getsize(out_appx):,} bytes)")

    # Verify signature
    print("Verifying signature with signtool...")
    res_verify = subprocess.run([SIGNTOOL_PATH, "verify", "/pa", "/v", out_appx], capture_output=True, text=True)
    if "Signing Certificate Chain" in res_verify.stdout:
        print("[OK] Verified signature successfully! Subject: CN=ZaloDev")
    else:
        print("[WARN] Verify output:", res_verify.stdout, res_verify.stderr)

    return out_appx

def deploy(appx_path):
    print("=== 3. Deploying to Lumia 640 XL via MTP ===")
    version = get_app_version()
    cer_path = os.path.join(PROJECT_DIR, f"ZaloDev.cer")
    if not os.path.exists(cer_path):
        cer_path = os.path.join(PROJECT_DIR, f"ZaloDev.cer")

    ps_cmd = f"""
    $shell = New-Object -ComObject Shell.Application
    $phone = $shell.Namespace(17).Items() | Where-Object {{ $_.Name -like '*phone*' -or $_.Name -like '*Lumia*' -or $_.Name -like '*Windows*' }} | Select-Object -First 1
    if (-not $phone) {{
        Write-Host "No MTP phone found."
        exit 0
    }}
    Write-Host "Found device: " $phone.Name
    $appxPath = "{appx_path}"
    $cerPath = "{cer_path}"

    $storage = $phone.GetFolder.Items() | Where-Object {{ $_.Name -eq 'Phone' -or $_.Name -like '*Internal*' }} | Select-Object -First 1
    if ($storage) {{
        $downloads = $storage.GetFolder.Items() | Where-Object {{ $_.Name -eq 'Downloads' -or $_.Name -eq 'Tải về' }} | Select-Object -First 1
        if ($downloads) {{
            Write-Host "Copying AppX & CER to Phone\\Downloads..."
            $downloads.GetFolder.CopyHere($appxPath, 16)
            if (Test-Path $cerPath) {{ $downloads.GetFolder.CopyHere($cerPath, 16) }}
            Start-Sleep -Seconds 3
        }}
    }}

    $sd = $phone.GetFolder.Items() | Where-Object {{ $_.Name -eq 'SD card' -or $_.Name -like '*SD*' }} | Select-Object -First 1
    if ($sd) {{
        $sdDownloads = $sd.GetFolder.Items() | Where-Object {{ $_.Name -eq 'Downloads' -or $_.Name -eq 'Tải về' }} | Select-Object -First 1
        if ($sdDownloads) {{
            Write-Host "Copying AppX & CER to SD card\\Downloads..."
            $sdDownloads.GetFolder.CopyHere($appxPath, 16)
            if (Test-Path $cerPath) {{ $sdDownloads.GetFolder.CopyHere($cerPath, 16) }}
            Start-Sleep -Seconds 3
        }}
    }}
    Write-Host "Deploy completed!"
    """
    res = subprocess.run(["powershell", "-NoProfile", "-Command", ps_cmd], capture_output=True, text=True)
    print(res.stdout)
    if res.stderr:
        print(res.stderr)

if __name__ == "__main__":
    with_deploy = "--deploy" in sys.argv or "--deploy-only" in sys.argv
    only_deploy = "--deploy-only" in sys.argv
    version = get_app_version()
    appx_path = os.path.join(ROOT_DIR, f"ZaloUWP_{version}_ARM.appx")

    if not only_deploy:
        build()
        appx_path = patch_and_repack(version)
    
    if with_deploy:
        deploy(appx_path)
    else:
        print(f"=== Sẵn sàng cài đặt qua Windows Device Portal ===")
        print(f"Gói cài đặt (.appx): {appx_path}")
        print(f"Thư mục test: {os.path.join(PROJECT_DIR, 'AppPackages', f'ZaloUWP_{version}_ARM_Test')}")
        print(f"Chứng chỉ (.cer): {os.path.join(ROOT_DIR, 'ZaloDev.cer')}")
        print(f"Dependency runtime: {os.path.join(ROOT_DIR, 'Microsoft.NET.CoreRuntime.1.1.appx')}")
