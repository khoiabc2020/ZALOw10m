# Zalo Native UWP cho Windows 10 Mobile

Ứng dụng Zalo native Universal Windows Platform (UWP) được phát triển riêng cho hệ điều hành **Windows 10 Mobile** (hỗ trợ các thiết bị Lumia như Lumia 950, 950 XL, 930, 830, 730, 640, 640 XL, 550, 520,...) và Windows 10 PC / Tablet.

Dự án được tổ chức theo kiến trúc chuẩn mực của [Unigram](https://github.com/UnigramDev/Unigram) (Telegram client UWP hàng đầu cho Windows 10 Mobile).

---

## 📱 Tính năng nổi bật

### 1. Xác thực & Đăng nhập
* **Quét mã QR**: Tạo và làm mới mã QR trực tiếp từ máy chủ Zalo (`id.zalo.me`), tự động long-polling và đăng nhập ngay khi xác nhận trên điện thoại.
* **Đăng nhập Web In-App**: Sử dụng WebView tích hợp để đăng nhập bằng Số điện thoại + Mật khẩu hoặc mã OTP SMS, tự động nhận diện và bóc tách cookie `zpw_sek`.
* **Đăng nhập Token**: Hỗ trợ dán cookie session `zpw_sek` trực tiếp cho nhà phát triển.
* **Tự động khôi phục phiên**: Duy trì trạng thái đăng nhập qua `LocalSettings`, không phải đăng nhập lại mỗi khi mở app.

### 2. Trò chuyện & Nhắn tin thời gian thực
* **Giao thức kép (REST + WebSocket)**: Đồng bộ danh sách hội thoại và bạn bè qua REST API, duy trì tin nhắn tức thì hai chiều qua WebSocket WSS (`chat-ws.zalo.me`) với nhịp tim Ping keep-alive 25s.
* **Bảo mật AES-CBC**: Mã hóa và giải mã payload theo đúng giao thức bảo mật của Zalo (AES 128/256-bit, Zero IV, PKCS7 Padding).
* **Trích dẫn & Trả lời (Quote / Reply)**: Trả lời tin nhắn với thanh preview trích dẫn trực quan và bong bóng tin nhắn hiển thị quote.
* **Thả cảm xúc (Reactions)**: Nhấn giữ tin nhắn để thả tim (❤️), thích (👍), cười (😆), wow (😮), buồn (😭), phẫn nộ (😡) với âm thanh gốc `like_action.m4a`.
* **Thao tác tin nhắn**: Sao chép văn bản vào Clipboard, thu hồi tin nhắn đã gửi.

### 3. Đa phương tiện chính thức từ Zalo APK
* **30 Sticker động (GIF)**: Bộ nhãn dán chính thức từ Zalo, gửi nhanh qua ngăn kéo 1 chạm.
* **90 Biểu cảm (Emoji)**: Toàn bộ sprite emoji cổ điển của Zalo (`emo_00.png` - `emo_89.png`).
* **Xem ảnh toàn màn hình (`ImageViewerPage`)**: Hỗ trợ cảm ứng đa điểm Pinch-to-zoom (1.0x - 4.0x) và lưu vào Saved Pictures.
* **Gửi hình ảnh & Tài liệu**: Chọn ảnh từ thư viện (`FileOpenPicker`) và gửi tài liệu đa định dạng (`.pdf`, `.docx`, `.xlsx`, `.zip`, `.txt`) hiển thị tên file và dung lượng.
* **Tin nhắn thoại**: Thu âm trực tiếp bằng Microphone qua `Windows.Media.Capture` native sang `.m4a`.
* **Bộ âm thanh gốc**:
  * Nhạc chuông cuộc gọi đến (`zalo_ringtone.ogg`).
  * Âm chờ cuộc gọi (`zalo_new_ringback.mp3`).
  * Âm gác máy (`zalo_new_hang_up.ogg`).
  * Âm gửi tin nhắn / thả tim (`like_action.m4a`).
* **20 Hình nền cuộc trò chuyện (`Assets/Backgrounds/`)**: Thay đổi dễ dàng trong Cài đặt.

### 4. Cuộc gọi thoại & Quản lý nhóm
* **Màn hình Cuộc gọi (`CallPage`)**: Giao diện cuộc gọi riêng biệt với đồng hồ đếm giây, bật/tắt mic, loa ngoài và nút ngắt cuộc gọi.
* **Tạo nhóm trò chuyện (`CreateGroupDialog`)**: Tích chọn bạn bè từ danh bạ để tạo nhóm mới.
* **Thông tin nhóm (`GroupInfoPage`)**: Xem danh sách thành viên, phân quyền Admin, nút rời nhóm.
* **Tìm & kết bạn (`AddFriendDialog`)**: Tra cứu tài khoản theo số điện thoại và gửi kết bạn.

### 5. Tối ưu sâu cho Windows 10 Mobile
* **Nhuộm màu StatusBar**: Tự động đặt màu nền `#0068FF` với chữ trắng hài hoà giao diện Lumia.
* **Phím cứng Back**: Đăng ký `SystemNavigationManager.BackRequested` để điều hướng lùi mượt mà.
* **Thông báo Toast**: Hiển thị thông báo khi có tin nhắn mới kèm avatar người gửi, nhấn vào để mở ngay phòng chat.
* **Live Tile**: Cập nhật số lượng tin nhắn chưa đọc lên ô gạch màn hình Start.
* **Giao diện Tối AMOLED (Dark Mode)**: Chế độ nền đen sâu `#000000` giúp tiết kiệm pin tối đa cho màn hình OLED Lumia.
* **Đa ngôn ngữ**: Hỗ trợ Tiếng Việt (`vi-VN`) và Tiếng Anh (`en-US`).

---

## 🏗️ Cấu trúc thư mục dự án

```text
ZaloUWP/
├── Assets/                 # Logo, Tile, Wallpapers, Sounds, Emojis, Stickers
├── Common/                 # Tiện ích hệ thống (ApiInfo, Mobile StatusBar helper)
├── Converters/             # XAML Value Converters (Visibility, Alignment)
├── Core/                   # Giao thức mạng, Crypto, API, WebSocket, Session
├── Database/               # Bộ nhớ đệm ngoại tuyến (Offline cache JSON)
├── Models/                 # Thực thể dữ liệu (Conversation, Message, Friend, User)
├── Services/               # Audio, Notification, Settings, Voice recording
├── Strings/                # File tài nguyên đa ngôn ngữ (vi-VN, en-US)
├── ViewModels/             # Tầng logic nghiệp vụ MVVM
├── Views/                  # Giao diện XAML (Main, Chat, Call, Group, Settings,...)
├── App.xaml(.cs)           # Khởi động ứng dụng, điều hướng và phím cứng
├── Package.appxmanifest    # Cấu hình gói UWP (Target: 10.0.15063 - 10.0.15254)
├── ZaloUWP_TemporaryKey.pfx # Chứng chỉ ký ứng dụng (Password: 123456)
├── ZaloUWP_TestCert.cer    # Chứng chỉ cài lên điện thoại Lumia
└── Build-Appx.ps1          # Kịch bản biên dịch và đóng gói tự động
```

---

## 🚀 Hướng dẫn Biên dịch & Cài đặt lên điện thoại

### 1. Biên dịch qua Visual Studio (Khuyên dùng)
1. Mở file solution: `ZaloUWP.sln` bằng **Visual Studio 2017 / 2019 / 2022** (cần cài đặt workload *Universal Windows Platform development*).
2. Chọn cấu hình:
   * **Configuration**: `Release`
   * **Platform**: `ARM` (đối với điện thoại Lumia thật) hoặc `x86` (đối với máy tính / emulator).
3. Chuột phải vào project **ZaloUWP** $\rightarrow$ chọn **Publish** (hoặc **Store**) $\rightarrow$ **Create App Packages...**.
4. Chọn **Sideloading**, chọn chứng chỉ `ZaloUWP_TemporaryKey.pfx` (mật khẩu: `123456`) $\rightarrow$ Bấm **Create**.

### 2. Biên dịch nhanh bằng PowerShell
```powershell
powershell -ExecutionPolicy Bypass -File .\Build-Appx.ps1 -Platform ARM -Configuration Release
```

### 3. Cài đặt vào điện thoại Windows 10 Mobile
1. Trên điện thoại, vào **Cài đặt (Settings)** $\rightarrow$ **Cập nhật & bảo mật (Update & Security)** $\rightarrow$ **Dành cho nhà phát triển (For developers)** $\rightarrow$ Bật **Chế độ nhà phát triển (Developer mode)**.
2. Chép file chứng chỉ `ZaloUWP_TestCert.cer` vào điện thoại, bấm vào file để cài đặt chứng chỉ vào máy.
3. Chép file gói `.appx` vào điện thoại, mở bằng **File Explorer** trên máy và bấm để cài đặt trực tiếp.

