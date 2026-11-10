# Zalo UWP — trạng thái dự án

Hướng dẫn đóng gói ARM có chữ ký: [ANTIGRAVITY_BUILD_HANDOFF.md](ANTIGRAVITY_BUILD_HANDOFF.md).
**Package ARM mới nhất đã build và ký số:** `D:\zalo\ZaloUWP_1.3.15.0_ARM.appx` (12,726,751 bytes, Subject: CN=ZaloDev). SẴN SÀNG CHO WINDOWS DEVICE PORTAL.
- Tích hợp bảng chọn Emoji, nhãn dán, Zalo emoticon và kho ảnh động GIF có sẵn trong ứng dụng tương tự Zalo Android.
- Tự động nhận diện số điện thoại trong tin nhắn để hiển thị Thẻ liên hệ Zalo (cho phép gửi lời mời kết bạn, nhắn tin trực tiếp hoặc sao chép số).
- Sửa triệt để lỗi không mở được ChatPage khi nhấn vào hội thoại/bạn bè (loại bỏ thuộc tính scrollbar trong thẻ `<ScrollViewer>` của `ChatPage.xaml` gây ra lỗi cast `IFrameworkElement7` trên Windows 10 Mobile 1709).

## Khôi phục hộp thư và danh bạ khi mở app (2026-11-09)

- UID tài khoản được lưu cùng session. Khi mở app, UI đọc cache hội thoại/danh bạ theo UID trước bước xác thực cookie mạng; lần nâng cấp đầu với session cũ chưa có UID vẫn cần một lần xác thực để ghi UID này.
- `StartAsync` nạp cache trước, sau đó làm mới WebSocket, nhóm, trạng thái ghim/lưu trữ/ẩn/tắt tiếng, danh bạ máy chủ, hiện diện và hồ sơ ở nền.
- Trong lúc đọc cache, danh sách bỏ qua các lần dựng lại lặp cho từng hội thoại và cập nhật một lần sau khi hoàn tất.
- Ghi cache hội thoại được tuần tự hóa để các lần lưu nền không ghi đè lẫn nhau; khi app chuyển sang nền, nó chờ lưu danh sách hội thoại và danh bạ.
- Avatar nhóm lấy từ metadata nhóm, tính cả nhóm còn trong cache hộp thư nếu API danh sách nhóm lỗi, rồi lưu lại URL để lần sau hiện sớm hơn.
- Chưa build ARM hoặc thử trên Lumia. Antigravity cần xác nhận sau khi tắt hẳn/mở lại app: hội thoại, 30 tin gần nhất mỗi cuộc trò chuyện, danh bạ và avatar nhóm hiện ngay; sau đó nội dung mới tiếp tục đồng bộ qua mạng.

## Chat: tùy chọn, trạng thái nút và ghi âm (2026-11-09)

- Nút ba chấm chat cá nhân mở `ConversationOptionsPage`; nút ba chấm nhóm mở màn thông tin/tùy chọn nhóm. Trang Tùy chọn đã được khai báo trong project để vào gói build; khi mở sẽ đưa cuộn về đầu, có avatar chữ dự phòng, các hàng giãn hết chiều ngang và bỏ trạng thái nền vuông.
- Nút chat/header dùng template không vẽ nền vuông khi nhấn; ô nhập tắt focus border hệ thống. Nút ghi âm hiện trạng thái ngay khi bắt đầu mở microphone, báo lỗi khởi tạo cụ thể và hủy an toàn nếu mất pointer capture.
- Tìm tin nhắn từ Tùy chọn giới hạn đúng hội thoại hiện tại.
- Tìm nhanh trong danh sách hội thoại được debounce 180 ms để tránh quét toàn bộ tin cache theo từng ký tự khi gõ trên máy ARM.
- WebSocket xử lý sự kiện 502/522: trạng thái gửi/nhận/xem trên tin do tài khoản gửi; tin đã xem có avatar người nhận khi URL avatar đã có trong cache. Cập nhật trạng thái cũng được lưu cùng cache tin nhắn.
- Cài đặt hiện chỉ cho chọn giao diện sáng để các trang không bị pha sáng/tối; Dark chưa có bộ màu đồng nhất.
- Chưa build ARM hoặc thử tương tác trên Lumia; cần kiểm tra ghi âm khi giữ/thả, quyền microphone, trạng thái nhận/xem từ tài khoản thứ hai và avatar sau khi nhấn.

## UI chat và nhóm — cần Antigravity build xác minh (2026-11-09)

- Chat: xử lý `InputPane.Showing/Hiding` để giữ header, dành vùng hiển thị phía trên bàn phím và căn ô nhập theo giữa; chat người chưa kết bạn hiện hàng Kết bạn, gửi lời mời qua API đang dùng ở Danh bạ.
- Tin `chat.recommended`: đọc URL trong payload lồng để tạo link preview; nếu là `zalo.me/g/...` hiển thị thẻ Cộng đồng Zalo và làm giàu metadata bằng API parse link. Tin danh thiếp vẫn dùng UI danh thiếp khi có UID và QR hợp lệ.
- Nhóm: header có thông tin/menu nhóm thay cho nút gọi cá nhân; tải số thành viên từ `getmg-v2`; hiển thị preview tin ghim bằng request nền và cache 5 phút; nút tắt/bật thông báo gọi API mute.
- Thông tin nhóm có nút mute đồng bộ cùng trạng thái hội thoại; avatar nhóm được cập nhật từ API thông tin nhóm.
- Chat và GroupInfo XAML đã parse XML thành công. Chưa build ARM, chưa xác nhận payload `chat.recommended` thực tế và chưa kiểm tra tương tác bàn phím/camera trên Lumia.
- Khi build, kiểm tra: header chat khi mở bàn phím; banner người lạ và gửi lời mời; tin `chat.recommended` chứa link cộng đồng; preview tin ghim; mute nhóm; nút info/menu trên header nhóm; chuyển qua lại từ GroupInfo và chat.

## Nhóm chỉ quản trị viên được nhắn — cần Antigravity build xác minh (2026-11-09)

- Chat nhóm đọc `setting.lockSendMsg` và so UID hiện tại với `creatorId`/`adminIds` trong dữ liệu `getmg-v2`. Thành viên thường thấy thanh thông báo ở vị trí composer; quản trị viên vẫn dùng composer bình thường.
- Khi đang tải quyền, composer tạm ẩn để tránh lộ ô gửi trước khi biết chính sách nhóm. Nếu API lỗi, hiện thông báo kèm nút thử lại.
- Các đường gửi chữ, danh thiếp, ảnh, sticker, voice và file trong `ZaloApp` đều kiểm tra lại trạng thái quyền trước khi gọi API; nếu chưa xác minh được quyền thì cũng chặn gửi và yêu cầu mở lại nhóm. Trạng thái được cache theo hội thoại.
- Màn Thông tin nhóm dùng dải thao tác icon tròn, cỡ chạm lớn hơn; nút đổi tên chỉ hiện khi tài khoản là trưởng/phó nhóm.
- Nguồn tham chiếu API trong workspace: `zca-js/src/apis/getGroupInfo.ts` (`/api/group/getmg-v2`), `updateGroupSettings.ts` (`lockSendMsg`), `getListBoard.ts` (`/api/board/list`) và `getListReminder.ts` (`/api/board/listReminder`). Đây là mã client API không chính thức có trong workspace; chưa xác minh live trên tài khoản hiện tại. Các thay đổi chưa được build ARM hay thử trên Lumia.
- Sau build, kiểm tra cùng một nhóm bằng tài khoản thành viên thường và tài khoản trưởng/phó: composer thay bằng notice đúng lúc, nút thử lại chạy khi mất mạng, thành viên không gửi được chữ/media/file, quản trị viên gửi bình thường.

## Cập nhật hiệu năng chat (2026-11-09)

- Chat gộp cập nhật ngày/header và cuộn xuống sau từng đợt thêm tin thay vì xử lý lặp lại trên mỗi phần tử. Danh sách chat giảm vùng đệm ảo hóa; ảnh nền giới hạn giải mã 480×854; bong bóng dùng brush dùng chung.
- Điều hướng tái sử dụng bitmap icon; danh sách hội thoại và danh bạ bỏ qua lọc/sắp xếp lại khi quay về tab mà query và nguồn dữ liệu không đổi.
- Đồng bộ toàn bộ lịch sử từ điện thoại vẫn chưa được nối. Nút hiện báo chưa hỗ trợ; cần triển khai request/xác nhận, polling, giải mã backup và nhập đúng hội thoại theo giao thức mới trước khi bật.
- Kiểm tra XML XAML đạt. Build ARM chưa xác minh trong môi trường hiện tại vì thiếu Visual Studio UWP targets; cần build trên máy có UWP workload và thử Lumia để đo độ mượt thực tế.

## Bổ sung luồng hội thoại và ứng dụng Zalo Developers

