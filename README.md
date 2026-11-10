# ZALOw10m — Zalo UWP Client cho Windows 10 Mobile 📱

Ứng dụng Zalo Native Client được phát triển và tối ưu hóa chuyên biệt cho các thiết bị chạy **Windows 10 Mobile** (ARM32), đặc biệt là dòng Microsoft Lumia (Lumia 640 XL, Lumia 950/950 XL, Lumia 730, 830, 930...).

Ứng dụng kết nối trực tiếp đến hệ thống Zalo thông qua giao thức WebSocket và REST API, mang lại trải nghiệm mượt mà ngay cả trên các thiết bị Lumia cấu hình 1GB RAM (chip Snapdragon 400).

---

## ✨ Tính năng nổi bật

- 🔐 **Đăng nhập an toàn bằng mã QR**: Quét mã QR trực tiếp từ app Zalo trên điện thoại chính.
- 💬 **Trò chuyện thời gian thực (Real-time Messaging)**:
  - Nhận và gửi tin nhắn tức thời qua kết nối WebSocket bảo mật.
  - Hiển thị trạng thái tin nhắn (Đang gửi, Đã gửi, Đã nhận, Đã xem).
  - Trò chuyện 1-1 và trò chuyện nhóm.
  - Phân loại hộp thư: Tab **Ưu tiên** và Tab **Khác**.
- 🎨 **Đa phương tiện & Đồ họa phong phú**:
  - Gửi và xem hình ảnh với trình xem ảnh phóng to/thu nhỏ (pinch-to-zoom).
  - Hệ thống **Zalo Emoticon chính thức**, Emoji hệ thống, ảnh GIF động và **Nhãn dán (Stickers)** đầy đủ.
  - Hiển thị danh thiếp (Contact Card), vị trí, thông báo nhóm.
- 👥 **Danh bạ & Nhóm**:
  - Đồng bộ danh sách bạn bè, hiển thị trạng thái và ảnh đại diện.
  - Tìm kiếm bạn bè qua số điện thoại và gửi lời mời kết bạn.
  - Quản lý danh sách nhóm, tạo nhóm mới, xem thông tin thành viên.
- ⚡ **Tối ưu hóa phần cứng di động**:
  - Tối ưu bộ nhớ đệm cho thiết bị RAM 1GB, chống tràn RAM và giảm nhiệt độ máy.
  - Giao diện chuẩn phong cách Zalo hiện đại, triệt tiêu hoàn toàn khung viền đen khi chạm nút trên Windows 10 Mobile.

---

## 📥 Hướng dẫn cài đặt lên điện thoại

### 1. Chuẩn bị
Tải về các tệp từ mục [**Releases**](https://github.com/khoiabc2020/ZALOw10m/releases):
- Gói ứng dụng: `ZaloUWP_x.x.xx.x_ARM.appx` (phiên bản mới nhất).
- Chứng chỉ số: `ZaloDev.cer`.
- *(Tùy chọn)*: `Microsoft.NET.CoreRuntime.1.1.appx` (nếu máy chưa có runtime .NET Core).

### 2. Bật chế độ Nhà phát triển trên Lumia
1. Vào **Cài đặt** (Settings) ➔ **Cập nhật & bảo mật** (Update & security) ➔ **Dành cho nhà phát triển** (For developers).
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

## 📜 Giấy phép & Tuyên bố miễn trừ trách nhiệm
- Dự án này được phát triển độc lập cho mục đích học tập, nghiên cứu và phục hồi trải nghiệm sử dụng trên các thiết bị Windows 10 Mobile cổ điển.
- Tất cả các nhãn hiệu, logo và tài nguyên liên quan đến Zalo thuộc sở hữu của VNG Corporation.
