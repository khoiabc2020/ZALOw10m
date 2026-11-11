import zipfile
import re
import os
import fitz # PyMuPDF

apk_path = r"D:\zalo\android_reference\zalo_base.apk"
out_dir = r"D:\zalo\ZaloWP81\Assets\Icons"
os.makedirs(out_dir, exist_ok=True)

targets = {
    # Search
    "ic_search_clean": ("zds_ic_search_line_24.xml", 24, 24),
    # User / Profile
    "ic_user_clean": ("zds_ic_user_line_24.xml", 24, 24),
    "ic_add_user_clean": ("zds_ic_add_user_line_24.xml", 24, 24),
    "ic_add_member_clean": ("zds_ic_add_member_line_24.xml", 24, 24),
    "ic_group_clean": ("zds_ic_group_line_24.xml", 24, 24),
    # Edit / Rename
    "ic_edit_clean": ("zds_ic_edit_line_24.xml", 24, 24),
    # Board / Notes / Polls / Calendar
    "ic_note_clean": ("zds_ic_note_solid_16.xml", 24, 24),
    "ic_poll_clean": ("zds_ic_poll_line_24.xml", 24, 24),
    "ic_calendar_clean": ("zds_ic_calendar_line_24.xml", 24, 24),
    # Wallpaper
    "ic_wallpaper_clean": ("zds_ic_wallpaper_line_24.xml", 24, 24),
    # Notifications / Bell
    "ic_bell_clean": ("ic_bottom_sheet_menu_on_notification.xml", 24, 24),
    "ic_bell_mute_clean": ("ic_bottom_sheet_menu_off_notification.xml", 24, 24),
    "ic_mute_clean": ("icn_msg_mute.xml", 24, 24),
    # Media & Files
    "ic_photo_clean": ("zds_ic_photo_line_24.xml", 24, 24),
    "ic_file_clean": ("zds_ic_file_line_24.xml", 24, 24),
    "ic_link_clean": ("zds_ic_link_line_24.xml", 24, 24),
    # Actions & Privacy
    "ic_pin_clean": ("zds_ic_pin_solid_16.xml", 24, 24),
    "ic_lock_clean": ("zds_ic_lock_line_24.xml", 24, 24),
    "ic_shield_clean": ("zds_ic_shield_line_24.xml", 24, 24),
    "ic_block_clean": ("zds_ic_user_block_solid_24.xml", 24, 24),
    "ic_trash_clean": ("ic_trash.xml", 24, 24),
    # Chat & Call Actions
    "ic_chat_clean": ("zds_ic_chat_solid_24.xml", 24, 24),
    "ic_call_clean": ("zds_ic_call_solid_24.xml", 24, 24),
    "ic_video_clean": ("zds_ic_video_solid_24.xml", 24, 24),
    # Audio & Voice Playback
    "ic_play_clean": ("zds_ic_play_solid_24.xml", 24, 24),
    "ic_pause_clean": ("zds_ic_pause_solid_24.xml", 24, 24),
    "ic_mic_clean": ("zds_ic_mic_solid_24.xml", 24, 24),
    # Check & Close
    "ic_check_clean": ("zds_ic_check_line_24.xml", 24, 24),
    "ic_check_circle_clean": ("zds_ic_check_circle_solid_24.xml", 24, 24),
    "ic_close_clean": ("zds_ic_close_line_24.xml", 24, 24),
    "ic_close_circle_clean": ("zds_ic_close_circle_solid_24.xml", 24, 24),
    # Navigation & General Controls
    "ic_back_clean": ("zds_ic_arrow_left_line_24.xml", 24, 24),
    "ic_chevron_right_clean": ("zds_ic_chevron_right_line_24.xml", 24, 24),
    "ic_camera_clean": ("zds_ic_camera_line_24.xml", 24, 24),
    "ic_download_clean": ("zds_ic_download_line_24.xml", 24, 24),
    "ic_share_clean": ("zds_ic_share_line_24.xml", 24, 24),
    "ic_refresh_clean": ("zds_ic_retry_line_24.xml", 24, 24),
    "ic_info_clean": ("zds_ic_info_circle_line_24.xml", 24, 24),
}

with zipfile.ZipFile(apk_path, 'r') as z:
    for out_name, (xml_name, def_w, def_h) in targets.items():
        found = [n for n in z.namelist() if n.endswith('/' + xml_name)]
        if not found:
            print("Not found:", xml_name)
            continue
        data = z.read(found[0])
        
        # Extract pathData
        paths = re.findall(rb'pathData="([^"]+)"', data)
        if not paths:
            matches = re.findall(rb'M[0-9\.\,\s\-a-zA-Z]+[zZ]?', data)
            paths = [m.decode('utf-8', errors='ignore') for m in matches if len(m) > 15]
        else:
            paths = [p.decode('utf-8', errors='ignore') for p in paths]
            
        print(f"Extracted {len(paths)} paths for {out_name} from {xml_name}")
        
        vp_w = def_w
        vp_h = def_h
        m_w = re.search(rb'viewportWidth="([0-9\.]+)"', data)
        if m_w: vp_w = float(m_w.group(1))
        m_h = re.search(rb'viewportHeight="([0-9\.]+)"', data)
        if m_h: vp_h = float(m_h.group(1))

        # Generate colors:
        # "" -> dark gray #24282C
        # "_gray" -> neutral gray #72808E
        # "_blue" -> Zalo blue #0088FF
        # "_white" -> pure white #FFFFFF
        # "_red" -> red #E53935
        colors = {
            "": "#24282C",
            "_gray": "#72808E",
            "_blue": "#0088FF",
            "_white": "#FFFFFF",
            "_red": "#E53935"
        }
        
        for suffix, color in colors.items():
            path_tags = "".join(f'<path fill="{color}" d="{p}"/>' for p in paths)
            svg = f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {vp_w} {vp_h}">{path_tags}</svg>'
            
            try:
                doc = fitz.open(stream=svg.encode('utf-8'), filetype='svg')
                pix = doc[0].get_pixmap(dpi=192) # 64x64 or 72x72 crisp rendering
                target_path = os.path.join(out_dir, f"{out_name}{suffix}.png")
                pix.save(target_path)
            except Exception as e:
                print(f"Failed to render {out_name}{suffix}: {e}")

print("All official Zalo icons successfully rendered!")