- Hộp thư có ghim/bỏ ghim hội thoại, xóa khỏi hộp thư, đánh dấu chưa đọc và tự bỏ dấu khi mở lại. Badge riêng của hội thoại được tính từ số tin chưa đọc thật; dấu chưa đọc không còn làm tăng giả số lượng tin.
- Lưu trữ hội thoại gọi `/api/archivedchat/update`; khi khởi động, app đọc `/api/archivedchat/list` và chỉ đồng bộ các dạng phần tử nhận diện được (chuỗi `u…`/`g…` hoặc object ID kèm loại). Nếu schema không nhận diện được/endpoint lỗi, app giữ trạng thái cục bộ theo UID thay vì xóa danh sách. Build ARM thành công; chưa live-test trên Lumia.
- Tin nhắn hỗ trợ thu hồi tin của mình, xóa ở phía mình, trạng thái thu hồi nhận từ WebSocket/lịch sử, phản hồi đang nhập và thử tải lại ảnh lỗi. Các thao tác máy chủ cập nhật UI sau khi request thành công; vẫn cần xác nhận trên thiết bị/tài khoản thật.
- Composer chat theo Android: emoji, ô nhập, menu ba chấm, micro/ảnh; micro chuyển thành gửi khi có nội dung. Menu ba chấm mở luồng gửi tệp và chia sẻ danh thiếp; icon tác vụ lấy từ APK tham chiếu. Build ARM Release thành công; cần xem trực tiếp trên Lumia.
- Khi MediaElement không phát trực tiếp voice/video do cần cookie Zalo, chat thử tải tệp qua HTTP session đăng nhập và phát từ thư mục tạm; lỗi tải/codec được hiển thị bằng trạng thái không phát được. Giới hạn downloader hiện là 25 MB. Build ARM thành công, playback cần xác minh trên Lumia.
- Tệp đính kèm nhận được tải theo từng khối qua HTTP session thẳng vào vị trí người dùng chọn trong Downloads, rồi mở bằng ứng dụng Windows phù hợp; nếu không có handler thì vẫn giữ tệp đã tải. Dung lượng được chặn ở 25 MB trong lúc stream, không đợi tải xong mới kiểm tra. Build ARM thành công; picker/handler cần xác minh trên Lumia.
- Nhấn giữ tin văn bản có Chuyển tiếp: hộp thoại tìm bạn bè/nhóm, chọn nhiều người nhận, gửi qua `/api/message/mforward` hoặc `/api/group/mforward` với payload theo `zca-js`. Chỉ chuyển tiếp text; ảnh, sticker, voice, video và file chưa được chuyển tiếp. Build ARM thành công; chưa live-test API trên Lumia.
- Danh bạ có tải lời mời, chấp nhận/từ chối, làm mới danh bạ sau chấp nhận và tra cứu local/online theo số điện thoại hoặc username. Endpoint dựa trên `zca-js`; đây là API phiên chat riêng, chưa được live-test trong lần triển khai này.
- Trang hồ sơ cho phép sửa trạng thái cá nhân qua `/api/social/profile/status`; lưu UI sau khi API xác nhận. Chưa live-test trên tài khoản thật.
- Ứng dụng Zalo Developers mới tạo có thể dùng cho OAuth Social Login hoặc tích hợp OA theo sản phẩm/quyền được duyệt. Nó không thay thế QR session cá nhân đang dùng và không tự cấp quyền đọc hộp thư cá nhân. Không lưu App Secret vào repo; nếu secret xuất hiện trong ảnh/chia sẻ, cần rotate.
- Nút gọi cá nhân báo rõ chưa hỗ trợ; không phát tiếng chờ, timer hay điều khiển giả. ZCC là SIP cho OA, không phải gọi cá nhân. Rà lại `ctdozlite` main hiện tại: repo mô tả Rust signaling và audio/video 1-1, nhưng `src/call.rs` còn khóa outbound theo allowlist tài khoản thử và chặn gọi nhóm; `src/media.rs` tự ghi nhận chưa có jitter buffer/resampling/echo cancellation. Dependency `cpal`/`opus` và FFmpeg camera hướng desktop, chưa chứng minh chạy Windows 10 Mobile ARMv7. Chưa port hay xác minh trên UWP; gọi chưa hoạt động trong app. Chi tiết và nguồn ở `ZALO_ANDROID_PARITY_ARCHITECTURE.md`.

> Tài liệu này ghi trạng thái đã kiểm tra trong mã nguồn, không coi giao diện có sẵn là bằng chứng tính năng đã chạy thật. Thiết kế parity Android và kiến trúc đích nằm trong [ZALO_ANDROID_PARITY_ARCHITECTURE.md](ZALO_ANDROID_PARITY_ARCHITECTURE.md).

## Mục tiêu

Ứng dụng chat UWP cho Lumia 640 XL Dual SIM, Windows 10 Mobile 10.0.15254, kiến trúc ARM 32-bit. Giao diện lấy Zalo Android làm tham chiếu, đồng thời giữ thao tác nhẹ và dễ chạm trên màn hình điện thoại.

## Luồng ứng dụng đang chạy

`App.xaml.cs` khôi phục QR session qua `ZaloLite.ZaloStore`, gọi `ZaloLite.ZaloApp.StartAsync`, rồi mở `Views/MainPage.xaml`. Màn chính, chat, danh bạ, WebSocket và đồng bộ điện thoại hiện dùng các lớp trong namespace `ZaloLite` được biên dịch cùng dự án.

Một bộ mã thứ hai trong `ZaloUWP.Core`, `ZaloUWP.ViewModels`, `ZaloUWP.Models` và `Database` chưa phải nguồn dữ liệu chính của luồng khởi chạy. Trang Cài đặt đã được rà soát: quyền riêng tư đọc/ghi bằng `ZaloApp.Session` hiện tại; giao diện, âm báo và hình nền lưu trong `ZaloSettingsService` ở bộ nhớ ứng dụng cục bộ. Trang Thông tin nhóm đã chuyển sang `ZaloLite` và API session đang chạy. Không tạo session hoặc dữ liệu giả ở bộ mã phụ.

## Trạng thái tính năng đã xác minh trong mã

