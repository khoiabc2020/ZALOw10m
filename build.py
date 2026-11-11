# Unified Build Script for Zalo (Windows Phone 8.1 & Windows 10 Mobile)
import os
import sys

root_dir = os.path.dirname(os.path.abspath(__file__))
build_script = os.path.join(root_dir, "build_wp81.py")

if __name__ == "__main__":
    if os.path.exists(build_script):
        import subprocess
        sys.exit(subprocess.run([sys.executable, build_script] + sys.argv[1:]).returncode)
    else:
        print("[ERROR] build_wp81.py not found.")
        sys.exit(1)
