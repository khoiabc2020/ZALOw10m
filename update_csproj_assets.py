import os
import re

def sync_csproj_assets(project_dir, csproj_name):
    csproj_path = os.path.join(project_dir, csproj_name)
    assets_dir = os.path.join(project_dir, "Assets")
    if not os.path.exists(csproj_path) or not os.path.exists(assets_dir):
        print(f"Skipping {csproj_path}")
        return

    with open(csproj_path, "r", encoding="utf-8") as f:
        content = f.read()

    # Collect all asset files relative to project_dir
    asset_files = []
    for root, dirs, files in os.walk(assets_dir):
        for f in files:
            full = os.path.join(root, f)
            rel = os.path.relpath(full, project_dir)
            asset_files.append(rel)

    # Filter out files already in csproj
    missing = []
    for rel in asset_files:
        if f'Include="{rel}"' not in content:
            missing.append(rel)

    print(f"[{csproj_name}] Total assets: {len(asset_files)}, Missing in csproj: {len(missing)}")

    if not missing:
        print(f"[{csproj_name}] All assets already included.")
        return

    # Create new ItemGroup for assets
    items = []
    for rel in sorted(missing):
        items.append(f'    <Content Include="{rel}" />')
    new_group = "\n  <ItemGroup>\n" + "\n".join(items) + "\n  </ItemGroup>\n"

    # Insert before </Project>
    content = content.replace("</Project>", new_group + "</Project>")

    with open(csproj_path, "w", encoding="utf-8") as f:
        f.write(content)

    print(f"[{csproj_name}] Successfully added {len(missing)} assets to csproj.")

sync_csproj_assets(r"D:\zalo\ZaloWP81", "ZaloWP81.csproj")
sync_csproj_assets(r"D:\zalo\ZaloUWP\ZaloUWP", "ZaloUWP.csproj")
