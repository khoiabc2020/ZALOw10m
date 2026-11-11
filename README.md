# ZALOw10m — Zalo Unified Client cho Windows Phone 8.1 & Windows 10 Mobile 📱

Ứng dụng Zalo Native Client được phát triển và tối ưu hóa chuyên biệt thống nhất thành **1 bản duy nhất (`ZaloWP81`)** chạy mượt mà trên cả **Windows Phone 8.1** và **Windows 10 Mobile** (ARM32), đặc biệt là các dòng máy Nokia / Microsoft Lumia (Lumia 640 XL, 950/950 XL, 730, 830, 930, 520, 630...).

Ứng dụng kết nối trực tiếp đến hệ thống Zalo thông qua giao thức WebSocket và REST API, mang lại trải nghiệm mượt mà ngay cả trên các thiết bị Lumia cấu hình 1GB RAM (chip Snapdragon 400).

---

## ✨ Tính năng nổi bật

- ⚡ **Bản duy nhất thống nhất (Unified Codebase)**:
  - Dự án gốc `ZaloWP81` chạy tương thích 100% trên cả Windows Phone 8.1 và Windows 10 Mobile.
  - Sử dụng giao diện thuần C# XAML tree siêu tốc, mở tức thì, không giật lag.
  - Chống văng ứng dụng triệt để: thay thế hoàn toàn `MessageDialog` bằng `ZaloActionSheet` hiện đại (không bị giới hạn 2 lệnh của WP8.1).
- 💬 **Trò chuyện thời gian thực (Real-time Messaging)**:
  - Nhận và gửi tin nhắn tức thời qua kết nối WebSocket bảo mật.
  - Hiển thị trạng thái tin nhắn (Đang gửi, Đã gửi, Đã nhận, Đã xem).
  - Trò chuyện 1-1 và trò chuyện nhóm đầy đủ avatar từng thành viên, cảnh báo nhóm hạn chế chat.
  - Header chat ghim cố định ở đỉnh màn hình kể cả khi mở bàn phím ảo.
  - Phân loại hộp thư: Tab **Ưu tiên** và Tab **Khác**.
- 🎨 **Giao diện & Tiện ích hiện đại**:
  - Tùy chỉnh tỉ lệ hiển thị giao diện (100%, 125%, 150%, 175%) trong Cài đặt.
  - Công tắc chuyển đổi hiện đại `ZaloModernSwitch` mượt mà, bỏ khung viền đen cổ điển.
  - Trang **Nhóm chung** (`MutualGroupsPage`) riêng biệt, đồng bộ phong cách hệ thống.
  - Trình xem ảnh phóng to/thu nhỏ (pinch-to-zoom).
  - Hệ thống **Zalo Emoticon chính thức**, Emoji hệ thống, ảnh GIF động và **Nhãn dán (Stickers)** đầy đủ.
  - Hiển thị danh thiếp (Contact Card), vị trí, thông báo nhóm.
- 👥 **Danh bạ & Quản trị nhóm**:
  - Đồng bộ danh sách bạn bè, hiển thị trạng thái và ảnh đại diện.
  - Tìm kiếm bạn bè qua số điện thoại và gửi lời mời kết bạn.
  - Quản lý danh sách nhóm, tạo nhóm mới, xem thông tin thành viên, chuyển quyền trưởng nhóm.

---

## 📥 Hướng dẫn cài đặt lên điện thoại

