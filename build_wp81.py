# Script tự động Build, Đóng gói và Ký số Zalo cho Windows Phone 8.1 ARM
import os
import sys
import shutil
import subprocess
import re

ROOT_DIR = os.path.dirname(os.path.abspath(__file__))
PROJECT_DIR = os.path.join(ROOT_DIR, "ZaloWP81")
CSPROJ_PATH = os.path.join(PROJECT_DIR, "ZaloWP81.csproj")
MANIFEST_PATH = os.path.join(PROJECT_DIR, "Package.appxmanifest")
ASSETS_PATH = os.path.join(PROJECT_DIR, "Assets")
MSBUILD_PATH = r"D:\VS\MSBuild\Current\Bin\MSBuild.exe"
MAKEAPPX_PATH = r"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\makeappx.exe"
MAKEPRI_81_PATH = r"C:\Program Files (x86)\Windows Kits\8.1\bin\x64\makepri.exe"
SIGNTOOL_PATH = r"C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe"
CERT_PATH = os.path.join(ROOT_DIR, "ZaloDev.pfx")
CERT_PASS = "123456"

def bump_version():
    if not os.path.exists(MANIFEST_PATH):
        return "1.0.1.0"
    with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
        content = f.read()
    match = re.search(r'Version="(\d+)\.(\d+)\.(\d+)\.(\d+)"', content)
    if match:
        major, minor, build_num, rev = match.groups()
        new_build = int(build_num) + 1
        new_version = f"{major}.{minor}.{new_build}.{rev}"
        new_content = re.sub(r'Version="\d+\.\d+\.\d+\.\d+"', f'Version="{new_version}"', content)
        with open(MANIFEST_PATH, "w", encoding="utf-8") as f:
            f.write(new_content)
        print(f"[VERSION] Auto-bumped version: {match.group(0)} -> Version=\"{new_version}\"")
        return new_version
    return "1.0.1.0"

def get_app_version():
    if not os.path.exists(MANIFEST_PATH):
        return "1.0.0.0"
    with open(MANIFEST_PATH, "r", encoding="utf-8") as f:
        content = f.read()
    match = re.search(r'Version="([0-9\.]+)"', content)
    if match:
        return match.group(1)
    return "1.0.0.0"

def build(auto_bump=True):
    version = bump_version() if auto_bump else get_app_version()
    print(f"=== 1. Building ZaloWP81 v{version} (ARM Release) ===")
    cmd = [
        MSBUILD_PATH,
        CSPROJ_PATH,
        "/p:Configuration=Release",
        "/p:Platform=ARM",
        "/m:1",
        "/v:m"
    ]
    res = subprocess.run(cmd)
    if res.returncode != 0:
        print("[ERROR] MSBuild failed.")
        sys.exit(1)
    print("[OK] MSBuild build succeeded.")
    return version

def pack_and_sign(version):
    print("=== 2. Packaging & Signing AppX for Windows Phone 8.1 ===")
    out_dir = os.path.join(PROJECT_DIR, "bin", "ARM", "Release")
    out_appx = os.path.join(ROOT_DIR, f"ZaloWP81_{version}_ARM.appx")
    
    # 2.1 Copy AppxManifest.xml
    manifest_dest = os.path.join(out_dir, "AppxManifest.xml")
    print(f"Copying {MANIFEST_PATH} -> {manifest_dest}...")
    shutil.copyfile(MANIFEST_PATH, manifest_dest)

    # 2.2 Copy Assets
    assets_dest = os.path.join(out_dir, "Assets")
    if os.path.exists(assets_dest):
        shutil.rmtree(assets_dest)
    print(f"Copying Assets -> {assets_dest}...")
    shutil.copytree(ASSETS_PATH, assets_dest)

    # 2.3 Generate resources.pri using makepri.exe
    priconfig_path = os.path.join(PROJECT_DIR, "priconfig.xml")
    if not os.path.exists(priconfig_path):
        res_cfg = subprocess.run([
            MAKEPRI_81_PATH, "createconfig", "/cf", priconfig_path, "/dq", "vi-VN", "/o"
        ], capture_output=True, text=True)
        print("Create PRI config:", res_cfg.stdout)

    # Delete any stale PRI files before indexing
    for f in os.listdir(out_dir):
        if f.endswith(".pri"):
            try:
                os.remove(os.path.join(out_dir, f))
            except:
                pass

    pri_dest = os.path.join(out_dir, "resources.pri")
    print(f"Generating {pri_dest}...")
    res_pri = subprocess.run([
        MAKEPRI_81_PATH, "new",
        "/pr", out_dir,
        "/cf", priconfig_path,
        "/mn", manifest_dest,
        "/of", pri_dest,
        "/o"
    ], capture_output=True, text=True)
    if res_pri.returncode != 0:
        print("[ERROR] makepri failed:\n", res_pri.stdout, res_pri.stderr)
        sys.exit(1)
    print("[OK] resources.pri generated successfully.")

    # 2.4 Clean up unwanted Views directory if present
    views_dir = os.path.join(out_dir, "Views")
    if os.path.exists(views_dir):
        shutil.rmtree(views_dir)

    print(f"Packing {out_dir} -> {out_appx}...")
    res_pack = subprocess.run([
        MAKEAPPX_PATH, "pack", "/d", out_dir, "/p", out_appx, "/l", "/h", "SHA256", "/o"
    ], capture_output=True, text=True)
    if res_pack.returncode != 0:
        print("[ERROR] makeappx failed:\n", res_pack.stdout, res_pack.stderr)
        sys.exit(1)
    print("[OK] AppX packaged successfully.")

    if os.path.exists(CERT_PATH):
        print(f"Signing AppX with {CERT_PATH}...")
        res_sign = subprocess.run([
            SIGNTOOL_PATH, "sign", "/fd", "SHA256", "/a", "/f", CERT_PATH, "/p", CERT_PASS, out_appx
        ], capture_output=True, text=True)
        if res_sign.returncode != 0:
            print("[WARN] Signing failed:", res_sign.stderr)
        else:
            print("[OK] Signed successfully.")

    inner_appx = os.path.join(PROJECT_DIR, f"ZaloWP81_{version}_ARM.appx")
    shutil.copyfile(out_appx, inner_appx)
    latest_appx = os.path.join(ROOT_DIR, "ZaloWP81_ARM.appx")
    shutil.copyfile(out_appx, latest_appx)
    print(f"[SUCCESS] Created package: {out_appx}")
    print(f"          Also updated: {inner_appx} and {latest_appx}")
    return out_appx

if __name__ == "__main__":
    ver = build()
    pack_and_sign(ver)
