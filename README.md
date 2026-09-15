# Vigear Multi-Warehouse API

API ASP.NET Core MVC chỉ đọc dữ liệu TikTok Shop để kiểm tra lệch kho và tồn theo SKU. Cửa sổ doanh số mặc định là **60 ngày**.

## Chức năng

- `GET /oauth/tiktok/start`: bắt đầu ủy quyền Seller qua TikTok Shop.
- `GET /oauth/tiktok/callback`: nhận kết quả ủy quyền. Địa chỉ này phải trùng với Redirect URL trong Partner Center.
- `POST /api/sync/run`: chạy báo cáo ngay; yêu cầu header `X-Admin-Key`.
- `GET /api/reports/latest`: lấy báo cáo đã tổng hợp, không bao gồm tên, số điện thoại hay địa chỉ chi tiết của khách.
- Tự chạy mỗi ngày theo `Audit:RunAtLocalTime`; kết quả được gộp thành một báo cáo để tránh gửi nhiều tin.

Quy tắc đã đặt:

| Mục | Thiết lập |
|---|---:|
| Cửa sổ bán để tính bình quân | 60 ngày |
| Tồn HCM tối thiểu | 15 ngày bán miền Nam |
| Tồn HN tối thiểu | 45 ngày bán toàn shop |
| Ranh giới kho HN | đến hết Đà Nẵng |
| Ranh giới kho HCM | từ Quảng Nam trở vào |

## Chuẩn bị IIS

1. Cài **.NET 8 Hosting Bundle** trên máy IIS và tạo App Pool `No Managed Code`, `AlwaysRunning`.
2. Publish dự án cho `win-x64`, rồi tạo website HTTPS trong IIS. Đặt `preloadEnabled="true"` cho ứng dụng để lịch chạy hằng ngày không bị IIS ngủ.
3. Trong cấu hình bảo mật của IIS hoặc secret manager, đặt `TikTokShop__AppKey`, `TikTokShop__AppSecret`, `TikTokShop__ServiceId`, `Audit__AdminApiKey`. Không đưa các giá trị này vào source code, Git hay chat.
4. Đặt `TikTokShop__OAuthRedirectUri` thành URL HTTPS thực tế, ví dụ `https://api.tenmiencuaban.vn/oauth/tiktok/callback`, rồi cập nhật đúng URL đó tại Partner Center → Vigear AI.
5. Giới hạn đường dẫn `/oauth/tiktok/start` bằng xác thực IIS hoặc chỉ cho phép IP quản trị, rồi mở `https://api.tenmiencuaban.vn/oauth/tiktok/start`, đăng nhập Seller và bấm ủy quyền. Sau đó gọi `POST /api/sync/run` để lấy báo cáo đầu tiên.
6. Lấy hai mã kho từ báo cáo đầu tiên, điền chúng vào `Audit__HanoiWarehouseIds__0` và `Audit__HcmWarehouseIds__0`. Việc này chặn suy đoán nhầm khi tên kho thay đổi.

## TikTok Shop API được gọi

- `GET /logistics/202309/warehouses`
- `POST /order/202309/orders/search`
- `POST /product/202502/products/search`
- `POST /product/202309/inventory/search`
- `GET /authorization/202309/shops` khi chưa cấu hình `ShopCipher`

Tất cả request sử dụng quyền đọc đã chọn: logistics, order information và product basic. Không có endpoint nào sửa đơn, tồn hoặc cấu hình kho.

## Zalo

Để gửi bản tổng hợp vào nhóm **Cảnh báo hàng hoá, đa kho**, cấu hình một webhook của dịch vụ Zalo/OA nội bộ vào `Notifications__WebhookUrl` và bật `Notifications__Enabled=true`. API chỉ gửi một payload tổng hợp khi có phát hiện mới trong lượt chạy; việc gửi Zalo cần thông tin xác thực của kênh Zalo do doanh nghiệp quản lý. `GET /api/reports/latest` và `POST /api/sync/run` đều yêu cầu header `X-Admin-Key`.

## Kiểm tra

Máy Windows triển khai cần .NET SDK 8.0 trở lên để tạo gói, và máy IIS cần .NET 8 Hosting Bundle để chạy:

```text
dotnet restore
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -o .\publish
```

Hoặc chạy `powershell -ExecutionPolicy Bypass -File .\publish-iis.ps1` trên máy Windows có .NET SDK 8.