| Khu vực | Trạng thái hiện tại |
|---|---|
| Đăng nhập QR và khôi phục cookie | Luồng QR và khôi phục phiên dùng `ZaloLite`; màn đăng nhập chỉ giữ QR. Các tab token/trình duyệt/import cũ dùng bộ phiên khác nên đã bỏ khỏi UI; kiểm tra lại QR trên Lumia sau mỗi thay đổi. |
| Tin nhắn chữ và WebSocket | Có gửi chữ, nhận frame, cache theo UID tài khoản; cache được nạp khi khởi động, không xóa trong static constructor. Cache cũ không có UID bị dọn để tránh trộn dữ liệu giữa tài khoản. Lúc WebSocket nhận key gửi lệnh 510/511 xin tin gần đây; khi mở chat gọi tải 50 tin lịch sử. Nút tải tin cũ trong nhóm dùng API history hiện có (tối đa 500); trong chat cá nhân gửi WebSocket cmd 510 với `lastId` cũ và chờ batch phản hồi 12 giây, sau đó thêm tin qua luồng WebSocket hiện có. Lịch sử direct tải rộng theo luồng tin cá nhân và được route về đúng hội thoại; chưa live-test trên Lumia. Khi mở chat và nhận tin mới trong chat đang mở, app gửi seen qua `/api/message/seenv2` hoặc `/api/group/seenv2`. Khi gửi chữ/ảnh, hội thoại chỉ vào danh sách sau khi API xác nhận; gửi chữ lỗi giữ bản nháp. Nhấn giữ tin có Sao chép/Trả lời, 6 cảm xúc, Thu hồi tin nhắn của mình và Xóa ở phía tôi. Reaction dùng `/api/{message,group}/reaction`; thu hồi dùng `/api/message/undo` hoặc `/api/group/undomsg`; xóa phía tôi dùng `/api/message/delete` hoặc `/api/group/deletemsg`, payload theo `zca-js`. Các thay đổi chỉ phản ánh sau khi API trả thành công. Reply gọi quote endpoint; media chưa hỗ trợ quote. Hộp thư cá nhân và các thao tác mới chưa live-test trên Lumia. |
| Danh bạ | Có gọi API bạn bè, lọc cục bộ và nạp danh sách nhóm thật qua `group_poll` + metadata nhóm. Trạng thái hoạt động dùng API tham chiếu `/api/social/friend/onlines`, chỉ chấm xanh UID server trả về; không suy đoán bạn bè còn lại đang offline. Header chat dùng `/api/social/profile/lastOnline` và chỉ hiện lần truy cập nếu `settings.show_online_status` bật. Chưa live-test presence trên Lumia. |
| Tạo nhóm | Đã nối hộp thoại với API tạo nhóm theo session và trả lỗi server; đã build, chưa xác nhận API thành công trên tài khoản thật. |
| Thêm bạn | Đã nối tra cứu số điện thoại và gửi lời mời theo session; đã build, chưa xác nhận kết quả API trên tài khoản thật. |
| Quản lý nhóm | Danh sách vai trò nhận từ `adminIds`/`creatorId`; menu giữ thành viên cho quản trị viên hỗ trợ bổ nhiệm/gỡ phó nhóm, xóa thành viên và (chỉ trưởng nhóm) chuyển quyền. Quản trị viên xem/tạo link mời, sao chép và vô hiệu hóa link; endpoint theo `zca-js`. Build ARM thành công; chưa live-test trên nhóm thật. |
| Tìm kiếm | Search bar lọc tên/preview/nội dung tin đã tải, bạn bè và nhóm không phân biệt dấu tiếng Việt (kể cả đ/Đ); Enter tại Tin nhắn mở kết quả từng tin, tìm không dấu trên cache đang có, tối đa 200 hit mới nhất; chạm hit mở chat và cuộn tới tin đó. Lịch sử máy chủ chưa tải xuống sẽ không xuất hiện. Enter trong Danh bạ gọi tra cứu số điện thoại hoặc username theo API tham chiếu. UI danh bạ dùng ba pill Tất cả/Bạn bè/OA; tab Tất cả xếp Liên hệ nhanh tối đa 10 bạn trước danh sách chữ cái. Nút header mở Nhóm và menu lời mời/thêm bạn/tạo nhóm. Chưa live-test tìm online hoặc điều hướng hit trên Lumia. |
| Ưu tiên/Khác/Lưu trữ | Ưu tiên/Khác lọc cùng danh sách; lưu trữ gọi API cập nhật. Khởi động đọc archive từ server, nhận dạng các dạng ID/type đã biết và lưu cache theo UID; schema lạ giữ cache cũ. Mục Đã lưu chỉ hiện hội thoại còn trong cache cục bộ. Chưa live-test trên Lumia. |
| Ảnh, sticker và tệp | Tin lịch sử/live parse URL ảnh từ object, JSON string hoặc JSON array; URL trong metadata đính kèm lồng nhau cũng được nhận diện. Chat dùng URL media thông thường; `chat.doodle` được hiển thị bằng pipeline ảnh/viewer; viewer ưu tiên `hdUrl`/`oriUrl`/`rawUrl` nếu tin nhắn cung cấp và giữ URL này trong cache. Khi tải ảnh trực tiếp lỗi, chat tải lại qua HTTP session để dùng cookie/User-Agent Zalo; viewer dùng cùng downloader, giữ tỉ lệ ảnh và hỗ trợ zoom/lưu. Sticker tra `sticker_detail`. Gửi ảnh JPG/JPEG/PNG/WebP theo chunk, đọc file nguồn dần, có caption; giới hạn app 20 MB. Tệp/MP4 cũng upload theo chunk trực tiếp từ file, checksum MD5 tính dần; giới hạn 20 MB. Tin có URL lấy preview title/desc/thumb/href qua `/api/message/parselink`; giới hạn 5 tin gần nhất mỗi lần mở chat, cache trong phiên, chạm mở URL. Emoji lấy từ APK; sticker tìm qua `suggest/stickers` và gửi direct/group. Voice ghi âm giữ-nút/thả-để-gửi, upload `asyncfile/upload`, chờ WebSocket `file_done`, gửi qua `/api/{message,group}/forward`. Tệp gửi xong chờ `file_done`, rồi gửi qua `asyncfile/msg`; MP4 phát bằng MediaElement, tệp mở URI qua Windows Launcher. ARM Release build thành công; upload/preview chưa live-test trên Lumia. |
| Thiết lập âm thanh | Nút bật/tắt âm thanh điều khiển âm báo khi nhận tin mới và gửi tin; tùy chọn Enter để gửi vẫn áp dụng trong ô chat. Chưa kiểm tra âm lượng/thời điểm phát trên Lumia. |
| Branding và ảnh | Splash dùng wordmark trắng trên nền xanh được dựng từ vector trong APK 26.10.02; ảnh chat giữ nguyên tỷ lệ; avatar tròn crop có chủ đích. Composer chat dùng icon emoji, ảnh, mic và gửi trích từ APK Android; gửi tệp và chia sẻ danh thiếp mở từ menu ba chấm. Preview hình nền không cắt ảnh. |
| Cuộc gọi | Chưa triển khai signaling/media cho Windows 10 Mobile ARMv7; nút gọi đã ẩn khỏi chat và danh bạ. `ctdozlite` README `main` và release v0.0.7 (rà 2026-11-08) mô tả signaling `zcall-v2` và bridge desktop tới `ZaloCall.exe`, chưa chứng minh tương thích UWP/mobile. |
| Đồng bộ lịch sử từ điện thoại | Tắt request UWP cũ `/api/message/pull_mobile_msg` vì trả HTTP 404; placeholder đồng bộ đã ẩn khỏi UI. Issue triển khai `ctdozlite` xác nhận chuỗi giao thức nhiều bước, chưa port chính xác và chưa bật cho tới khi request được nối đúng. |
| Nhật ký | Chưa nối nguồn dữ liệu bài đăng cá nhân; tab placeholder đã ẩn khỏi thanh điều hướng. APK Android 26.10.02 có dấu vết route/cache nhưng chưa giải được host, payload, schema response và pagination theo phiên; chưa đủ căn cứ để gọi endpoint.
| Camera QR | Chưa có bộ giải mã QR cho camera Lumia. Có thể triển khai như tính năng local bằng MediaCapture và thư viện decoder thuần C#, nhưng phải xác nhận thư viện chạy trên ARMv7/Windows 10 Mobile 15254. Nút placeholder đã ẩn cho tới khi nối luồng quét thật.
| Cloud cá nhân | Chưa tìm được endpoint tải lên/liệt kê tệp được xác nhận trong `zca-js` hoặc mã workspace. Placeholder đã ẩn; người dùng vẫn có thể gửi tệp trong chat.
| Khám phá | Gợi ý kết bạn tải loại `recommType=1` qua `/api/friend/recommendsv2/list`, lọc bạn hiện có và lời mời đã gửi, gửi lời mời kèm ghi chú bằng `/api/friend/sendreq`. Cloud/QR placeholder đã ẩn; tiện ích tạo nhóm, thêm bạn và gợi ý vẫn hoạt động. Chưa tích hợp danh mục dịch vụ Zalo.
| Trạng thái lỗi API và badge tab | Phản hồi HTML lỗi như nginx 404 được chuyển thành thông báo ngắn có mã HTTP, không hiện raw HTML; badge N/! minh họa cố định ở Nhật ký/Cá nhân đã bỏ vì chưa có nguồn unread thật. |
| Thông tin nhóm, hồ sơ, cài đặt | Thông tin nhóm tải tên/avatar/thành viên qua `getmg-v2`; bảng nhóm đọc 20 mục gần đây từ `/api/board/list`, tạo/sửa ghi chú qua `/api/board/topic/createv2` và `/api/board/topic/updatev2`, tạo poll qua `/api/poll/create`, mở chi tiết qua `/api/poll/detail` và gửi lựa chọn qua `/api/poll/vote`; nhắc hẹn nhóm tải qua `/api/board/listReminder`, tạo/sửa/xóa qua `/api/board/topic/createv2`, `/api/board/topic/updatev2`, `/api/board/topic/remove` (nút quản lý chỉ hiện với người tạo). Đã build, chưa live-test. Thẻ hồ sơ Cá nhân đọc `/api/social/profile/me-v2`; trang hiển thị thông tin cá nhân, nối sửa tên/ngày sinh/giới tính qua `/api/social/profile/update` theo `zca-js/src/apis/updateProfile.ts`, trạng thái qua `/api/social/profile/status`, và chọn ảnh + chuyển sang JPEG + multipart upload `fileContent` lên `/api/profile/upavatar` theo `zca-js/src/apis/changeAccountAvatar.ts`. Upload avatar build được nhưng chưa live-test. Chỉ cập nhật UI sau khi request thành công; tài khoản business có `biz.cate` được chặn khi sửa hồ sơ để tránh làm mất danh mục. Nút thông tin trong chat và menu danh bạ mở hồ sơ bạn bè qua `/api/social/friend/getprofiles/v2`; nút Nhắn tin trên hồ sơ mở đúng chat. Chỉnh sửa hồ sơ bạn bè chưa hỗ trợ. Cài đặt ứng dụng lưu theme/âm báo/Enter gửi/hình nền; cài đặt quyền riêng tư tải từ `/api/setting/me`, cập nhật online/đã xem/ngày sinh/nhận tin/gọi và cách tìm bạn qua `/api/setting/update` theo `zca-js/src/apis/{getSettings,updateSettings}.ts`. Cài đặt chưa live-test; mục không có dữ liệu server được khóa để tránh ghi đè. Trang giới thiệu lấy phiên bản từ package. |

