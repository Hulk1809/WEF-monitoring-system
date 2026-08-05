# 🛡️ Enterprise WAF & Security Monitoring System
### Hệ thống Giám sát Tự động, Phản ứng Sự cố và Bảo vệ Ứng dụng Web trên Nền tảng Docker Container

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](https://opensource.org/licenses/MIT)
[![Docker](https://img.shields.io/badge/Docker-Containerized-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![.NET 8.0](https://img.shields.io/badge/.NET-8.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Redis](https://img.shields.io/badge/Redis-Pub%2FSub%20%3C1ms-DC382D?logo=redis&logoColor=white)](https://redis.io/)
[![HTTPS TLS 1.3](https://img.shields.io/badge/HTTPS-TLS%201.3%20%2F%20HTTP2-009639?logo=nginx&logoColor=white)](https://nginx.org/)
[![SIEM CEF Log](https://img.shields.io/badge/SIEM-CEF%20Log-FF6F00)](https://www.elastic.co/)

---

## 📌 GIỚI THIỆU TỔNG QUAN

**WEF-monitoring-system** là giải pháp an ninh mạng toàn diện thiết kế theo mô hình **Phòng thủ Đa sâu (Defense-in-Depth 4 Lớp)** dành cho các ứng dụng Web doanh nghiệp trên hạ tầng Docker Container. System kết hợp **AI-Driven WAF (Machine Learning SDCA & ONNX Runtime)**, đồng bộ IP chặn thời gian thực **< 1ms qua Redis Pub/Sub**, cổng **Nginx HTTPS TLS 1.3**, **Dashboard giám sát real-time**, **Tự động xuất log SIEM chuẩn CEF** và **Cảnh báo Telegram Bot kết hợp Phản ứng sự cố phân cấp (Tiered Incident Response)**.

---

## 🏗️ KIẾN TRÚC HỆ THỐNG (DEFENSE-IN-DEPTH 4 LỚP)

```
[ INTERNET / ATTACKER ]
          │
          ▼  (HTTPS Port 443 / TLS 1.3 / HSTS Header Forwarding)
┌────────────────────────────────────────────────────────────────────────┐
│  LỚP 1: GATEWAY HARDENING - Nginx Reverse Proxy (Port 80/443)          │
└────────────────────────────────────────────────────────────────────────┘
          │  (Internal Docker Bridge: secure-net)
          ▼
┌────────────────────────────────────────────────────────────────────────┐
│  LỚP 2: AI-DRIVEN WAF & PREPROCESSING - ASP.NET Core (.NET 8.0)        │
│  - Multi-layer Decode (UrlDecode/HtmlDecode x3) & Sanitization         │
│  - ML.NET SDCA Model (34.741+ samples) & ONNX Runtime Inference Engine │
└────────────────────────────────────────────────────────────────────────┘
     │                            │
     ▼ (DB Connection)            ▼ (Sub-millisecond IP Sync <1ms)
┌───────────────────────┐   ┌────────────────────────────────────────────┐
│ PostgreSQL 16 (DB)    │   │  LỚP 3: DISTRIBUTED CACHE & RATE LIMITER   │
│ (No Exposed Ports!)   │   │  - Redis 7 Alpine Pub/Sub Channel          │
└───────────────────────┘   │  - Sliding Window 404 Rate Limiting        │
                            └────────────────────────────────────────────┘
                                  │
                                  ▼
┌────────────────────────────────────────────────────────────────────────┐
│  LỚP 4: ACTIVE MONITORING & EMERGENCY RESPONSE (Monitor Module)        │
│  - Docker Socket Daemon Monitor (/var/run/docker.sock)                 │
│  - Real-time Web Dashboard (Port 5001 + Google Authenticator TOTP MFA)  │
│  - SIEM CEF Log Aggregator API (/api/siem/cef-logs)                    │
│  - Telegram Bot Alerts & Remote Control Commands (/start_web, /unblock)│
└────────────────────────────────────────────────────────────────────────┘
```

---

## 🔥 TÍNH NĂNG NỔI BẬT

1. **AI-Driven WAF (Không dùng Regex tĩnh):** Huấn luyện trên tập dữ liệu 34.741 mẫu độc hại. Đạt độ chính xác thực nghiệm **98.37%**, thời gian suy luận **< 2ms/request**.
2. **Giải mã Đa tầng & Tiệt trùng Payload (Anti-Evasion):** Giải mã 3 lớp URL/HTML Entities và loại bỏ inline comments (`UNION/**/SELECT` -> `UNION SELECT`) trước khi đút vào AI engine.
3. **Đồng bộ IP Chặn < 1ms qua Redis Pub/Sub:** Khi phát hiện tấn công, Publisher bắn thông điệp tới `blocked-ips-channel` giúp toàn bộ cụm Web node cập nhật Blacklist trong bộ nhớ RAM ở tốc độ dưới mili-giây.
4. **Phản ứng Sự cố Phân cấp (Tiered Incident Response):** Cấp 1 chặn duy nhất IP độc hại mà không ngắt Web server; Cấp 2 tự động dừng container bảo vệ CSDL khi máy chủ quá tải CPU/RAM > 95%.
5. **Mã hóa HTTPS TLS 1.3 & Gateway Hardening:** Tích hợp cặp chứng chỉ RSA 2048-bit, HSTS, HTTP/2 và Security Headers chống MitM.
6. **Dashboard An ninh Real-time & TOTP MFA:** Giám sát chỉ số CPU/RAM/Log stream từ Docker Socket, định vị vị trí địa lý GeoIP, bảo vệ cổng 5001 bằng Google Authenticator OTP 6 chữ số.
7. **Xuất Log SIEM Chuẩn CEF:** Cung cấp API `/api/siem/cef-logs` xuất nhật ký định dạng Common Event Format sẵn sàng tích hợp ELK Stack / Datadog / Splunk.

---

## 🛠️ HƯỚNG DẪN KHỞI CHẠY (QUICK START)

### Yêu cầu hệ thống:
- Docker & Docker Compose
- .NET 8.0 SDK (cho phát triển cục bộ)
- Python 3.x

### 1. Clone Repository & Khai báo Biến môi trường:
```bash
git clone https://github.com/Hulk1809/WEF-monitoring-system.git
cd WEF-monitoring-system
cp .env.example .env
```

### 2. Sinh chứng chỉ SSL/TLS tự động:
```bash
python nginx/ssl/generate_ssl_certs.py
```

### 3. Khởi chạy toàn bộ hệ thống bằng Docker Compose:
```bash
docker-compose up -d --build
```

### 4. Truy cập Dịch vụ:
- **Ứng dụng Web (HTTP):** `http://localhost:8080`
- **Ứng dụng Web (HTTPS TLS 1.3):** `https://localhost:8443`
- **Dashboard Giám sát An ninh:** `http://localhost:5001` (Yêu cầu mã TOTP Google Authenticator)

---

## 👤 SINH VIÊN THỰC HIỆN

- **Sinh viên:** Võ Quốc Thắng (MSSV: 231A011150)
- **Ngành:** An toàn Thông tin - Trường Đại học Văn Hiến
- **Email:** voquocthang18092005@gmail.com
- **GitHub:** [@Hulk1809](https://github.com/Hulk1809)
