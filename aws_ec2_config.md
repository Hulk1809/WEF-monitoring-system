# Thông tin cấu hình máy chủ AWS EC2 (Deployment Server)

Tài liệu này chứa thông tin cấu hình máy chủ AWS EC2 dùng để triển khai hệ thống **Docker Security Shield**.

## 1. Thông tin kết nối & Cấu hình máy chủ

| Thông số | Giá trị |
| :--- | :--- |
| **IP Máy chủ (Host)** | `3.1.210.184` |
| **Tên đăng nhập (User)** | `ec2-user` |
| **Đường dẫn khóa SSH (Key)** | `HULK1809.pem` (Nằm ngay tại thư mục dự án) |
| **Hệ điều hành (AMI)** | `amazon/al2023-ami-2023.11.20260509.0-kernel-6.1-arm64` (Amazon Linux 2023 ARM64) |
| **Cấu hình máy chủ (Instance)** | `t4g.micro` (Vi xử lý AWS Graviton ARM64) |

---

## 2. Hướng dẫn kết nối SSH tới máy chủ

Bạn có thể mở PowerShell hoặc Command Prompt tại thư mục dự án này và thực thi lệnh sau để kết nối tới EC2:

```powershell
ssh -i "HULK1809.pem" ec2-user@3.1.210.184
```

> [!IMPORTANT]
> **Lưu ý về quyền của tệp tin khóa `.pem` (Chỉ cần thiết nếu gặp lỗi cảnh báo quyền hạn SSH):**
> Nếu SSH từ chối kết nối do khóa SSH có quyền hạn quá mở (UNPROTECTED PRIVATE KEY FILE), chạy lệnh sau trong PowerShell để giới hạn quyền truy cập tệp khóa:
> ```powershell
> # Tắt quyền kế thừa và chỉ cho phép User hiện tại đọc khóa
> icacls "D:\botspotify\HULK1809.pem" /inheritance:r
> icacls "D:\botspotify\HULK1809.pem" /grant:r "${env:USERNAME}:(R)"
> ```

---

## 3. Lưu ý khi triển khai Docker trên AWS ARM64 (t4g.micro)

*   **Tính tương thích của Container:** Tất cả các ảnh cơ sở (Base Images) trong tệp `docker-compose.yml` của dự án bao gồm:
    *   `mcr.microsoft.com/dotnet/sdk:8.0`
    *   `mcr.microsoft.com/dotnet/aspnet:8.0`
    *   `postgres:16-alpine`
    đều hỗ trợ kiến trúc **ARM64**. Docker trên EC2 sẽ tự động tải các phiên bản phù hợp để biên dịch và chạy bình thường.
*   **Cài đặt Docker trên Amazon Linux 2023:**
    Nếu máy chủ EC2 mới khởi tạo và chưa có Docker, hãy kết nối SSH và chạy các lệnh sau để thiết lập:
    ```bash
    # Cập nhật hệ thống
    sudo dnf update -y

    # Cài đặt Docker
    sudo dnf install docker -y

    # Khởi động dịch vụ Docker và thiết lập chạy cùng hệ thống
    sudo systemctl start docker
    sudo systemctl enable docker

    # Thêm ec2-user vào nhóm docker để chạy không cần sudo
    sudo usermod -aG docker ec2-user

    # Khởi động lại terminal (hoặc chạy lệnh newgrp docker) để áp dụng quyền mới
    newgrp docker

    # Cài đặt Docker Compose (plugin)
    sudo dnf install docker-compose-plugin -y
    ```