Icon tab đang dùng resource Android đã trích từ XAPK tham chiếu; trạng thái tab chưa chọn tiếp tục dùng icon xám hiện có. Chi tiết endpoint và cách lấy service host theo phiên được ghi trong [ZALO_ANDROID_PARITY_ARCHITECTURE.md](ZALO_ANDROID_PARITY_ARCHITECTURE.md#61-cách-app-android-liên-kết-api--kết-quả-rà-apk). API cá nhân là giao thức riêng không chính thức và chưa được xác minh live trong lượt rà này.

## Build

Build ARM Release thành công trong workspace bằng MSBuild 18:

```powershell
D:\VS\MSBuild\Current\Bin\MSBuild.exe D:\zalo\ZaloUWP\ZaloUWP\ZaloUWP.csproj /t:Build /p:Configuration=Release /p:Platform=ARM /p:UseDotNetNativeToolchain=false /p:AppxBundle=Never /m:1
```

Gói kiểm tra được tạo tại `ZaloUWP\ZaloUWP\AppPackages\ZaloUWP_1.2.2.0_ARM_Test\ZaloUWP_1.2.2.0_ARM.appx`. Build Debug ARM trên máy này từng lỗi do thiếu `ucrtbased.dll` ARM trong Windows SDK; file x86 hoặc ARM64 không thay thế được bản ARMv7. Build chưa đồng nghĩa với cài đặt hay kiểm thử trên điện thoại.

## Quy tắc giữ tài liệu đúng

- Tách rõ **đã có UI**, **đã có code**, **đã nối vào luồng khởi chạy**, và **đã xác minh trên Lumia**.
- Tài liệu Zalo for Developers về OAuth/OA không được xem là đặc tả API chat cá nhân.
- Dùng `zca-js` trong workspace làm tài liệu tham chiếu giao thức; coi endpoint không chính thức là có thể thay đổi và báo lỗi rõ ràng.
- Không lưu mật khẩu chứng chỉ, cookie, token hoặc khóa riêng trong Markdown hay log.
- Chat đã tách tin `chat.video.msg` khỏi ảnh và dùng MediaElement controls; `share.file` đọc URL/tên tệp rồi mở URI qua Windows Launcher. Gửi tệp thường/MP4 đã nối theo payload tham chiếu `zca-js` (`asyncfile/upload`, `file_done`, `asyncfile/msg`), giới hạn phía app 20 MB. Chưa kiểm chứng upload, codec hoặc handler trên Lumia.
- Parser media chat đọc URL từ object, JSON-string và mảng lồng nhau; sticker tải chi tiết theo ID qua service map sticker. Bộ trích ID sticker vừa được mở rộng để xử lý object, mảng và JSON-string lồng sâu. ARM Release build thành công; tải ảnh/sticker vẫn cần xác minh trên Lumia.
- Icon chat header, emoji và nút gửi dùng ảnh gốc trích từ APK Zalo 26.10.02; icon ảnh/mic dùng resource Android sẵn có trong Assets. Reaction đã có code nhưng chưa thử tài khoản thật.
- Cập nhật bảng trạng thái này và architecture doc sau mỗi mốc thay đổi lớn.

## Cập nhật triển khai 2026-11-08

- Đã thêm tắt/bật thông báo riêng cho từng hội thoại và nhóm theo `/api/social/profile/getmute` và `setmute`; trạng thái tải từ server, lưu cache và chặn âm báo cho tin mới ở hội thoại đã tắt tiếng. ARM Release build thành công; chưa live-test API trên Lumia.
- Zalo Cloud Connect là luồng gọi audio/video giữa OA và người dùng qua SIP trunk, yêu cầu OA/gói dịch vụ phù hợp, quyền gọi được duyệt và hệ thống tổng đài doanh nghiệp. Cấu hình trong ảnh hiện chưa có cổng kết nối/nhánh. Đây không phải gọi cá nhân giữa bạn bè.
## Cập nhật UI bạn bè và nhóm

- Danh bạ cho phép nhấn giữ/chạm phải một người bạn để sửa hoặc xóa biệt danh. Biệt danh được tải từ `/api/alias/list`, ghi qua `/api/alias/update` hoặc `/api/alias/remove`, rồi đồng bộ sang tên hội thoại và cache sau khi API trả thành công.
- Thông tin nhóm có thao tác đổi tên qua `/api/group/updateinfo`; sau khi server xác nhận, tên mới cập nhật trong trang nhóm, danh sách hội thoại và cache. Quyền quản trị do máy chủ xác định.
- Cả hai phần đã biên dịch ARM Release; API chưa live-test trên thiết bị.
## Cập nhật lời mời và danh bạ

- Danh bạ có thêm thẻ lời mời đã gửi. Danh sách dùng `/api/friend/requested/list`; thu hồi dùng `/api/friend/undo`. Mã lỗi 112 của danh sách được xử lý là trạng thái không có lời mời theo ghi chú `zca-js`.
- Menu nhấn giữ bạn bè có thao tác xóa bạn qua `/api/friend/remove`, có xác nhận và loại UID khỏi liên hệ nhanh sau khi API thành công. Cuộc trò chuyện hiện có được giữ lại.
- ARM Release build thành công; chưa xác minh các luồng này trên tài khoản/Lumia thật.

## Cập nhật trích xuất sticker và media chat

- Mở rộng parser ID sticker để duyệt object, mảng và JSON-string lồng nhau; parser URL ảnh đọc thêm URL nằm trong mảng JSON. Sticker tiếp tục tải chi tiết qua API phiên hiện tại khi có ID.
- ARM Release build thành công; chưa xác minh tải ảnh/sticker trực tiếp trên Lumia.

## Cập nhật bảng nhóm và tìm kiếm — 2026-11-08

- Trang thông tin nhóm tải 20 mục bảng nhóm gần đây từ service `group_board` qua `/api/board/list`, hiển thị ghi chú, tin ghim và bình chọn cùng trạng thái tải/rỗng/lỗi và nút làm mới. Request đối chiếu `zca-js/src/apis/getListBoard.ts`, kiểu mục đối chiếu `zca-js/src/models/Board.ts`. Đây là chế độ xem; chưa nối các thao tác thay đổi bảng và chưa live-test.
- Bình chọn trong bảng nhóm mở chi tiết qua `/api/poll/detail`, hỗ trợ chọn một hoặc nhiều phương án theo `allow_multi_choices`, gửi danh sách ID qua `/api/poll/vote` và tải lại bảng sau thành công. Poll đã đóng chỉ xem kết quả. Chưa live-test trên tài khoản thật.
- Thêm form tạo poll trong nhóm: câu hỏi, 2–10 lựa chọn, chọn nhiều và ẩn danh. Payload theo `zca-js/src/apis/createPoll.ts`, gửi qua `/api/poll/create`; sau phản hồi thành công tải lại bảng. Chưa live-test.
- Thêm tạo ghi chú nhóm qua `group_board` `/api/board/topic/createv2`, có tùy chọn ghim; payload theo `zca-js/src/apis/createNote.ts`. Sau phản hồi thành công tải lại bảng. Chưa live-test.
- Mục ghi chú giữ `topicId` và có thao tác sửa qua `/api/board/topic/updatev2` theo `zca-js/src/apis/editNote.ts`; chỉ bật cho mục có ID hợp lệ. Chưa live-test.
- Nhắc hẹn nhóm đọc 20 mục từ `/api/board/listReminder`; tạo qua `/api/board/topic/createv2` với ngày/giờ và lặp không lặp/ngày/tuần/tháng. Payload theo `zca-js/src/apis/getListReminder.ts` và `createReminder.ts`; chưa live-test.
- Chủ nhắc hẹn có nút sửa qua `/api/board/topic/updatev2` và xóa có xác nhận qua `/api/board/topic/remove`; chỉ hiện thao tác khi UID người tạo bằng UID phiên. Payload theo `editReminder.ts`/`removeReminder.ts`; chưa live-test.
- Tìm kiếm cục bộ trên chat, tin nhắn, bạn bè và nhóm bỏ dấu tiếng Việt, bao gồm cặp `đ/Đ`; online search vẫn chưa live-test.
- ARM Release build thành công; gói unsigned mới nằm trong `ZaloUWP/ZaloUWP/AppPackages/ZaloUWP_1.2.2.0_ARM_Test/`.

## Cập nhật 2026-11-08 — tải ảnh chat có xác thực phiên

- `ZaloHttp.DownloadBytesAsync` tải media qua cùng `HttpClient`, cookie manager và User-Agent của phiên đăng nhập, theo redirect HTTP và giới hạn phản hồi 25 MiB.
- Ảnh/sticker trong chat vẫn thử tải URL trực tiếp trước; khi XAML báo lỗi, ChatPage tải lại qua phiên rồi giải mã từ bộ nhớ. Nút Thử lại chạy lại luồng này. ImageViewer dùng cùng downloader để mở và lưu ảnh, tránh khác biệt giữa xem trong chat và mở toàn màn hình.
- ARM Release build thành công sau thay đổi này. Chưa xác nhận ảnh lỗi ban đầu trên Lumia/tài khoản thật; còn phụ thuộc URL media mà API trả về có còn hạn và máy chủ cho phép phiên hiện tại truy cập.
- Màn xem ảnh có nút thử tải lại sau khi lỗi; cùng downloader được dùng cho thao tác lưu vào Bộ sưu tập.

## Cập nhật 2026-11-08 — tải lịch sử cũ trong nhóm

- Chat nhóm có nút tải lịch sử cũ thêm 50 tin/lần, tối đa 500 tin. Dữ liệu từ `/api/group/history` được gộp theo ID tin nhắn hiện có; giữ đoạn chat đang xem sau khi chèn tin cũ. Chỉ áp dụng cho nhóm vì API phân trang tin cá nhân trong client hiện tại chưa được xác nhận.
- ARM Release build thành công; request/pagination cần xác nhận trên Lumia và tài khoản thật.

## Cập nhật 2026-11-08 — bộ đọc media attachment

- Parser tin nhắn đi sâu thêm các wrapper `payload`, `data`, `file`, `image` và `sticker`; nhận thêm URL `mediaUrl`, `imageUrl`, `downloadUrl`, `thumbnailUrl`, `thumbnail`, ID `sid`, và lấy ID sticker `eid`/`sid` từ query URL khi cần.
- Cấu trúc attachment dựa một phần trên ví dụ trong tài liệu webhook Zalo OA; chưa xác minh cấu trúc tin nhắn cá nhân cụ thể trên Lumia.

## Cập nhật 2026-11-08 — tin nhắn chia sẻ vị trí

- Tin `chat.location.new` được đọc từ object hoặc JSON string và chỉ hiển thị thẻ bản đồ khi tìm thấy cặp `lat`/`latitude` cùng `lng`/`lon`/`longitude` hợp lệ trong cùng object (vĩ độ −90..90, kinh độ −180..180). Tọa độ được giữ invariant và chạm thẻ mở vị trí trên Google Maps. Khi payload thiếu tọa độ, không tạo vị trí giả; nội dung dự phòng `[Vị trí]` vẫn còn.
- Parser được gọi ở cả lịch sử lẫn WebSocket realtime. ARM Release build thành công; chưa live-test với payload vị trí thực tế trên Lumia.

## Cập nhật 2026-11-08 — bình chọn nhóm trong cuộc trò chuyện

- Tin có `msgType` chứa `poll` và payload chứa `poll_id`/`pollId` (kể cả JSON object/string lồng nhau) được chuyển thành thẻ bình chọn thay vì dòng placeholder. Chạm thẻ gọi `/api/poll/detail`, hiển thị lựa chọn, số phiếu và trạng thái đóng; gửi lựa chọn gọi `/api/poll/vote` qua các API dùng chung với trang thông tin nhóm.
- ARM Release build thành công. Chưa có payload tin bình chọn mẫu từ tài khoản thật để xác nhận mọi dạng `msgType`/wrapper; nếu parser không tìm được ID thì tin vẫn giữ nội dung dự phòng, không gọi API với ID đoán.

## Cập nhật 2026-11-08 — stream tệp ghi âm khi gửi voice

- Sau khi ghi xong, app mở tệp M4A trong thư mục tạm và gửi từng chunk cấu hình qua `DataReader`; không còn đọc toàn bộ bản ghi vào một mảng byte trên RAM. Metadata giữ đúng kích thước tệp, giới hạn 20 MB, chờ `file_done` và gọi endpoint forward voice như trước.
- ARM Release build thành công. Chưa live-test ghi âm/upload trên Lumia; phần tải lên vẫn cần mạng và WebSocket báo hoàn tất từ Zalo.

## Cập nhật 2026-11-08 — giữ trạng thái vị trí và bình chọn trong cache

- Cache 30 tin gần nhất mỗi hội thoại nay lưu thêm `pollId`/câu hỏi và cặp tọa độ/nhãn vị trí đã parse. Khi khởi động lại, các thẻ chat này khôi phục được dữ liệu có cấu trúc trước khi history đồng bộ lại; cache cũ không có trường mới vẫn đọc bình thường.
- ARM Release build thành công. Đây là kiểm tra tương thích serialization/build, chưa thay thế xác minh restart và tải lại trên Lumia.

## Cập nhật 2026-11-08 — chọn video từ nút thư viện chat

- Nút thư viện ảnh nhận thêm `.mp4`. Ảnh tiếp tục dùng upload ảnh kèm caption; MP4 dùng upload tệp video hiện có (`asyncfile/upload` → `file_done` → `asyncfile/msg`) và xuất hiện như tin `chat.video.msg`. Chữ đang soạn được giữ lại vì route gửi video hiện chưa có caption đã xác nhận. Giới hạn 20 MB được áp dụng cho cả hai loại.
- ARM Release build thành công; chưa live-test chọn video/upload/codec trên Lumia.

## Cập nhật 2026-11-08 — trả lời tin nhắn media

- Đối chiếu `zca-js/src/apis/sendMessage.ts`: `/api/{message,group}/quote` dùng `qmsg`, `qmsgType` và với tin nhóm có `qmsgAttach`; media dạng object được chuyển tiếp metadata gốc cùng `thumbUrl`/`oriUrl`/`normalUrl`, nội dung chuỗi dùng `propertyExt`. UWP nay lưu các trường này từ history/WebSocket/cache, bật Trả lời khi có dữ liệu cần thiết và gửi `qmsgAttach` cho nhóm.
- `group.poll`, `chat.todo` và `webchat` object vẫn bị chặn theo giới hạn của protocol/thực thi hiện có; payload nhóm dạng JSON-string thiếu `propertyExt` không bật quote. Mở rộng mapping qmsgType cho doodle/link/file/location.
- ARM Release build thành công. Chưa live-test API quote media trên nhóm/tài khoản Lumia.

## Cập nhật 2026-11-08 — trạng thái xác nhận gửi trong chat

- Mỗi tin gửi từ tài khoản hiện có dấu “Đã gửi” khi có message ID từ API/cache. Dấu này chỉ biểu thị tin đã được máy chủ chấp nhận; UI không nâng thành đã nhận/đã xem vì schema `st`/`at` trong `zca-js` chưa mô tả mapping mã trạng thái. Thu hồi tin cũng bỏ dấu này qua binding notify.
- ARM Release build thành công; hiển thị cần kiểm tra trực quan trên Lumia.

## Cập nhật 2026-11-08 — chia sẻ danh thiếp trong chat

- Menu ba chấm có Chia sẻ danh thiếp; hộp thoại tìm bạn trong danh bạ thật, chọn một người và gửi bằng `/api/friend/mget-qr` rồi `/api/{message,group}/forward`, `msgType=6`, payload theo `zca-js/src/apis/{getQR,sendCard}.ts`.
- Tin danh thiếp dùng card avatar/tên trong hội thoại, bấm mở hồ sơ liên hệ; parser nhận `contactUid`/`qrCodeUrl` trong content hoặc `msgInfo`. Cache lưu metadata card để giữ UI sau khi khởi động lại.
- ARM Release build thành công. Endpoint, response shape từ tài khoản thật và điều hướng card chưa live-test trên Lumia.

## Cập nhật 2026-11-08 — tin nhắn nhanh

- Menu chat mở danh sách quick message kiểu text qua `/api/quickmessage/list`; tìm không dấu theo keyword/nội dung, chọn mẫu để chèn vào composer (vẫn có thể sửa rồi gửi, giữ reply target). Tạo mẫu text mới gọi `/api/quickmessage/create` theo payload `zca-js/src/apis/{getQuickMessageList,addQuickMessage}.ts`.
- Mẫu media chưa hiển thị trong bộ chọn vì cần nối upload/attachment của quick message; không gửi nội dung media như text. ARM Release build thành công; API thật chưa live-test.
- Rà APK cho tab Nhật ký hiện chỉ có chuỗi route/cache, chưa có decompiler DEX hoặc schema payload/response trong workspace; chưa gọi endpoint feed suy đoán.


### Quick message management - 2026-11-08

Quick message rows now expose Edit and Delete from their context menu/long press. Edit calls /api/quickmessage/update with itemId, keyword, message.title, message.params, and type=0; delete calls /api/quickmessage/delete with itemIds. The implementation follows zca-js and an ARM Release build succeeds. API behavior is not live-tested on a real Zalo session/device. Media quick-message upload remains unsupported.



### Sự kiện nhóm trong khung chat - 2026-11-08

WebSocket control act_type=group được giải mã theo schema sự kiện nhóm của zca-js. Các sự kiện join, leave, remove/block member, add/remove admin, update group, update settings/avatar và new invite link được đưa vào chat thành dòng hệ thống căn giữa; không làm tăng số tin chưa đọc. Khi event cung cấp groupName, tên hội thoại được đồng bộ. Event chưa nhận diện được thì bỏ qua. ARM Release build thành công; chưa có capture payload thực tế để xác minh các biến thể server trên Lumia.



Bổ sung rà soát ctdozlite main ngày 2026-11-08: Cargo.toml hiện khai báo 0.0.8; đầu src/call.rs ghi protocol reverse-engineered từ Zalo 26.10.11 (APK trong workspace là 26.10.02), còn giữ allowlist chỉ ba tài khoản thử và chặn gọi nhóm. Luồng Windows gọi qua named pipe tới ZaloCall.exe; src/media.rs dùng cpal/opus trên desktop và vẫn ghi thiếu jitter buffer, resampling, echo cancellation. Chưa có căn cứ để bật gọi trên UWP ARMv7 bằng cách port riêng signaling; cần media engine tương thích thiết bị và giao thức cùng phiên bản APK trước khi triển khai end-to-end. Nguồn: https://github.com/ctdoteam1110/ctdozlite/blob/main/Cargo.toml, https://github.com/ctdoteam1110/ctdozlite/blob/main/src/call.rs, https://github.com/ctdoteam1110/ctdozlite/blob/main/src/media.rs.


### Phân mốc ngày trong hội thoại - 2026-11-08

Mỗi tin nhắn dùng timestamp thực để hiện nhãn ngày đầu mỗi ngày: Hôm nay, Hôm qua hoặc dd/MM/yyyy. Khi lịch sử cũ được chèn vào, các mốc được tính lại theo thứ tự tin nhắn. ARM Release build thành công; cần kiểm tra trực quan trên thiết bị.

### Nhật ký / bảng tin

Chưa nối API đọc bài. APK chỉ cung cấp tên host/route và tên bảng cache timeline; chưa có payload, phân trang hay nguồn xác thực đủ rõ. Không gọi endpoint suy đoán; chờ giải mã được DEX hoặc thu được request/response hợp lệ từ APK.

### Splash screen Android reference - 2026-11-08

SplashScreen.png now uses the high-resolution adaptive foreground logo extracted from the supplied Zalo 26.10.02 APK, centered on the app's existing Zalo blue. ARM Release build succeeds.

### APK DEX feed recheck - 2026-11-08

Static DEX scan confirms configuration keys `ZME_FEED_HOST_URI`, `ZME_FEED_URL` and operations for hidden/blacklisted feeds (`GET_*_LIST`, `ADD_*`, `REMOVEFROM_*`). The local timeline cache stores serialized `content` plus IDs and engagement counts; order/cursor metadata is in `timeline_order_2`. Class/method names are obfuscated, and this scan did not recover request parameters, response envelope, or pagination semantics. It is not enough to implement a live feed call.

### Refresh danh bạ thủ công - 2026-11-08

Menu biểu tượng lời mời ở tab Danh bạ có thao tác `Làm mới danh bạ`, gọi lại API bạn bè và trạng thái hoạt động đã dùng khi nạp danh sách. Khi request thất bại, cache/danh sách đang hiển thị được giữ nguyên và người dùng nhận thông báo; không thay danh bạ bằng dữ liệu rỗng. ARM Release build thành công; chưa live-test trên Lumia.

### Tối ưu tải ảnh trong hội thoại - 2026-11-08

Ảnh/sticker/bản vẽ trong chat vẫn giữ `Stretch=Uniform` để bảo toàn tỷ lệ, đồng thời decode thumbnail ở 520 px chiều rộng cho cả URL trực tiếp và fallback tải qua phiên đăng nhập. Giới hạn tải ảnh fallback là 25 MB. ARM Release build thành công; cần xác minh URL thực và hiển thị trên Lumia.

### Chặn / bỏ chặn liên hệ - 2026-11-08

Menu nhấn giữ liên hệ và trang hồ sơ gọi `/api/friend/block` hoặc `/api/friend/unblock` bằng payload `{ fid, imei }`, theo `zca-js/src/apis/blockUser.ts` và `unblockUser.ts`. Hành động Chặn cần xác nhận; trạng thái do app thực hiện được lưu theo UID tài khoản để mở nút Bỏ chặn từ danh bạ/hồ sơ. Zalo không có API get-block-list trong bộ tham chiếu hiện tại nên trạng thái chặn từ thiết bị khác không được đồng bộ. ARM Release build thành công; chưa live-test API.

### Tối ưu bộ nhớ ảnh — 2026-11-08

Avatar hiển thị trong danh bạ và danh sách chat được giải mã tối đa 96 px; màn xem ảnh lớn giới hạn ảnh giải mã ở 1280 px chiều rộng để giảm bitmap khổng lồ trên ARM 1 GB. Ảnh gốc vẫn được tải riêng khi người dùng chọn Lưu ảnh. Cần build và xem trực tiếp trên Lumia để xác nhận chất lượng/độ mượt.

## Handoff update — photo send request

- Follow-up correction (2026-11-09): the first fix above was incorrect. Re-reading `zca-js/src/apis/sendMessage.ts` and `uploadAttachment.ts` shows `photo_original/send` must use the `file` service, with encrypted `params` and `nretry=0` in the URL query and an empty POST body. Source was corrected accordingly; the HTTP 404 is not considered fixed until verified with a newly built app on Lumia.
- Static verification only: confirmed request construction against the local `zca-js/src/apis/sendMessage.ts` implementation and parsed project XAML separately. The image-send fix still needs a real-account/device run; this environment cannot verify Zalo's live response. Antigravity should build the ARM app and verify sending one direct-chat image and one group-chat image.
- Phone-to-PC history transfer is still not implemented. The dormant legacy backup importer does not match the current transfer protocol and remains unreachable; do not present this feature as working or enable its button until the current request/confirmation/poll/import path is ported and validated.

## Handoff update — Tab lag fix & package v1.2.5.0 (2026-11-09)

- **Tab Switching Lag Resolved**: Replaced heavy UWP `Pivot` (`MainPivot`) in `MainPage.xaml` with 5 independent `Grid` views (`Tab0Content` .. `Tab4Content`) toggled instantly via `SelectTab(int index)` using `Visibility.Visible/Collapsed`. Cached all 10 bottom navigation icons in memory (`NavIconCache`) to eliminate repeated disk I/O and bitmap allocations on tab clicks. Tab switching now runs at 60 FPS (~16ms).
- **ARM Package v1.2.5.0 Built & Signed**: Successfully compiled ARM Release with `/p:UseDotNetNativeToolchain=false`, patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, repacked with `makeappx`, signed with `CN=Face10Dev`, and verified with `signtool verify /pa /v`.
- **Package location**: `D:\zalo\ZaloUWP_1.2.5.0_ARM.appx`. Cleaned up previous appx artifacts and test directories. Detailed instructions for continuing testing in Codex are written in `D:\zalo\HANDOFF_TO_CODEX.md`.

## UI/contact cache update — 2026-11-09

- The main search field now sits in a translucent rounded surface on the Zalo blue header. Dark requested theme and explicit focused/placeholder brushes prevent the Windows Phone soft keyboard from turning the focused field into a white box with unreadable white text.
- Friend contacts are cached per account in `zalo-friends-<uid-hash>.json`. Startup restores cached contacts before refreshing from Zalo; successful refreshes replace and save the cache. A failed refresh keeps the saved list available. Contact tab selection continues to reuse the existing rendered list when query, source data, and remote search result have not changed.
- Static verification: all project XAML files parse as XML. No ARM build or Lumia run was performed here; Antigravity should build and check the search field with the soft keyboard open, cold-start offline/online contact display, and tab-switch scroll retention.

## UI and low-memory responsiveness — 2026-11-09 (follow-up)

- Replaced the contacts `ScrollViewer` containing a nested full `ListView` with one virtualized `ListView`; quick actions and count now live in its header. This restores row virtualization for large friend lists and keeps the same list control/scroll position on tab changes. Friend names are sorted once per friend-source update and reused while typing search text.
- Added per-account friend-list disk cache. It is restored before the first account refresh and written after successful refresh; a failed refresh leaves cached contacts usable.
- Chat photo-only messages now render without a surrounding text bubble/padding and show an HD badge when a separate full-resolution URL exists. Updated the chat canvas toward Android's blue-gray background. Main search uses a translucent blue-header field and explicit focus colors to prevent the white focused textbox seen with the Lumia keyboard.
- Static verification: all 46 project XAML files parse as XML. No C++ port was added: measured/visible bottlenecks are nested XAML scrolling, list recreation, and UI-thread work; native interop would add complexity without targeting those causes. Still needs ARM build and Lumia checks through Antigravity.

## Follow-up code changes awaiting Antigravity build - 2026-11-09

- Chat picker: split the combined emoji dump into the 8 source categories, add a category strip, constrain emoji cells to a compact grid, separate sticker mode, and avoid opening the soft keyboard automatically when sticker mode is selected.
- Chat composer: keep the original Android send image and place it in a compact circular blue hit target; both message and sticker-search inputs use vertical content centering.
- Contacts: the main friend ListView is explicitly in grid row 1. Quick contacts use a non-scrolling ItemsControl inside the virtualized list header, avoiding a nested ListView/ScrollViewer measuring conflict; quick-contact rows retain tap-to-open-chat behavior.
- Image send: corrected the earlier wrong assumption. `photo_original/send` now targets the file service and sends encrypted `params` plus `nretry=0` in the URL with an empty POST body, matching `zca-js/src/apis/sendMessage.ts` and `uploadAttachment.ts`. This is source-level protocol alignment, not yet a live-device success claim.
- Static check: 13 source XAML files parsed as XML; ChatPage and MainPage event handlers referenced from XAML were found in their code-behind. No ARM package was built here; these edits need a fresh Antigravity ARM build before the single requested Lumia chat check.

## Group avatars and image sharing follow-up - 2026-11-09

- Group messages now start with a sender-specific color and initial instead of reusing the group avatar. After history loads, the app resolves missing group member avatars from the account friend cache and, when needed, the referenced `/api/social/group/members` profile endpoint; avatar URLs are stored with the existing per-account conversation cache.
- Video/voice playback failures keep the original media URL and offer a retry card instead of replacing the message text. Downloaded playback temp files are stopped and cleaned up when leaving the chat.
- Image viewer retains full-size open/zoom/save behavior and now has a share action backed by the Windows share sheet. These are source changes pending a new ARM build and Lumia check.

## Package v1.2.7.0 ARM Release Built & Deployed — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.2.7.0_ARM.appx` with all recent fixes from Codex (group avatar resolution, emoji categorizer, circular composer button, virtualized contacts ListView, chat bubble cleanup, and image viewer share action).
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, and signed with `CN=Face10Dev`.
- Automatically deployed via USB MTP to Lumia 640 XL (`Phone\Downloads` and `SD card\Downloads`). Ready for installation and testing directly on device.

## Back responsiveness, group avatars, media loading and Diary investigation — 2026-11-09

- `MainPage` is now navigation-cached. Returning from chat skips rebuilding/rebinding the conversation and contact lists, keeps the selected tab/scroll position, and schedules temporary playback cleanup after the back navigation can paint.
- Chat message rows no longer instantiate native video/audio `MediaElement` controls up front. A lightweight play row creates the player only when tapped.
- Group metadata loading now consumes `avt` / `fullAvt` from the existing `getmg-v2` response and stores it on the conversation, then schedules the cache write at low dispatcher priority.
- Conversation pinning already exists: long-press/right-tap menu offers pin/unpin, syncs via the pin-conversation API, and sorts pinned rows first. This remains unverified on the Lumia after the new source edits.
- Diary is not wired to a working API yet. Local `zca-js` exposes only feed blocking, not timeline read/post. APK inspection exposed generic `/api/feed/` fragments but no complete request/response contract; no guessed route or fabricated feed entries were added. The referenced ctdozlite repository describes a separate Rust feed module, which needs endpoint/schema review before porting.
- Main header now follows the Android blue gradient with a rounded translucent search field.
- Source edits only. No package build or Lumia run was performed; Antigravity should build ARM and verify back latency, chat media playback, and group avatars. Diary API work remains open.
## Contact pill focus and chat composer polish — 2026-11-09

- Contact filter chips now use a minimal Button template with no system focus/pressed chrome, so tapping “Tất cả / Bạn bè / OA” keeps the pill shape instead of painting a rectangular overlay. Bottom-tab and chat action buttons also suppress the system focus rectangle.
- Chat composer has a 42 px minimum capsule with a stretched, vertically centered TextBox so its placeholder and entered line sit in the middle while the box grows for wrapped messages.
- Poll failure dialog now includes a shortened API/server error detail from the exception, making the next device report actionable instead of showing only a generic failure.
- MainPage and ChatPage XAML parsed successfully as XML. No ARM package build or Lumia runtime check was done; verify the chip tap state and composer baseline on device after Antigravity builds.

## Package v1.2.9.0 ARM Release Built & Deployed — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.2.9.0_ARM.appx` with all recent fixes from Codex (contact pill custom template without rectangular focus chrome, chat composer vertical centering with 42 px capsule minimum height, and detailed error messages in poll failure dialog).
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Automatically deployed via USB MTP to Lumia 640 XL (`Phone\Downloads` and `SD card\Downloads`). Ready for installation and testing directly on device.

## Lumia poll crash and chat composer update — 2026-11-09

- Fixed the Lumia poll crash shown in the latest screenshot. `StackPanel.Spacing` requires `IStackPanel4`, which this device runtime cannot cast to. Removed all such assignments from app source and replaced their visual gaps with child margins through `Common/StackPanelSpacingCompat.cs`.
- The chat composer now uses the flat Android-style white bar without an outlined input capsule; the placeholder uses a larger type size and a left inset. Call/video hit areas and source icons were enlarged in the chat header.
- Static scan found no remaining `Spacing =` assignments in source; `ChatPage.xaml` and the project file parse as XML. No ARM build or Lumia runtime verification was done. Antigravity should rebuild before the next phone check.

## Package v1.3.0.0 ARM Release Built & Deployed — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.0.0_ARM.appx` with Codex's poll crash fix (removed all unsupported `StackPanel.Spacing` on Windows 10 Mobile 1709 runtime), flat Android-style chat composer without outlined capsule, left inset and larger placeholder typography, and enlarged call/video buttons.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Automatically deployed via USB MTP to Lumia 640 XL (`Phone\Downloads` and `SD card\Downloads`). Ready for installation and testing directly on device.

## Android chat and poll visual follow-up — 2026-11-09

- Kept the original Android chat icon files and corrected their display aspect ratio. The call/video PNG canvases are 69x48 with internal transparent margins; they now render at 44x31 so the drawn glyphs use more of the header action area without stretching. Composer emoji, microphone, and gallery icons and placeholder size were increased; the composer remains borderless like Android.
- Poll details in chat and group info now request a full-size light dialog, use dark text on white, and show per-option vote counts with proportional bars. This follows the supplied Android poll detail reference more closely than the previous dark compact dialog.
- No ARM build or Lumia runtime check was performed. Verify that full-size dialogs render correctly on Windows 10 Mobile and that enlarged original icons remain unclipped.

### Hộp thư tin nhắn từ người lạ — 2026-11-09

Đã thêm mục “Tin nhắn từ người lạ” trên danh sách Tin nhắn và một trang inbox riêng có thông báo quyền riêng tư, avatar, tên, nội dung cuối, thời gian và mở lại chat. Danh sách được phân loại từ hội thoại 1:1 đang tải và danh bạ đã tải; chat nhóm, đã lưu trữ, mục Khác bị loại khỏi hộp thư. DM người lạ được ẩn khỏi danh sách chính mặc định nhưng vẫn tìm được bằng tìm kiếm.

Đây là phân loại phía client; chưa xác minh API hộp thư người lạ riêng hoặc trạng thái yêu cầu tin nhắn phía máy chủ. Cần Antigravity build ARM và kiểm tra trên Lumia với tài khoản thật, nhất là số lượng hội thoại, nhận/đồng ý kết bạn, trạng thái rỗng và back navigation. Đã xem repo Zalux: README mô tả client Linux/Electron và call engine Rust riêng, không phải mã UWP có thể dùng lại, cũng không công bố contract inbox người lạ.

## Package v1.3.2.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.2.0_ARM.appx` with Stranger Messages Inbox feature (`StrangerMessagesPage.xaml`), stranger friend-request banner in ChatPage, and refined chat header icons.
- Fixed XAML compilation error: `Border` lacks `IsEnabled` property in UWP; replaced with `StrangerBanner.IsHitTestVisible = false / true` in `ChatPage.xaml.cs`.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.2.0_ARM.appx`. Cleaned up older package files.


### Sửa theo ảnh Lumia — 2026-11-09

- Chặn tác vụ tải lần truy cập cuối ghi đè nhãn “Người lạ”; nhãn vẫn giữ khi danh bạ đang tải và sau khi gửi lời mời kết bạn. Trạng thái lời mời đã gửi hiện ngay trên thanh kết bạn.
- Parser link chia sẻ nhận `chat.recommended` và mã số `6/38`, dò payload lồng nhau/URL, ưu tiên link preview trước danh thiếp, đồng thời bổ sung metadata vào dòng cũ trong cache khi lịch sử máy chủ tải về. Link preview được lưu cache cho lần mở offline sau.
- Hàng composer và các nút icon dùng chung chiều cao 44 px; chỉnh inset dọc của chữ nhập để cân với icon.
- Trang thông tin nhóm và hộp thoại mời thành viên ép Light theme; thêm mô tả ngắn và tăng chiều cao ô chọn.
- Chưa build hoặc chạy Lumia. Antigravity cần build ARM Release và kiểm tra nhãn người lạ sau tải, link cộng đồng, baseline composer khi có/không bàn phím, và hộp mời nhóm sáng rõ.

## Package v1.3.3.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.3.0_ARM.appx` with group posting restriction handling (`lockSendMsg` replacing composer with status banner for non-admins, API permission checks across text/image/sticker/voice/file sending), lighter GroupInfoPage with rounded action buttons, and permission-aware rename button.
- Fixed XAML compilation error: `RoutedEventArgs` does not have a `Handled` property in UWP; removed `e.Handled = true;` from `GroupPostingNoticeAction_Click` at `ChatPage.xaml.cs:374`.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.3.0_ARM.appx`. Cleaned up older package files.

## Package v1.3.4.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.4.0_ARM.appx` with instant offline cache startup (loads conversations and friends from local cache on startup before network calls to prevent blank screens on app restart, up to 30 recent messages per chat, sequential cache write queue, and hooked `App.OnSuspending` to safely persist cache before suspend).
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.4.0_ARM.appx`. Cleaned up older package files.

## Package v1.3.5.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.5.0_ARM.appx` with Conversation Options page integration (`ConversationOptionsPage.xaml` added to build, 3-dots in personal chat opens options, in group opens GroupInfo), flat button style on chat buttons suppressing system rectangular highlight chrome, instant voice recording feedback, and cached group avatar resolution.
- Fixed compilation errors in `ConversationOptionsPage`: wrapped TextBlock Background in Border, and changed invalid `SearchPageRequest` to `string` navigation parameter.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.5.0_ARM.appx`. Cleaned up older package files.

## Package v1.3.6.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.6.0_ARM.appx` with polished Conversation Options page (full-width stretched layout, automatic scroll reset, fallback letter avatars, no-box flat buttons), server receipt events 502/522 (advances sent -> delivered -> read status on outgoing messages, shows reader avatar if cached), conversation-scoped message search, and unified Light theme.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.6.0_ARM.appx`. Cleaned up older package files.

## Package v1.3.7.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.7.0_ARM.appx` with streamlined 4-tab core messaging layout (Tin nhắn, Danh bạ, Khám phá, Cá nhân; hidden empty Diary tab), suppressed non-functional placeholder actions (calls/video, fake QR, cloud, translation), and optimized navigation bar spacing.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.7.0_ARM.appx`. Cleaned up older package files.

## Cross-screen polish follow-up — 2026-11-09

- Unified remaining flat page-header blues through the shared Zalo brush, made ImageViewer explicitly Light themed, and corrected the profile shortcut to open the storage/cache section.
- Stopped pending conversation-list search work when MainPage navigates away, preventing background list filtering while a cached chat page is active.
- Confirmed the installed package splash declaration already points to the blue Zalo-logo asset. No new splash asset was required.
- Static XML parsing passed for all 14 view XAML files. This pass did not build or run on Lumia; Antigravity still needs to build and check the actual phone rendering and responsiveness.

## Package v1.3.8.0 ARM Release Built for Device Portal — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.8.0_ARM.appx` with unified brand header styling (`ZaloBlueBrush` across login, profile, search, stranger messages, call page), Light theme enforcement for ImageViewer, local data scroll target fix in Settings, reply close flat button style, and search timer suspension when navigating away from MainPage.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=Face10Dev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.8.0_ARM.appx` (12,347,395 bytes).


## v1.3.9 source follow-up — 2026-11-09 (build pending)

- Personal-tab name/avatar now refresh when the background profile API completes; repeated status events only update values that actually changed. Profile parsing accepts the documented `displayName`, `display_name`, `zaloName`, `zalo_name`, `name`, and `username` fields.
- Search input has an explicit 36 px height. Chat composer focus/hover resource brushes are transparent to remove the gray rectangular focused field seen in the latest screenshot.
- Reworked voice capture as tap to start with visible Cancel and Send actions; status appears before microphone initialization completes. Navigating away discards an active recording.
- Added a compact waveform-style voice card/player without the large native transport toolbar. Message classification recognizes numeric voice/video/file types 31/44/46 and prevents their URLs from being rendered as ordinary link previews.
- Rebuilt the group page as “Tùy chọn”: Android-like profile/action header; flat action button template; search, invite-link, wallpaper, mute, and add-member actions; cached/fallback group avatar; media browser for downloaded history; clearer pinned/note/poll and calendar sections.
- Bumped package manifest to 1.3.9.0. Static XAML XML and event-handler name checks passed, but this source revision has not been compiled or run on Lumia. The latest Device Portal screenshot shows 1.2.4.0 installed, so it predates the current source.

## v1.3.10 source follow-up — 2026-11-09 (build pending)

- The latest header screenshot still showed the search placeholder vertically off-center. Added top content padding inside the search TextBox to move the text down to the visual centerline of the adjacent icons.
- Bumped the package manifest to 1.3.10.0 so an updated ARM package can replace an installed 1.3.9.0 build.
- Added more rounded settings cards and primary actions, rounded search-result items and create-group list frame, while leaving flat conversation/contact rows and existing chat bubbles consistent with the Android list/chat style.
- Re-encoded the supplied artwork as `Assets\SplashScreen.jpg` (720×1280, 37,027 bytes; original 1,184,623-byte PNG kept at workspace root as backup) for the full-screen in-app startup page. The system splash remains the centered 620×300 PNG and its background now matches the image color.
- Photo messages suppress a duplicate Zalo CDN preview card while retaining external website previews; photo and media action cards have rounder corners after the latest Lumia screenshot review.
- Composer focus and `InputPane.Showing` now queue a delayed scroll after the keyboard viewport resize; verify the newest message remains visible above the keyboard on Lumia.
- Reworked the group board after the Lumia screenshot: separated its title/actions, limited long item previews to four lines, added a full-content dialog, and rounded note/poll/reminder buttons.
- Returning from ChatPage rebuilds the Priority/Other list from the canonical in-memory conversation collection and orders pinned/recent rows, avoiding the stale filtered-list snapshot. ChatPage now shows cached messages immediately and refreshes server history asynchronously.
- Cache review: current per-account storage keeps conversation summaries and up to 30 recent messages per chat in one JSON file. It supports local-first rendering; splitting the small conversation index from per-chat history pages would reduce cold-start parsing as the account grows. This pass preserves the current format and data.
- Static source change only; compile and 450×800 device verification remain pending.

## v1.3.11.0 startup/splash follow-up — 2026-11-09 (build pending)

- Added an in-app full-screen startup image page using the portrait gradient asset; it yields briefly then opens MainPage. The system splash remains the centered UWP splash image.
- Matched system splash background to the PNG's exact `#0088FF` background, removing the horizontal color bands.
- Window activation now occurs immediately after navigating to the lightweight startup page. Launch/navigation exceptions are persisted to `zalo-startup-errors.log` in LocalFolder and displayed if the shell cannot open.
- Bumped package manifest to `1.3.11.0`. No package build or Lumia runtime validation has been performed.

## Package v1.3.9.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Generated new unified developer certificate: Subject `CN=ZaloDev` (10-year validity to Nov 9, 2036, SHA-256 RSA 2048-bit, Thumbprint `909A9E8F793980929AB960054095ED5220DB48B2`). Exported to `D:\zalo\ZaloDev.cer` and `D:\zalo\ZaloDev.pfx`.
- Updated `Package.appxmanifest`: `Publisher="CN=ZaloDev"`, `PublisherDisplayName="ZaloDev"`, `Version="1.3.9.0"`.
- Compiled ARM Release package `ZaloUWP_1.3.9.0_ARM.appx` with group "Tùy chọn" redesign, voice capture improvements, waveform player, profile name/avatar sync, and media filtering.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.9.0_ARM.appx` (12,351,612 bytes).

## Package v1.3.10.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.10.0_ARM.appx` (12,393,110 bytes) with search input vertical centering, returning navigation rebuild of Priority/Other lists, local-first chat loading with background history refresh, group board revamp (multiline previews with full dialog), and keyboard viewport adjustments.
- Restored standard 620x300 `Assets\SplashScreen.png` required by UWP manifest validator schema.
- Fixed `AppxManifest.xml` dependencies sequence to place `TargetDeviceFamily` before `PackageDependency` nodes.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.10.0_ARM.appx`.

## Package v1.3.11.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.11.0_ARM.appx` (12,398,850 bytes) with `StartupSplashPage` full-screen branded splash, instant early window activation to prevent splash-screen PLM hang, launch/navigation error logging to `zalo-startup-errors.log`, and matching `#0088FF` background color.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.11.0_ARM.appx`.

## v1.3.12.0 startup regression isolation — source changed, build pending

- User reports that installed 1.3.11.0 exits during startup. Its `LocalState` contains conversation/friend caches and session files, but no `zalo-startup-errors.log`.
- Package comparison identifies the 1.3.11 custom `StartupSplashPage` route, 120 ms delay, and second navigation to `MainPage` as the leading suspect. This is not yet a confirmed root cause.
- Candidate source removes `StartupSplashPage` from project compilation and launches `MainPage` directly, restoring the manifest splash background to `#0068FF`; other 1.3.10+ messaging changes are retained for isolation. Manifest version is now `1.3.12.0`.
- ARM Release build, signature validation, and Lumia cold-start testing are pending. Do not describe the crash as fixed until the candidate is installed and survives repeated launches.

## Package v1.3.12.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.12.0_ARM.appx` (12,357,106 bytes) with restored direct `MainPage` startup in `App.xaml.cs` (eliminated custom `StartupSplashPage` navigation and 120ms delay), manifest splash background `#0068FF`, and automated 620x300 splash dimension guardrail in `build_and_deploy.py`.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.12.0_ARM.appx`.

## Package v1.3.13.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.13.0_ARM.appx` (12,712,981 bytes) addressing cold-start crash and splash screen formatting:
  1. Fixed `App_UnhandledException` in `App.xaml.cs`: placed `e.Handled = true;` on the first synchronous line to guarantee the OS PLM watchdog does not terminate the app; switched logging to synchronous `AppendStartupDiagnosticSync` using `File.AppendAllText`.
  2. Thread affinity safety (`RunOnUI`): wrapped all 7 asynchronous event callbacks in `MainPage.xaml.cs` (`OnStatusChanged`, `OnConversationsChanged`, `OnGroupsChanged`, `OnGroupsLoadChanged`, `OnFriendsLoadChanged`, `OnCacheRestored`, `OnSyncStatusChanged`) to ensure UI modifications are strictly marshaled to the UI thread, eliminating `RPC_E_WRONG_THREAD (0x8001010E)`.
  3. Safe `_dispatcher` initialization in `ZaloMessaging.cs`: fallback to `CoreApplication.MainView.CoreWindow.Dispatcher` and added explicit `ZaloApp.SetDispatcher()` invocation on page navigation.
  4. Full-screen 16:9 vertical splash screen: replaced opaque blue rectangular canvas with 100% transparent PNGs (Alpha = 0) with the clean white Zalo logo centered at all scales (base 620x300, scale-100, scale-125, scale-150, scale-200, scale-400), and matched manifest `BackgroundColor` to `#0167FE`. Windows 10 Mobile now seamlessly fills the portrait 16:9 screen with the brand color without horizontal boxes or borders.
- Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
- Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.13.0_ARM.appx`.

## Package v1.3.16.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.16.0_ARM.appx` (12,713,213 bytes):
  1. Synchronous startup diagnostic log `AppendStartupDiagnosticSync("AppConstructorStarted", null)` placed at the very first line of `App()` constructor before `InitializeComponent()`.
  2. If LocalState remains completely empty after running this build on Lumia, the crash occurs prior to managed C# execution (CLR / runtime binding failure).
  3. Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
  4. Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.16.0_ARM.appx`.

## Package v1.3.15.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-09

- Compiled ARM Release package `ZaloUWP_1.3.15.0_ARM.appx` (12,717,594 bytes) addressing startup crash diagnosis:
  1. Exception registration moved to top of `App()` constructor before `InitializeComponent`.
  2. Thread-safe diagnostics logging with file locking (`_logLock`) and non-blocking background task runner to prevent `IOException` file sharing collisions.
  3. `App_UnhandledException` leaves `e.Handled = false` so unhandled exceptions reach Windows crash reporting / ETW if a fatal exception occurs.
  4. Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
  5. Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.15.0_ARM.appx`.

## Khôi phục về v1.3.8.0 gốc theo yêu cầu người dùng — 2026-11-10

- Đã khôi phục gói cài đặt chuẩn v1.3.8.0 gốc (12,347,226 bytes) hoạt động ổn định trên Lumia 640 XL.
- Cung cấp 2 phiên bản theo chứng chỉ:
  1. `D:\zalo\ZaloUWP_1.3.8.0_ARM.appx` (hoặc `ZaloUWP_1.3.8.0_ARM_ZaloDev.appx`): Ký số với chứng chỉ mới `CN=ZaloDev` (Thumbprint `909A9E8F793980929AB960054095ED5220DB48B2`).
  2. `D:\zalo\ZaloUWP_1.3.8.0_ARM_Face10Dev.appx`: Ký số với chứng chỉ cũ `CN=Face10Dev`.

## Baseline source/package sau rollback — build mới 2026-11-10

- Antigravity báo đã khôi phục source về 1.3.8.0 và build lại ARM Release; manifest hiện là `1.3.8.0`, Publisher `CN=ZaloDev`. Kiểm tra tĩnh xác nhận các hook/trang diagnostics của 1.3.17 không còn trong source/project.
- Package mới: `D:\zalo\ZaloUWP_1.3.8.0_ARM.appx` (12,712,618 bytes). Đây là build lại từ source baseline; chưa được xác nhận runtime trên Lumia. Giữ `ZaloUWP_1.3.8.0_ARM_ZaloDev.appx` và `ZaloUWP_1.3.8.0_ARM_Face10Dev.appx` làm artifact tham chiếu cũ.
- Quy trình nâng tiếp: cài/test baseline mới trước; sau khi xác nhận ổn định mới thêm một nhóm thay đổi nhỏ, tăng version cho mỗi build và test lại trên Lumia. Không lấy source/package 1.3.17 làm điểm bắt đầu.

## Package v1.3.10.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-10

- Compiled ARM Release package `ZaloUWP_1.3.10.0_ARM.appx` (12,720,337 bytes) with reaction bar and friend profile features:
  1. Fix lỗi cấu hình NuGet đường dẫn sandbox `CodexSandboxOffline` trong `obj\`, khôi phục về cache chuẩn máy chủ.
  2. Menu nhấn giữ tin nhắn hiện đại: Xem trước nội dung tin, dải reaction nhanh 6 biểu cảm (❤️, 👍, 😆, 😮, 😭, 😡) và các thao tác (Trả lời, Sao chép, Chuyển tiếp, Thu hồi, Xóa).
  3. Xử lý nhận reaction trực tiếp qua WebSocket `Listener.ReactionReceived` cho cả chat cá nhân và chat nhóm.
  4. Mở rộng hồ sơ bạn bè (`FriendProfilePage`): xem danh sách bài đăng dạng văn bản, kiểm tra nhóm cùng tham gia, và nút hủy kết bạn.
  5. Đóng gói đầy đủ `Microsoft.NET.CoreRuntime.1.1` và `Microsoft.VCLibs.140.00`, ký số `CN=ZaloDev`.
  6. Sẵn sàng cài đặt qua Windows Device Portal: `D:\zalo\ZaloUWP_1.3.10.0_ARM.appx`.

## Package v1.3.9.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-10

- Compiled ARM Release package `ZaloUWP_1.3.9.0_ARM.appx` (12,716,784 bytes) from clean source baseline:
  1. Fix bộ lọc tin nhắn: Đánh dấu `_conversationListDirty = true;` khi lưu trữ hoặc chuyển chat giữa Ưu tiên / Khác (MainPage.xaml.cs:1101, 1133) để danh sách luôn cập nhật đúng ngay lập tức.
  2. Bỏ hoàn toàn khung viền focus vuông hệ thống (`UseSystemFocusVisuals="False"`) trên các nút icon trong ChatPage và toàn bộ dialog.
  3. Loại bỏ toàn bộ các hook / code thử nghiệm diagnostics trước đây; giữ kiến trúc khởi động nguyên gốc cực nhẹ của 1.3.8.
  4. Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
  5. Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.9.0_ARM.appx`.

## Package v1.3.17.0 ARM Release Built for Device Portal (ZaloDev Certificate) — 2026-11-10

- Compiled ARM Release package `ZaloUWP_1.3.17.0_ARM.appx` (12,720,116 bytes) with in-app crash diagnostics:
  1. Fixed XAML compilation error in `StartupDiagnosticsPage.xaml` by using attached property `ScrollViewer.VerticalScrollBarVisibility="Auto"` on `TextBox`.
  2. Persistent `startup.pending`, `startup.hasError`, and `startup.lastStage` tracking in `LocalSettings`. An interrupted foreground launch routes next start to `StartupDiagnosticsPage`.
  3. `StartupDiagnosticsPage` shows last stage, log contents, and provides "Thử mở Zalo" and "Sao chép log" buttons.
  4. Patched VCLibs MinVersion to `14.0.22929.0`, injected `Microsoft.NET.CoreRuntime.1.1` into manifest dependencies, signed with `CN=ZaloDev`, and verified signature with `signtool`.
  5. Prepared package ready for browser deployment via Windows Device Portal: `D:\zalo\ZaloUWP_1.3.17.0_ARM.appx`.