### 1. Chuẩn bị
Tải về các tệp từ mục [**Releases**](https://github.com/khoiabc2020/ZALOw10m/releases):
- Gói ứng dụng: `ZaloWP81_1.0.43.0_ARM.appx` (hoặc `ZaloWP81_ARM.appx`).
- Chứng chỉ số: `ZaloDev.cer` (cài vào Local Machine ➔ Trusted People / Trusted Root).

### 2. Bật chế độ Nhà phát triển trên Lumia
1. Vào **Cài đặt** (Settings) ➔ **Cập nhật & bảo mật** (Update & security) ➔ **Dành cho nhà phát triển** (For developers).
2. Chọn **Chế độ nhà phát triển** (Developer mode).
3. Bật **Cổng thiết bị Windows** (Windows Device Portal) và kết nối qua trình duyệt máy tính để tải file `.appx` lên cài đặt trực tiếp.
2. Chọn **Chế độ nhà phát triển** (Developer mode).
3. Bật **Cổng thiết bị** (Device Portal) và ghi lại địa chỉ IP (ví dụ: `http://192.168.1.x`).

### 3. Cài đặt chứng chỉ `ZaloDev.cer`
- **Cách 1 (Device Portal)**:
  1. Mở trình duyệt trên máy tính truy cập địa chỉ IP Device Portal của điện thoại.
  2. Vào mục **Apps manager** ➔ phần chứng chỉ hoặc tải file `ZaloDev.cer` vào điện thoại.
- **Cách 2 (Trực tiếp trên điện thoại)**:
  1. Chép file `ZaloDev.cer` vào bộ nhớ máy hoặc thẻ nhớ.
  2. Mở ứng dụng **File Explorer** trên điện thoại, nhấn vào file `ZaloDev.cer` và chọn **Install Certificate** vào kho `Trusted People` hoặc `Root`.

### 4. Cài đặt gói `.appx`
- Mở **Windows Device Portal** trên trình duyệt máy tính (`http://<IP-điện-thoại>/Apps%20manager`).
- Tại ô **App package**: Chọn tệp `ZaloUWP_..._ARM.appx`.
- Nhấn **Deploy** (hoặc **Go**) và đợi trang báo **Done!**.
- Ứng dụng **Zalo Mobile** sẽ xuất hiện trong danh sách ứng dụng của điện thoại.

---

## 🛠️ Hướng dẫn build từ mã nguồn

### Yêu cầu môi trường
- Windows 10 hoặc Windows 11 (PC).
- **Visual Studio 2017, 2019 hoặc 2022** đã cài đặt:
  - Universal Windows Platform development workload.
  - Windows 10 SDK (tối thiểu 10.0.10586 / 10.0.15063 / 10.0.16299).
- **Python 3.8+** (để chạy script build tự động).

### Các bước biên dịch
1. Clone mã nguồn về máy:
   ```bash
   git clone https://github.com/khoiabc2020/ZALOw10m.git
   cd ZALOw10m
   ```
2. Chạy script tự động biên dịch, vá tương thích VCLibs 14.0 cho Mobile 1709, đóng gói và ký số:
   ```bash
   python build_and_deploy.py
   ```
3. Gói `.appx` sẵn sàng cài đặt sẽ nằm ngay tại thư mục gốc của dự án.

---

## 📌 Phiên bản mới nhất: v1.3.23.0
- **Tối ưu hóa dung lượng**: Giảm dung lượng gói `.appx` xuống còn **~11.4 MB** (tiết kiệm hơn 1.3 MB dung lượng lưu trữ), loại bỏ tài nguyên dư thừa và nén lossless hình ảnh.
- **Refactor kiến trúc**: Phân tách lớp nguyên khối `ZaloMessaging.cs` thành 9 mô-đun chuyên biệt (`ZaloApp`, `ZaloConversation`, `ZaloListener`, `ZaloFormat`, `ZaloAvatar`, `ZaloEmoticonMap`, `ZaloGcm`, `ZaloJson`, `ZaloMessaging`).
- **Tối ưu bộ nhớ (1GB RAM)**: Giới hạn kích thước decode ảnh đại diện (`DecodePixelWidth = 96`), quản lý bộ nhớ đệm tin nhắn thông minh và unhook event sạch sẽ khi chuyển trang.
- **Sửa lỗi giao diện**: Triệt tiêu hoàn toàn khung viền đen (focus visuals) khi chạm vào biểu tượng và thanh điều hướng trên Windows 10 Mobile.

---

## 📜 Giấy phép & Tuyên bố miễn trừ trách nhiệm
- Dự án này được phát triển độc lập cho mục đích học tập, nghiên cứu và phục hồi trải nghiệm sử dụng trên các thiết bị Windows 10 Mobile cổ điển.
- Tất cả các nhãn hiệu, logo và tài nguyên liên quan đến Zalo thuộc sở hữu của VNG Corporation.

