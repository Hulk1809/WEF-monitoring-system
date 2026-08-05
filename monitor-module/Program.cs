using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;


namespace MonitorModule
{
    public class Program
    {
        // Tên các container cần giám sát (khai báo trong docker-compose)
        private const string AppContainerName = "secure-app";
        private const string DbContainerName = "postgres-db";
        private const string NginxContainerName = "nginx-proxy";

        // Ngưỡng cảnh báo và phòng vệ
        private const int ScanThresholdCount = 15; // Số request nghi vấn tối đa (15 lần)
        private const int ScanThresholdSeconds = 60; // Trong khoảng thời gian (60 giây)
        private const double CpuThresholdPercent = 85.0; // Ngưỡng CPU cảnh báo (%)

        // Lưu trữ dữ liệu giám sát trong bộ nhớ (Memory Cache)
        public static readonly ConcurrentDictionary<string, ConcurrentQueue<DateTime>> AttackTracker = new();
        public static readonly ConcurrentDictionary<string, string> BlockedIps = new(); // IP -> Time blocked (Sẽ nạp từ SQLite khi khởi động)
        public static readonly ConcurrentDictionary<string, string> WhitelistedIps = new(); // IP -> Time whitelisted
        public static readonly ConcurrentDictionary<string, string> SeenIps = new(); // IP -> Country
        
        // Lưu trữ các phiên đăng nhập hợp lệ (Token -> Hạn dùng)
        public static readonly ConcurrentDictionary<string, DateTime> ActiveSessions = new();

        // Theo dõi số lần nhập sai mã MFA và thời gian khóa đăng nhập
        public static readonly ConcurrentDictionary<string, int> LoginAttempts = new();
        public static readonly ConcurrentDictionary<string, DateTime> LoginLockouts = new();

        public static bool IsAuthorized(HttpContext context)
        {
            // Tạm thời mở quyền truy cập trực tiếp (Bypass Auth) cho Admin theo yêu cầu
            return true;
        }
        
        // Khóa đồng bộ hóa ghi cơ sở dữ liệu SQLite
        private static readonly object DbLock = new();
        
        // Trạng thái an ninh hiện tại: "SAFE", "WARNING", "UNDER_ATTACK", "ISOLATED"
        public static string SecurityStatus = "SAFE";
        public static string ThreatDetails = "Không phát hiện mối đe dọa nào.";

        // Chỉ số CPU/RAM real-time của các Container
        public static readonly ConcurrentDictionary<string, ContainerMetrics> ContainerStats = new()
        {
            [AppContainerName] = new ContainerMetrics { Name = AppContainerName, IsRunning = false },
            [DbContainerName] = new ContainerMetrics { Name = DbContainerName, IsRunning = false },
            [NginxContainerName] = new ContainerMetrics { Name = NginxContainerName, IsRunning = false }
        };

        private static DockerClient? _dockerClient;
        private static string _appContainerId = string.Empty;
        private static string _dbContainerId = string.Empty;
        private static string _nginxContainerId = string.Empty;

        public static void Main(string[] args)
        {
            // Đảm bảo thư mục lưu trữ dữ liệu tồn tại
            Directory.CreateDirectory("data");

            // Khởi tạo cơ sở dữ liệu SQLite và nạp cache
            using (var db = new MonitorDbContext())
            {
                db.Database.EnsureCreated();
                // Đảm bảo các bảng mới tồn tại kể cả khi DB đã được tạo từ trước
                db.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS WhitelistedIps (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Ip TEXT NOT NULL,
                        Time TEXT NOT NULL
                    );
                ");
                db.Database.ExecuteSqlRaw(@"
                    CREATE TABLE IF NOT EXISTS SeenIps (
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
                        Ip TEXT NOT NULL,
                        Country TEXT NOT NULL,
                        FirstSeen TEXT NOT NULL
                    );
                ");
                try
                {
                    var loadedIps = db.BlockedIps.ToList();
                    foreach (var record in loadedIps)
                    {
                        BlockedIps.TryAdd(record.Ip, record.Time);
                    }

                    var loadedWhiteIps = db.WhitelistedIps.ToList();
                    foreach (var record in loadedWhiteIps)
                    {
                        WhitelistedIps.TryAdd(record.Ip, record.Time);
                    }

                    var loadedSeenIps = db.SeenIps.ToList();
                    foreach (var record in loadedSeenIps)
                    {
                        SeenIps.TryAdd(record.Ip, record.Country);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB ERROR] Không thể nạp cache dữ liệu: {ex.Message}");
                }
            }

            var builder = WebApplication.CreateBuilder(args);

            // Cấu hình Kestrel lắng nghe trên cổng 5001 cho Dashboard
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.ListenAnyIP(5001);
            });

            // Đăng ký dịch vụ CORS để Dashboard có thể gọi từ ngoài nếu cần
            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
                });
            });

            var app = builder.Build();

            app.UseCors();
            app.UseDefaultFiles(); // Tự động phục vụ index.html nếu truy cập root "/"
            app.UseStaticFiles();  // Phục vụ các file tĩnh trong thư mục wwwroot

            // ==================== HỆ THỐNG XÁC THỰC MFA AUTHENTICATOR (TOTP) ====================

            // API Đăng nhập MFA Authenticator (TOTP)
            app.MapPost("/api/auth/login", (LoginDto req, HttpContext context) =>
            {
                var clientIp = context.Request.Headers["X-Forwarded-For"].FirstOrDefault() 
                               ?? context.Request.Headers["X-Real-IP"].FirstOrDefault() 
                               ?? context.Connection.RemoteIpAddress?.ToString() 
                               ?? "Unknown";
                if (clientIp.StartsWith("::ffff:")) clientIp = clientIp.Substring(7);

                // Kiểm tra xem IP có đang bị khóa đăng nhập không
                if (LoginLockouts.TryGetValue(clientIp, out var lockoutUntil))
                {
                    if (DateTime.UtcNow < lockoutUntil)
                    {
                        var remainingTime = lockoutUntil - DateTime.UtcNow;
                        return Results.Json(new { Success = false, Message = $"Bạn đã nhập sai 5 lần liên tiếp. Thiết bị tạm thời bị khóa đăng nhập trong 5 phút. Vui lòng thử lại sau {Math.Ceiling(remainingTime.TotalSeconds)} giây." }, statusCode: 429);
                    }
                    else
                    {
                        LoginLockouts.TryRemove(clientIp, out _);
                        LoginAttempts.TryRemove(clientIp, out _);
                    }
                }

                try
                {
                    var email = req?.Email?.Trim() ?? "";
                    var code = req?.Code?.Trim() ?? "";

                    if (email != "voquocthang18092005@gmail.com")
                    {
                        return Results.Json(new { Success = false, Message = "Email không được cấp quyền truy cập." }, statusCode: 403);
                    }

                    if (!string.IsNullOrEmpty(code) && TotpHelper.VerifyCode(code))
                    {
                        // Đăng nhập thành công -> Reset số lần thử sai
                        LoginAttempts.TryRemove(clientIp, out _);
                        LoginLockouts.TryRemove(clientIp, out _);

                        var token = Guid.NewGuid().ToString("N");
                        ActiveSessions.TryAdd(token, DateTime.UtcNow.AddHours(2));
                        return Results.Ok(new { Success = true, Token = token });
                    }

                    // Đăng nhập thất bại -> Tăng số lần thử
                    int attempts = LoginAttempts.AddOrUpdate(clientIp, 1, (key, val) => val + 1);
                    if (attempts >= 5)
                    {
                        // Khóa 5 phút (300 giây)
                        var lockoutTime = DateTime.UtcNow.AddMinutes(5);
                        LoginLockouts[clientIp] = lockoutTime;
                        return Results.Json(new { Success = false, Message = "Bạn đã nhập sai 5 lần liên tiếp. Thiết bị tạm thời bị khóa đăng nhập trong 5 phút. Vui lòng thử lại sau 300 giây." }, statusCode: 429);
                    }

                    return Results.Json(new { Success = false, Message = $"Mã xác thực Google Authenticator không chính xác. Bạn còn {5 - attempts} lần thử trước khi bị khóa 5 phút." }, statusCode: 400);
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Success = false, Message = ex.Message }, statusCode: 500);
                }
            });

            // API kiểm tra Token Session có hợp lệ không
            app.MapGet("/api/auth/check", (HttpContext context) =>
            {
                if (IsAuthorized(context))
                {
                    return Results.Ok(new { Success = true });
                }
                return Results.Json(new { Success = false, Message = "Phiên làm việc hết hạn hoặc không hợp lệ." }, statusCode: 401);
            });

            // ===================================================================================

            // API 1: Lấy trạng thái hệ thống và chỉ số CPU/RAM
            app.MapGet("/api/status", async (HttpContext context) => 
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);

                // Cập nhật trạng thái chạy của container
                ContainerStats[AppContainerName].IsRunning = !string.IsNullOrEmpty(_appContainerId) && await IsContainerRunning(_appContainerId);
                ContainerStats[DbContainerName].IsRunning = !string.IsNullOrEmpty(_dbContainerId) && await IsContainerRunning(_dbContainerId);

                if (ContainerStats[AppContainerName].IsRunning == false && SecurityStatus == "ISOLATED")
                {
                    // Vẫn giữ Isolated
                }
                else if (BlockedIps.Count > 0)
                {
                    SecurityStatus = "UNDER_ATTACK";
                }
                else
                {
                    SecurityStatus = "SAFE";
                    ThreatDetails = "Hệ thống đang hoạt động an toàn.";
                }

                return Results.Ok(new
                {
                    Status = SecurityStatus,
                    ThreatDetails,
                    BlockedCount = BlockedIps.Count,
                    BlockedIps = BlockedIps.Select(kv => new { Ip = kv.Key, Time = kv.Value }),
                    Containers = ContainerStats.Values.ToList()
                });
            });

            // API 2: Lấy danh sách Logs (tối đa 100 dòng gần nhất từ SQLite)
            app.MapGet("/api/logs", (HttpContext context) => 
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);

                using var db = new MonitorDbContext();
                return Results.Ok(db.Logs.OrderByDescending(l => l.Timestamp).Take(100).ToList());
            });

            // API 3: Điều khiển container (Start / Stop / Restart)
            app.MapPost("/api/control", async (HttpContext context) =>
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);
                if (_dockerClient == null) return Results.Json(new { Success = false, Message = "Docker Client chưa sẵn sàng." }, statusCode: 500);

                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    var doc = JsonDocument.Parse(body);
                    var containerName = doc.RootElement.GetProperty("containerName").GetString();
                    var action = doc.RootElement.GetProperty("action").GetString();

                    string containerId = containerName == AppContainerName ? _appContainerId : _dbContainerId;
                    if (string.IsNullOrEmpty(containerId))
                    {
                        // Thử resolve lại ID
                        await ResolveContainerIdsAsync();
                        containerId = containerName == AppContainerName ? _appContainerId : _dbContainerId;
                    }

                    if (string.IsNullOrEmpty(containerId))
                    {
                        return Results.BadRequest(new { Success = false, Message = $"Không tìm thấy ID cho container {containerName}" });
                    }

                    AddLog("SYSTEM", $"Quản trị viên yêu cầu thực thi hành động '{action}' trên Container '{containerName}'");

                    bool success = false;
                    switch (action?.ToLower())
                    {
                        case "start":
                            success = await _dockerClient.Containers.StartContainerAsync(containerId, new ContainerStartParameters());
                            if (containerName == AppContainerName && SecurityStatus == "ISOLATED")
                            {
                                // Cập nhật trạng thái an ninh, giữ nguyên danh sách IP chặn
                                SecurityStatus = BlockedIps.Count > 0 ? "UNDER_ATTACK" : "SAFE";
                                ThreatDetails = "Ứng dụng được khởi động lại bởi Quản trị viên (giữ nguyên danh sách IP bị khóa).";
                                _ = SendTelegramAlertAsync($"✅ *HỆ THỐNG ĐÃ ĐƯỢC KHÔI PHỤC BỞI QUẢN TRỊ VIÊN*\n\n*Trạng thái:* {SecurityStatus}\n*Hành động:* Khởi chạy lại Container `{AppContainerName}` (danh sách IP bị khóa được giữ nguyên để bảo vệ hệ thống).");
                            }
                            break;
                        case "stop":
                            success = await _dockerClient.Containers.StopContainerAsync(containerId, new ContainerStopParameters { WaitBeforeKillSeconds = 3 });
                            break;
                        case "restart":
                            await _dockerClient.Containers.RestartContainerAsync(containerId, new ContainerRestartParameters { WaitBeforeKillSeconds = 3 });
                            success = true;
                            if (containerName == AppContainerName)
                            {
                                SecurityStatus = BlockedIps.Count > 0 ? "UNDER_ATTACK" : "SAFE";
                                ThreatDetails = "Ứng dụng được restart bởi Quản trị viên (giữ nguyên danh sách IP bị khóa).";
                                _ = SendTelegramAlertAsync($"✅ *HỆ THỐNG ĐÃ ĐƯỢC KHÔI PHỤC BỞI QUẢN TRỊ VIÊN*\n\n*Trạng thái:* {SecurityStatus}\n*Hành động:* Khởi chạy lại Container `{AppContainerName}` (danh sách IP bị khóa được giữ nguyên).");
                            }
                            break;
                    }

                    return Results.Ok(new { Success = success, Action = action, ContainerName = containerName });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Success = false, Message = ex.Message }, statusCode: 500);
                }
            });

            // API 4: Gỡ chặn một IP từ Dashboard
            app.MapPost("/api/unblock", async (HttpContext context) =>
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    var doc = JsonDocument.Parse(body);
                    var ip = doc.RootElement.GetProperty("ip").GetString();

                    if (string.IsNullOrEmpty(ip)) return Results.BadRequest(new { Success = false, Message = "IP không hợp lệ." });

                    bool removedInMemory = BlockedIps.TryRemove(ip, out _);
                    bool removedInDb = false;

                    using var db = new MonitorDbContext();
                    var record = db.BlockedIps.FirstOrDefault(b => b.Ip == ip);
                    if (record != null)
                    {
                        db.BlockedIps.Remove(record);
                        db.SaveChanges();
                        removedInDb = true;
                    }

                    if (removedInMemory || removedInDb)
                    {
                        AddLog("SYSTEM", $"Quản trị viên gỡ chặn IP {ip} từ Dashboard.");
                        _ = SendTelegramAlertAsync($"✅ *GỠ CHẶN QUA DASHBOARD*\n\n- IP: `{ip}` đã được mở khóa tự do truy cập bởi Quản trị viên.");
                        return Results.Ok(new { Success = true, Message = $"Đã mở khóa IP {ip}" });
                    }

                    return Results.BadRequest(new { Success = false, Message = "IP không tồn tại trong danh sách chặn." });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Success = false, Message = ex.Message }, statusCode: 500);
                }
            });

            // API 5: Lấy danh sách IP bị chặn dạng mảng đơn giản (phục vụ secure-app đồng bộ hóa)
            app.MapGet("/api/blocked-ips-list", () => BlockedIps.Keys.ToList());

            // API 6: Lấy danh sách IP trắng dạng mảng đơn giản (phục vụ secure-app đồng bộ hóa)
            app.MapGet("/api/whitelist-ips-list", () => WhitelistedIps.Keys.ToList());

            // API 7: Lấy danh sách IP trắng đầy đủ (phục vụ Dashboard hiển thị)
            app.MapGet("/api/whitelist", (HttpContext context) => {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);
                return Results.Ok(WhitelistedIps.Select(kv => new { Ip = kv.Key, Time = kv.Value }).ToList());
            });

            // API 8: Thêm IP vào danh sách trắng từ Dashboard
            app.MapPost("/api/whitelist/add", async (HttpContext context) =>
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    var doc = JsonDocument.Parse(body);
                    var ip = doc.RootElement.GetProperty("ip").GetString();

                    if (string.IsNullOrEmpty(ip)) return Results.BadRequest(new { Success = false, Message = "IP không hợp lệ." });

                    // Xóa khỏi danh sách chặn nếu có
                    BlockedIps.TryRemove(ip, out _);
                    using (var db = new MonitorDbContext())
                    {
                        var blockedRecord = db.BlockedIps.FirstOrDefault(b => b.Ip == ip);
                        if (blockedRecord != null)
                        {
                            db.BlockedIps.Remove(blockedRecord);
                            db.SaveChanges();
                        }
                    }

                    var timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    bool added = WhitelistedIps.TryAdd(ip, timeStr);
                    if (added)
                    {
                        using (var db = new MonitorDbContext())
                        {
                            db.WhitelistedIps.Add(new WhitelistedIpRecord { Ip = ip, Time = timeStr });
                            db.SaveChanges();
                        }
                        AddLog("SYSTEM", $"Quản trị viên thêm IP {ip} vào danh sách tin cậy (Whitelist) từ Dashboard.");
                        _ = SendTelegramAlertAsync($"✅ *WHITE LIST UPDATE*\n\n- IP: `{ip}` đã được thêm vào danh sách tin cậy bởi Quản trị viên.");
                        return Results.Ok(new { Success = true, Message = $"Đã thêm IP {ip} vào whitelist" });
                    }
                    return Results.BadRequest(new { Success = false, Message = "IP đã tồn tại trong danh sách tin cậy." });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Success = false, Message = ex.Message }, statusCode: 500);
                }
            });

            // API 9: Xóa IP khỏi danh sách trắng từ Dashboard
            app.MapPost("/api/whitelist/remove", async (HttpContext context) =>
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);
                try
                {
                    using var reader = new StreamReader(context.Request.Body);
                    var body = await reader.ReadToEndAsync();
                    var doc = JsonDocument.Parse(body);
                    var ip = doc.RootElement.GetProperty("ip").GetString();

                    if (string.IsNullOrEmpty(ip)) return Results.BadRequest(new { Success = false, Message = "IP không hợp lệ." });

                    bool removedInMemory = WhitelistedIps.TryRemove(ip, out _);
                    bool removedInDb = false;

                    using var db = new MonitorDbContext();
                    var record = db.WhitelistedIps.FirstOrDefault(w => w.Ip == ip);
                    if (record != null)
                    {
                        db.WhitelistedIps.Remove(record);
                        db.SaveChanges();
                        removedInDb = true;
                    }

                    if (removedInMemory || removedInDb)
                    {
                        AddLog("SYSTEM", $"Quản trị viên xóa IP {ip} khỏi danh sách tin cậy.");
                        _ = SendTelegramAlertAsync($"ℹ️ *WHITE LIST UPDATE*\n\n- IP: `{ip}` đã được gỡ bỏ khỏi danh sách tin cậy.");
                        return Results.Ok(new { Success = true, Message = $"Đã xóa IP {ip} khỏi whitelist" });
                    }
                    return Results.BadRequest(new { Success = false, Message = "IP không tồn tại trong danh sách tin cậy." });
                }
                catch (Exception ex)
                {
                    return Results.Json(new { Success = false, Message = ex.Message }, statusCode: 500);
                }
            });

            // API 10: Thống kê số liệu tấn công phục vụ biểu đồ Dashboard
            app.MapGet("/api/stats", (HttpContext context) =>
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);
                using var db = new MonitorDbContext();
                var logs = db.Logs.ToList();

                // 1. Phân loại loại tấn công
                int sqliCount = logs.Count(l => l.Type == "CRITICAL" && (l.Message.Contains("SQL-INJECTION") || l.Message.Contains("SQL INJECTION")));
                int xssCount = logs.Count(l => l.Type == "CRITICAL" && (l.Message.Contains("XSS") || l.Message.Contains("CROSS-SITE SCRIPTING")));
                int rateLimitCount = 0; // Removed RATE-LIMIT

                // 2. Thống kê theo ngày (Attack Timeline) - Nhóm 7 ngày gần nhất
                var timeline = logs
                    .Where(l => l.Type == "CRITICAL" || l.Type == "SUSPICIOUS")
                    .GroupBy(l => l.Timestamp.Date)
                    .OrderBy(g => g.Key)
                    .Select(g => new { Date = g.Key.ToString("yyyy-MM-dd"), Count = g.Count() })
                    .TakeLast(7)
                    .ToList();

                // 3. Quốc gia tấn công (Attack Countries) - Xem quốc gia của những IP bị chặn
                var blockedIpsList = db.BlockedIps.ToList();
                var seenIpsList = db.SeenIps.ToList();
                
                var countries = blockedIpsList
                    .Select(b => {
                        var seen = seenIpsList.FirstOrDefault(s => s.Ip == b.Ip);
                        return seen != null ? seen.Country : "Local Network/Không rõ";
                    })
                    .GroupBy(c => c)
                    .Select(g => new { Country = g.Key, Count = g.Count() })
                    .OrderByDescending(g => g.Count)
                    .ToList();

                return Results.Ok(new
                {
                    AttackTypes = new { Sqli = sqliCount, Xss = xssCount, RateLimit = rateLimitCount },
                    Timeline = timeline,
                    Countries = countries
                });
            });

            // API 4: SIEM Enterprise Log Aggregation (Common Event Format - CEF)
            app.MapGet("/api/siem/cef-logs", (HttpContext context) =>
            {
                if (!IsAuthorized(context)) return Results.Json(new { Success = false, Message = "Chưa đăng nhập hoặc phiên làm việc hết hạn." }, statusCode: 401);

                using var db = new MonitorDbContext();
                var logs = db.Logs.OrderByDescending(l => l.Timestamp).Take(200).ToList();
                var cefLogs = logs.Select(l => 
                    $"CEF:0|Enterprise DevSecOps|AI-WAF|1.0|{(l.Type == "CRITICAL" ? "400" : "200")}|{l.Type}|{(l.Type == "CRITICAL" ? "8" : "3")}|src=0.0.0.0 msg={l.Message} timestamp={l.Timestamp:yyyy-MM-ddTHH:mm:ssZ}"
                ).ToList();

                return Results.Ok(new { Format = "CEF", Version = "1.0", Count = cefLogs.Count, Logs = cefLogs });
            });

            // Khởi chạy Docker Monitor engine trong background
            Task.Run(async () => await StartDockerMonitorAsync());

            app.Run();
        }

        public static void PublishRedisBlockIp(string ip)
        {
            try
            {
                string redisConnStr = Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "redis:6379";
                using var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnStr);
                var sub = redis.GetSubscriber();
                sub.Publish(StackExchange.Redis.RedisChannel.Literal("blocked-ips-channel"), ip);
                Console.WriteLine($"[REDIS PUB <1ms] Instant broadcast blocked IP '{ip}' to Redis Channel 'blocked-ips-channel'!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[REDIS PUB WARNING] Could not publish to Redis ({ex.Message}). Fallback to HTTP polling.");
            }
        }

        private static async Task StartDockerMonitorAsync()
        {
            AddLog("SYSTEM", "Module giám sát tự động đang kết nối tới Docker Engine Daemon...");

            string dockerSocket = "unix:///var/run/docker.sock";
            if (OperatingSystem.IsWindows() && !Directory.Exists("/var/run"))
            {
                dockerSocket = "npipe://./pipe/docker_engine";
            }

            try
            {
                var config = new DockerClientConfiguration(new Uri(dockerSocket));
                _dockerClient = config.CreateClient();
                await _dockerClient.System.PingAsync();

                AddLog("SYSTEM", "Kết nối tới Docker Engine API thành công.");

                // Định danh container ban đầu
                await ResolveContainerIdsAsync();

                // Tiến hành chạy vòng lặp giám sát
                var cts = new CancellationTokenSource();
                var statsTask = MonitorStatsLoopAsync(cts.Token);
                var logsTask = MonitorAppLogsLoopAsync(cts.Token);
                var telegramCmdTask = MonitorTelegramCommandsLoopAsync(cts.Token);

                await Task.WhenAll(statsTask, logsTask, telegramCmdTask);
            }
            catch (Exception ex)
            {
                AddLog("CRITICAL", $"Lỗi khởi động cơ chế giám sát Docker: {ex.Message}");
            }
        }

        private static async Task ResolveContainerIdsAsync()
        {
            if (_dockerClient == null) return;
            try
            {
                var containers = await _dockerClient.Containers.ListContainersAsync(new ContainersListParameters { All = true });
                foreach (var container in containers)
                {
                    var names = container.Names.Select(n => n.TrimStart('/')).ToList();
                    if (names.Contains(AppContainerName))
                    {
                        _appContainerId = container.ID;
                    }
                    else if (names.Contains(DbContainerName))
                    {
                        _dbContainerId = container.ID;
                    }
                    else if (names.Contains(NginxContainerName))
                    {
                        _nginxContainerId = container.ID;
                    }
                }
            }
            catch (Exception ex)
            {
                AddLog("SYSTEM", $"Lỗi tìm ID container: {ex.Message}");
            }
        }

        private static async Task<bool> IsContainerRunning(string containerId)
        {
            if (_dockerClient == null || string.IsNullOrEmpty(containerId)) return false;
            try
            {
                var response = await _dockerClient.Containers.InspectContainerAsync(containerId);
                return response.State.Running;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Loop giám sát thông số CPU, RAM
        /// </summary>
        private static async Task MonitorStatsLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrEmpty(_appContainerId) || string.IsNullOrEmpty(_dbContainerId))
                    {
                        await ResolveContainerIdsAsync();
                    }

                    if (!string.IsNullOrEmpty(_appContainerId))
                        await FetchContainerStatsAsync(_appContainerId, AppContainerName);

                    if (!string.IsNullOrEmpty(_dbContainerId))
                        await FetchContainerStatsAsync(_dbContainerId, DbContainerName);
                }
                catch (Exception)
                {
                    // Bỏ qua lỗi kết nối tạm thời
                }

                await Task.Delay(2000, cancellationToken); // Lấy số liệu mỗi 2 giây
            }
        }

        private static async Task FetchContainerStatsAsync(string containerId, string containerName)
        {
            if (_dockerClient == null) return;
            try
            {
                var statsParams = new ContainerStatsParameters { Stream = false };
#pragma warning disable CS0618
                using var statsStream = await _dockerClient.Containers.GetContainerStatsAsync(containerId, statsParams, CancellationToken.None);
#pragma warning restore CS0618
                using var reader = new StreamReader(statsStream);
                var jsonContent = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(jsonContent)) return;

                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;

                if (!root.TryGetProperty("cpu_stats", out var cpuStats) || 
                    !root.TryGetProperty("precpu_stats", out var preCpuStats))
                {
                    ContainerStats[containerName].IsRunning = false;
                    return;
                }

                // CPU
                long totalUsage = cpuStats.GetProperty("cpu_usage").GetProperty("total_usage").GetInt64();
                long preTotalUsage = preCpuStats.GetProperty("cpu_usage").GetProperty("total_usage").GetInt64();
                long systemUsage = cpuStats.GetProperty("system_cpu_usage").GetInt64();
                long preSystemUsage = preCpuStats.GetProperty("system_cpu_usage").GetInt64();

                double cpuPercent = 0.0;
                long cpuDelta = totalUsage - preTotalUsage;
                long systemDelta = systemUsage - preSystemUsage;

                if (systemDelta > 0 && cpuDelta > 0)
                {
                    int onlineCpu = 1;
                    if (cpuStats.TryGetProperty("online_cpus", out var onlineCpusProp))
                        onlineCpu = onlineCpusProp.GetInt32();
                    cpuPercent = ((double)cpuDelta / systemDelta) * onlineCpu * 100.0;
                }

                // RAM
                long memUsage = root.GetProperty("memory_stats").GetProperty("usage").GetInt64();
                long memLimit = root.GetProperty("memory_stats").GetProperty("limit").GetInt64();
                double memPercent = ((double)memUsage / memLimit) * 100.0;

                // Update Metrics
                ContainerStats[containerName].CpuUsage = Math.Round(cpuPercent, 2);
                ContainerStats[containerName].MemUsage = Math.Round(memPercent, 2);
                ContainerStats[containerName].MemRawMb = Math.Round((double)memUsage / 1024 / 1024, 1);
                ContainerStats[containerName].MemLimitMb = memLimit / 1024 / 1024;
                ContainerStats[containerName].IsRunning = true;

                if (cpuPercent > CpuThresholdPercent)
                {
                    AddLog("WARNING", $"Cảnh báo: Container '{containerName}' quá tải CPU ({ContainerStats[containerName].CpuUsage}%)!");
                }
            }
            catch
            {
                ContainerStats[containerName].IsRunning = false;
            }
        }

        /// <summary>
        /// Loop giám sát logs sử dụng MultiplexedStream của Docker.DotNet
        /// </summary>
        private static async Task MonitorAppLogsLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrEmpty(_appContainerId))
                    {
                        await Task.Delay(2000, cancellationToken);
                        continue;
                    }

                    var logParams = new ContainerLogsParameters
                    {
                        ShowStdout = true,
                        ShowStderr = true,
                        Follow = true,
                        Tail = "0"
                    };

                    using var logStream = await _dockerClient!.Containers.GetContainerLogsAsync(_appContainerId, false, logParams, cancellationToken);
                    
                    var buffer = new byte[8192];
                    var charBuffer = new char[8192];
                    var sb = new StringBuilder();
                    var decoder = Encoding.UTF8.GetDecoder();

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var result = await logStream.ReadOutputAsync(buffer, 0, buffer.Length, cancellationToken);
                        if (result.EOF) break;

                        if (result.Count > 0)
                        {
                            int charCount = decoder.GetChars(buffer, 0, result.Count, charBuffer, 0);
                            sb.Append(charBuffer, 0, charCount);

                            string currentContent = sb.ToString();
                            int newlineIndex;
                            while ((newlineIndex = currentContent.IndexOf('\n')) >= 0)
                            {
                                var line = currentContent.Substring(0, newlineIndex).TrimEnd('\r', '\n', ' ');
                                sb.Remove(0, newlineIndex + 1);
                                currentContent = sb.ToString();

                                if (!string.IsNullOrWhiteSpace(line))
                                {
                                    ParseAndStoreLog(line);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    await Task.Delay(3000, cancellationToken);
                    await ResolveContainerIdsAsync();
                }
            }
        }

        private static void ParseAndStoreLog(string rawLog)
        {
            // Định dạng log:
            // [ACCESS] IP: ::ffff:172.20.0.1 | Path: /api/data | Method: GET | Status: 200
            // [SUSPICIOUS] IP: ::ffff:172.20.0.1 | Path: /admin | Method: GET | User-Agent: ...
            
            string type = "INFO";
            string message = rawLog;

            string ip = ExtractIpFromLog(rawLog);
            if (!string.IsNullOrEmpty(ip) && ip != "Unknown")
            {
                // Kiểm tra danh sách Whitelist trước khi xử lý phòng thủ
                if (WhitelistedIps.ContainsKey(ip))
                {
                    if (rawLog.Contains("[ATTACK]"))
                    {
                        AddLog("INFO", $"[BYPASS] Phát hiện payload độc hại từ IP tin cậy: {ip}. Bỏ qua phòng vệ.");
                    }

                    else if (rawLog.Contains("Status: 404"))
                    {
                        AddLog("INFO", $"[BYPASS] IP tin cậy truy cập URL không tồn tại: {rawLog}");
                    }
                    else
                    {
                        AddLog("ACCESS", rawLog);
                    }
                    return;
                }

                // Thực hiện ghi nhận và thông báo truy cập mới (chạy nền không block luồng logs)
                _ = Task.Run(async () => await CheckAndLogNewAccessAsync(ip, rawLog));
            }

            if (rawLog.Contains("[ATTACK]"))
            {
                type = "CRITICAL";
                if (!string.IsNullOrEmpty(ip) && ip != "Unknown")
                {
                    // Lấy loại tấn công (SQL-INJECTION hoặc XSS)
                    string attackType = "MALICIOUS-PAYLOAD";
                    if (rawLog.Contains("Type: SQL-INJECTION")) attackType = "SQL INJECTION";
                    if (rawLog.Contains("Type: XSS")) attackType = "CROSS-SITE SCRIPTING (XSS)";

                    // Lấy payload tấn công
                    string payload = "";
                    int payloadIndex = rawLog.IndexOf("Payload: ");
                    if (payloadIndex != -1) payload = rawLog.Substring(payloadIndex + 9);

                    // Thêm IP vào danh sách chặn vĩnh viễn (WAF block)
                    var blockTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    if (BlockedIps.TryAdd(ip, blockTime + " (Vĩnh viễn - Tấn công WAF)"))
                    {
                        PublishRedisBlockIp(ip);
                        // Lưu vào SQLite
                        lock (DbLock)
                        {
                            try
                            {
                                using var db = new MonitorDbContext();
                                db.BlockedIps.Add(new BlockedIpRecord { Ip = ip, Time = blockTime + " (Tấn công WAF)" });
                                db.SaveChanges();
                            }
                            catch (Exception ex)
                            {
                                Console.WriteLine($"[DB ERROR] Lỗi ghi BlockedIp: {ex.Message}");
                            }
                        }
                    }

                    // Gửi tin nhắn cảnh báo Telegram - Chỉ chặn IP, KHÔNG ngắt nguồn Web
                    _ = SendTelegramAlertAsync($"🛡️ *CẢNH BÁO TẤN CÔNG (IP BLOCKED)* 🛡️\n\n*Kiểu tấn công:* `{attackType}`\n*IP nguồn:* `{ip}`\n*Payload:* `{payload}`\n*Hành động:* Hệ thống đã CHẶN ĐỨNG địa chỉ IP này. Web vẫn hoạt động bình thường cho người dùng khác.");

                    // Cập nhật trạng thái an ninh
                    SecurityStatus = "BLOCKING_ATTACKER";
                    ThreatDetails = $"Đã tự động khóa IP tấn công: {ip}. Hệ thống Web vẫn phục vụ người dùng bình thường.";
                }
            }
            else if (rawLog.Contains("[SUSPICIOUS]"))
            {
                type = "SUSPICIOUS";
                
                if (!string.IsNullOrEmpty(ip) && ip != "Unknown")
                {
                    TrackAttack(ip, rawLog);
                }
            }
            else if (rawLog.Contains("[ACCESS]"))
            {
                type = "ACCESS";
                if (rawLog.Contains("Status: 404"))
                {
                    type = "WARNING";
                    if (!string.IsNullOrEmpty(ip) && ip != "Unknown")
                    {
                        TrackAttack(ip, rawLog);
                    }
                }
            }

            else if (rawLog.Contains("[ERROR]"))
            {
                type = "ERROR";
            }

            AddLog(type, message);
        }

        private static string ExtractIpFromLog(string logLine)
        {
            try
            {
                int ipLabelIndex = logLine.IndexOf("IP: ");
                if (ipLabelIndex == -1) return string.Empty;

                int ipStartIndex = ipLabelIndex + 4;
                // Nếu IP bắt đầu bằng ipv4-mapped-ipv6 ::ffff:
                if (logLine.Substring(ipStartIndex).StartsWith("::ffff:"))
                {
                    ipStartIndex += 7;
                }

                int pipeIndex = logLine.IndexOf('|', ipStartIndex);
                if (pipeIndex == -1)
                {
                    return logLine[ipStartIndex..].Trim();
                }
                return logLine[ipStartIndex..pipeIndex].Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void TrackAttack(string ip, string rawLog)
        {
            if (BlockedIps.ContainsKey(ip)) return;

            var queue = AttackTracker.GetOrAdd(ip, _ => new ConcurrentQueue<DateTime>());
            var now = DateTime.Now;
            queue.Enqueue(now);

            // Xóa log cũ ngoài 10s
            var thresholdTime = now.AddSeconds(-ScanThresholdSeconds);
            while (queue.TryPeek(out var time) && time < thresholdTime)
            {
                queue.TryDequeue(out _);
            }

            if (queue.Count >= ScanThresholdCount)
            {
                var blockTime = now.ToString("HH:mm:ss");
                if (BlockedIps.TryAdd(ip, blockTime))
                {
                    PublishRedisBlockIp(ip);
                    // Lưu vào SQLite
                    lock (DbLock)
                    {
                        try
                        {
                            using var db = new MonitorDbContext();
                            db.BlockedIps.Add(new BlockedIpRecord { Ip = ip, Time = blockTime });
                            db.SaveChanges();
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[DB ERROR] Lỗi ghi BlockedIp: {ex.Message}");
                        }
                    }
                    // Gửi báo động Telegram - Chỉ khóa IP, không ngắt nguồn Web
                    _ = SendTelegramAlertAsync($"🛡️ *DOCKER SECURITY SHIELD ALERT* 🛡️\n\n*Phát hiện rà quét vượt ngưỡng!*\n*IP tấn công:* `{ip}`\n*Hành động:* Hệ thống đã tự động KHÓA IP này. Web vẫn hoạt động bình thường cho những người dùng khác.");
                    SecurityStatus = "BLOCKING_ATTACKER";
                    ThreatDetails = $"Đã tự động khóa IP rà quét: {ip}. Web vẫn phục vụ người dùng bình thường.";
                }
            }
        }

        private static void TriggerDefense(string attackerIp)
        {
            SecurityStatus = "ISOLATED";
            ThreatDetails = $"Phát hiện tấn công rà quét từ IP: {attackerIp}. Hệ thống tự động kích hoạt kịch bản phòng vệ: Dừng Container ứng dụng để cô lập mối đe dọa.";
            
            AddLog("CRITICAL", $"[DEFENSE] PHÁT HIỆN TẤN CÔNG RÀ QUÉT VƯỢT NGƯỠNG TỪ IP: {attackerIp} ({ScanThresholdCount} lần/{ScanThresholdSeconds}s)");
            AddLog("CRITICAL", $"[DEFENSE] Kích hoạt kịch bản phòng vệ: Đang dừng container '{AppContainerName}' để cô lập...");

            // Gửi báo động Telegram
            _ = SendTelegramAlertAsync($"🚨 *DOCKER SECURITY SHIELD ALERT* 🚨\n\n*Phát hiện tấn công rà quét vượt ngưỡng!*\n*IP tấn công:* `{attackerIp}`\n*Hành động:* Hệ thống đã tự động dừng Container `{AppContainerName}` để cô lập an toàn cơ sở dữ liệu!");

            _ = Task.Run(async () =>
            {
                try
                {
                    if (_dockerClient != null && !string.IsNullOrEmpty(_appContainerId))
                    {
                        await _dockerClient.Containers.StopContainerAsync(_appContainerId, new ContainerStopParameters { WaitBeforeKillSeconds = 3 });
                        AddLog("CRITICAL", $"[DEFENSE SUCCESS] Đã dừng thành công Container '{AppContainerName}'. Ứng dụng nghiệp vụ đã được cô lập an toàn!");
                    }
                }
                catch (Exception ex)
                {
                    AddLog("CRITICAL", $"[DEFENSE FAILED] Lỗi dừng container: {ex.Message}");
                }
            });
        }

        public static void AddLog(string type, string message)
        {
            var log = new LogEntry
            {
                Timestamp = DateTime.Now,
                Type = type,
                Message = message
            };

            // Lưu vào SQLite
            lock (DbLock)
            {
                try
                {
                    using var db = new MonitorDbContext();
                    db.Logs.Add(log);
                    db.SaveChanges();

                    // Dọn dẹp log cũ quá 500 dòng
                    var count = db.Logs.Count();
                    if (count > 500)
                    {
                        var oldestLogs = db.Logs.OrderBy(l => l.Timestamp).Take(count - 500).ToList();
                        db.Logs.RemoveRange(oldestLogs);
                        db.SaveChanges();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB ERROR] Lỗi ghi log SQLite: {ex.Message}");
                }
            }

            // In ra console monitor để debug
            Console.ForegroundColor = type switch
            {
                "CRITICAL" => ConsoleColor.Red,
                "WARNING" => ConsoleColor.Yellow,
                "SUSPICIOUS" => ConsoleColor.DarkYellow,
                "ACCESS" => ConsoleColor.Blue,
                "SYSTEM" => ConsoleColor.Cyan,
                _ => ConsoleColor.White
            };
            Console.WriteLine($"[{log.Timestamp:HH:mm:ss}] [{type}] {message}");
            Console.ResetColor();
        }

        private static void ClearBlockedIpsInDb()
        {
            lock (DbLock)
            {
                try
                {
                    using var db = new MonitorDbContext();
                    db.BlockedIps.RemoveRange(db.BlockedIps);
                    db.SaveChanges();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DB ERROR] Lỗi xóa BlockedIps: {ex.Message}");
                }
            }
        }

        private static async Task CheckAndLogNewAccessAsync(string ip, string rawLog)
        {
            if (SeenIps.ContainsKey(ip)) return;

            string country = "Local Network (Mạng nội bộ)";
            bool isPublicIp = true;

            // Kiểm tra IP private
            if (ip == "127.0.0.1" || ip == "::1" || ip == "localhost" ||
                ip.StartsWith("10.") || ip.StartsWith("192.168.") ||
                ip.StartsWith("172.16.") || ip.StartsWith("172.17.") || ip.StartsWith("172.18.") ||
                ip.StartsWith("172.19.") || ip.StartsWith("172.20.") || ip.StartsWith("172.21.") ||
                ip.StartsWith("172.22.") || ip.StartsWith("172.23.") || ip.StartsWith("172.24.") ||
                ip.StartsWith("172.25.") || ip.StartsWith("172.26.") || ip.StartsWith("172.27.") ||
                ip.StartsWith("172.28.") || ip.StartsWith("172.29.") || ip.StartsWith("172.30.") ||
                ip.StartsWith("172.31."))
            {
                isPublicIp = false;
            }

            if (isPublicIp)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    var response = await _httpClient.GetAsync($"http://ip-api.com/json/{ip}", cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var content = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(content);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "success")
                        {
                            string countryName = (root.TryGetProperty("country", out var countryProp) ? countryProp.GetString() : null) ?? "";
                            string city = (root.TryGetProperty("city", out var cityProp) ? cityProp.GetString() : null) ?? "";
                            country = string.IsNullOrEmpty(city) ? countryName : $"{city}, {countryName}";
                            if (string.IsNullOrEmpty(country)) country = "Local Network/Không rõ";
                        }
                    }
                }
                catch
                {
                    country = "Không rõ quốc gia";
                }
            }

            if (!string.IsNullOrEmpty(country) && SeenIps.TryAdd(ip, country))
            {
                lock (DbLock)
                {
                    try
                    {
                        using var db = new MonitorDbContext();
                        db.SeenIps.Add(new SeenIpRecord { Ip = ip, Country = country, FirstSeen = DateTime.Now });
                        db.SaveChanges();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DB ERROR] Lỗi ghi SeenIp: {ex.Message}");
                    }
                }
            }
        }

        private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();

        public static async Task SendTelegramAlertAsync(string message)
        {
            string? token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            string? chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId) || token == "YOUR_BOT_TOKEN_HERE")
            {
                return;
            }

            try
            {
                var url = $"https://api.telegram.org/bot{token}/sendMessage";
                var payload = new
                {
                    chat_id = chatId,
                    text = message,
                    parse_mode = "Markdown"
                };

                var json = JsonSerializer.Serialize(payload);
                var content = new System.Net.Http.StringContent(json, Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync(url, content);
                if (!response.IsSuccessStatusCode)
                {
                    var errorDetails = await response.Content.ReadAsStringAsync();
                    AddLog("WARNING", $"[TELEGRAM ERROR] Gửi tin nhắn thất bại: Code {response.StatusCode} | Chi tiết: {errorDetails}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TELEGRAM ERROR] Lỗi kết nối: {ex.Message}");
            }
        }

        private static async Task MonitorTelegramCommandsLoopAsync(CancellationToken cancellationToken)
        {
            string? token = Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN");
            string? chatId = Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID");

            if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(chatId) || token == "YOUR_BOT_TOKEN_HERE")
            {
                AddLog("WARNING", "[TELEGRAM COMMANDS] Bỏ qua khởi chạy lắng nghe lệnh do chưa cấu hình Token.");
                return;
            }

            AddLog("SYSTEM", "[TELEGRAM COMMANDS] Khởi chạy vòng lặp lắng nghe lệnh từ Telegram...");
            long offset = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var url = $"https://api.telegram.org/bot{token}/getUpdates?offset={offset}&timeout=10";
                    var responseString = await _httpClient.GetStringAsync(url, cancellationToken);
                    using var doc = JsonDocument.Parse(responseString);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var update in resultElement.EnumerateArray())
                        {
                            long updateId = update.GetProperty("update_id").GetInt64();
                            offset = updateId + 1;

                            if (update.TryGetProperty("message", out var messageElement))
                            {
                                var fromElement = messageElement.GetProperty("from");
                                long fromId = fromElement.GetProperty("id").GetInt64();

                                if (fromId.ToString() == chatId && messageElement.TryGetProperty("text", out var textElement))
                                {
                                    string command = textElement.GetString()?.Trim().ToLower() ?? "";
                                    await HandleTelegramCommandAsync(command);
                                }
                            }
                        }
                    }
                }
                catch
                {
                    // Lỗi mạng hoặc parse, chờ 5s rồi thử lại
                    await Task.Delay(5000, cancellationToken);
                }
                
                await Task.Delay(2000, cancellationToken);
            }
        }

        private static async Task HandleTelegramCommandAsync(string command)
        {
            if (command == "/start_web" || command == "/reset" || command == "/restore")
            {
                AddLog("SYSTEM", $"[TELEGRAM COMMAND] Nhận yêu cầu khởi động Web App qua Telegram: '{command}'");

                if (_dockerClient == null)
                {
                    await SendTelegramAlertAsync("❌ *Thất bại:* Docker Client chưa sẵn sàng.");
                    return;
                }

                try
                {
                    if (string.IsNullOrEmpty(_appContainerId))
                    {
                        await ResolveContainerIdsAsync();
                    }

                    if (string.IsNullOrEmpty(_appContainerId))
                    {
                        await SendTelegramAlertAsync("❌ *Thất bại:* Không tìm thấy ID cho container `secure-app`.");
                        return;
                    }

                    // Khởi chạy lại container
                    await _dockerClient.Containers.StartContainerAsync(_appContainerId, new ContainerStartParameters());

                    // Cập nhật trạng thái
                    SecurityStatus = BlockedIps.Count > 0 ? "UNDER_ATTACK" : "SAFE";
                    ThreatDetails = "Hệ thống đã được khởi động lại qua Telegram (danh sách IP bị khóa được giữ nguyên).";

                    AddLog("SYSTEM", "[TELEGRAM COMMAND SUCCESS] Đã khởi chạy lại Container secure-app thành công (không xóa chặn IP).");
                    await SendTelegramAlertAsync($"✅ *KHỞI CHẠY WEB APP THÀNH CÔNG!*\n\n- Dịch vụ `secure-app` đã hoạt động trở lại.\n- Trạng thái hệ thống: *{SecurityStatus}*\n- Lưu ý: Danh sách IP bị khóa hiện tại vẫn được giữ nguyên để bảo vệ hệ thống.");
                }
                catch (Exception ex)
                {
                    AddLog("CRITICAL", $"[TELEGRAM COMMAND FAILED] Lỗi khôi phục: {ex.Message}");
                    await SendTelegramAlertAsync($"❌ *Thất bại:* Lỗi khi khởi chạy container: {ex.Message}");
                }
            }
            else if (command == "/unblock_all" || command == "/unlock_all")
            {
                AddLog("SYSTEM", "[TELEGRAM COMMAND] Nhận yêu cầu gỡ chặn toàn bộ IP.");
                
                try
                {
                    BlockedIps.Clear();
                    AttackTracker.Clear();
                    ClearBlockedIpsInDb();

                    AddLog("SYSTEM", "[TELEGRAM COMMAND SUCCESS] Đã gỡ chặn toàn bộ IP qua Telegram.");
                    await SendTelegramAlertAsync("✅ *GỠ CHẶN TOÀN BỘ IP THÀNH CÔNG!*\n\n- Danh sách IP bị khóa đã được xóa sạch.");
                }
                catch (Exception ex)
                {
                    await SendTelegramAlertAsync($"❌ *Lỗi:* Không thể gỡ chặn toàn bộ IP: {ex.Message}");
                }
            }
            else if (command.StartsWith("/unblock ") || command.StartsWith("/unlock "))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendTelegramAlertAsync("❌ *Lỗi:* Vui lòng chỉ định IP cần mở khóa. Ví dụ: `/unblock 192.168.1.1`.");
                    return;
                }

                string targetIp = parts[1].Trim();
                bool removedInMemory = BlockedIps.TryRemove(targetIp, out _);
                bool removedInDb = false;

                try
                {
                    using var db = new MonitorDbContext();
                    var record = db.BlockedIps.FirstOrDefault(b => b.Ip == targetIp);
                    if (record != null)
                    {
                        db.BlockedIps.Remove(record);
                        db.SaveChanges();
                        removedInDb = true;
                    }
                }
                catch (Exception ex)
                {
                    AddLog("SYSTEM", $"[TELEGRAM ERROR] Lỗi xóa IP {targetIp} khỏi DB: {ex.Message}");
                }

                if (removedInMemory || removedInDb)
                {
                    AddLog("SYSTEM", $"[TELEGRAM COMMAND SUCCESS] Đã gỡ chặn riêng cho IP: {targetIp}");
                    await SendTelegramAlertAsync($"✅ *GỠ CHẶN THÀNH CÔNG!*\n\n- IP: `{targetIp}` đã được mở khóa tự do truy cập.");
                }
                else
                {
                    await SendTelegramAlertAsync($"❌ *Thất bại:* IP `{targetIp}` hiện không nằm trong danh sách chặn.");
                }
            }
            else if (command == "/list_blocked" || command == "/blocked")
            {
                if (BlockedIps.Count == 0)
                {
                    await SendTelegramAlertAsync("ℹ️ *Danh sách chặn trống.* Hiện tại không có IP nào bị khóa.");
                    return;
                }

                var sb = new StringBuilder();
                sb.AppendLine("🚫 *DANH SÁCH IP ĐANG BỊ KHÓA:*");
                int index = 1;
                foreach (var kv in BlockedIps)
                {
                    sb.AppendLine($"{index}. `{kv.Key}` (Bị khóa lúc: *{kv.Value}*)");
                    index++;
                }

                await SendTelegramAlertAsync(sb.ToString());
            }
            else if (command == "/status")
            {
                var appRunning = !string.IsNullOrEmpty(_appContainerId) && await IsContainerRunning(_appContainerId);
                var dbRunning = !string.IsNullOrEmpty(_dbContainerId) && await IsContainerRunning(_dbContainerId);

                string statusMsg = $"📊 *BÁO CÁO TRẠNG THÁI HỆ THỐNG*\n\n" +
                                   $"- Trạng thái chung: *{SecurityStatus}*\n" +
                                   $"- Container `{AppContainerName}`: {(appRunning ? "🟢 Running" : "🔴 Stopped")}\n" +
                                   $"- Container `{DbContainerName}`: {(dbRunning ? "🟢 Running" : "🔴 Stopped")}\n" +
                                   $"- Số lượng IP đang bị khóa: {BlockedIps.Count}";
                await SendTelegramAlertAsync(statusMsg);
            }
            else if (command.StartsWith("/whitelist "))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendTelegramAlertAsync("❌ *Lỗi:* Vui lòng chỉ định IP cần thêm vào whitelist. Ví dụ: `/whitelist 192.168.1.1`.");
                    return;
                }

                string targetIp = parts[1].Trim();
                
                // Gỡ chặn nếu IP này đang bị Blocked
                BlockedIps.TryRemove(targetIp, out _);
                try
                {
                    using var db = new MonitorDbContext();
                    var record = db.BlockedIps.FirstOrDefault(b => b.Ip == targetIp);
                    if (record != null)
                    {
                        db.BlockedIps.Remove(record);
                        db.SaveChanges();
                    }
                }
                catch {}

                var timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                bool added = WhitelistedIps.TryAdd(targetIp, timeStr);
                if (added)
                {
                    try
                    {
                        using var db = new MonitorDbContext();
                        db.WhitelistedIps.Add(new WhitelistedIpRecord { Ip = targetIp, Time = timeStr });
                        db.SaveChanges();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[DB ERROR] Whitelist insert: {ex.Message}");
                    }
                    AddLog("SYSTEM", $"[TELEGRAM COMMAND] Đã thêm IP {targetIp} vào Whitelist.");
                    await SendTelegramAlertAsync($"✅ *THÊM WHITELIST THÀNH CÔNG!*\n\n- IP: `{targetIp}` đã được đưa vào danh sách tin cậy và gỡ chặn (nếu có).");
                }
                else
                {
                    await SendTelegramAlertAsync($"❌ *Thất bại:* IP `{targetIp}` đã có sẵn trong danh sách tin cậy.");
                }
            }
            else if (command.StartsWith("/unwhitelist ") || command.StartsWith("/remove_whitelist "))
            {
                var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendTelegramAlertAsync("❌ *Lỗi:* Vui lòng chỉ định IP cần gỡ khỏi whitelist. Ví dụ: `/unwhitelist 192.168.1.1`.");
                    return;
                }

                string targetIp = parts[1].Trim();
                bool removedInMemory = WhitelistedIps.TryRemove(targetIp, out _);
                bool removedInDb = false;

                try
                {
                    using var db = new MonitorDbContext();
                    var record = db.WhitelistedIps.FirstOrDefault(w => w.Ip == targetIp);
                    if (record != null)
                    {
                        db.WhitelistedIps.Remove(record);
                        db.SaveChanges();
                        removedInDb = true;
                    }
                }
                catch (Exception ex)
                {
                    AddLog("SYSTEM", $"[TELEGRAM ERROR] Lỗi xóa IP {targetIp} khỏi whitelist DB: {ex.Message}");
                }

                if (removedInMemory || removedInDb)
                {
                    AddLog("SYSTEM", $"[TELEGRAM COMMAND SUCCESS] Đã gỡ IP khỏi Whitelist: {targetIp}");
                    await SendTelegramAlertAsync($"✅ *GỠ WHITELIST THÀNH CÔNG!*\n\n- IP: `{targetIp}` đã bị loại bỏ khỏi danh sách tin cậy.");
                }
                else
                {
                    await SendTelegramAlertAsync($"❌ *Thất bại:* IP `{targetIp}` hiện không nằm trong danh sách tin cậy.");
                }
            }
            else if (command == "/stats")
            {
                try
                {
                    using var db = new MonitorDbContext();
                    var since = DateTime.Now.AddHours(-24);
                    var logs = db.Logs.Where(l => l.Timestamp >= since).ToList();

                    int sqli = logs.Count(l => l.Type == "CRITICAL" && (l.Message.Contains("SQL-INJECTION") || l.Message.Contains("SQL INJECTION")));
                    int xss = logs.Count(l => l.Type == "CRITICAL" && (l.Message.Contains("XSS") || l.Message.Contains("CROSS-SITE SCRIPTING")));

                    var totalBlocked = db.BlockedIps.Count();
                    var totalWhite = db.WhitelistedIps.Count();

                    string statsMsg = $"📊 *THỐNG KÊ AN NINH 24H QUA*\n\n" +
                                     $"🔹 *Tấn công SQLi:* `{sqli}` lần\n" +
                                     $"🔹 *Tấn công XSS:* `{xss}` lần\n\n" +
                                     $"🚫 *IP bị khóa hiện tại:* `{totalBlocked}`\n" +
                                     $"🛡️ *IP Whitelist hiện tại:* `{totalWhite}`";

                    await SendTelegramAlertAsync(statsMsg);
                }
                catch (Exception ex)
                {
                    await SendTelegramAlertAsync($"❌ *Lỗi lấy thống kê:* {ex.Message}");
                }
            }
            else if (command == "/restart_all")
            {
                AddLog("SYSTEM", "[TELEGRAM COMMAND] Nhận yêu cầu khởi động lại toàn bộ hệ thống.");
                await SendTelegramAlertAsync("⏳ *Đang khởi động lại toàn bộ các container dịch vụ (secure-app, postgres-db, nginx-proxy)...*");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        if (_dockerClient != null)
                        {
                            int count = 0;
                            if (!string.IsNullOrEmpty(_appContainerId)) { await _dockerClient.Containers.RestartContainerAsync(_appContainerId, new ContainerRestartParameters()); count++; }
                            if (!string.IsNullOrEmpty(_dbContainerId)) { await _dockerClient.Containers.RestartContainerAsync(_dbContainerId, new ContainerRestartParameters()); count++; }
                            if (!string.IsNullOrEmpty(_nginxContainerId)) { await _dockerClient.Containers.RestartContainerAsync(_nginxContainerId, new ContainerRestartParameters()); count++; }

                            await SendTelegramAlertAsync($"✅ *KHỞI ĐỘNG LẠI THÀNH CÔNG!*\n\n- Đã khởi động lại thành công `{count}` container dịch vụ.");
                        }
                        else
                        {
                            await SendTelegramAlertAsync("❌ *Thất bại:* Docker Client chưa sẵn sàng.");
                        }
                    }
                    catch (Exception ex)
                    {
                        await SendTelegramAlertAsync($"❌ *Lỗi khởi động lại:* {ex.Message}");
                    }
                });
            }
            else if (command == "/help" || command == "/start")
            {
                string helpMsg = "🤖 *VHU SECURITY SHIELD BOT*\n\n" +
                                 "Danh sách lệnh được hỗ trợ:\n" +
                                 "👉 `/start_web` : Khởi chạy lại Web App (giữ nguyên danh sách IP bị khóa).\n" +
                                 "👉 `/list_blocked` : Xem danh sách các IP đang bị chặn.\n" +
                                 "👉 `/unblock_all` : Gỡ chặn toàn bộ IP (không tác động tới Web App).\n" +
                                 "👉 `/unblock <ip>` : Gỡ chặn riêng cho một địa chỉ IP nhất định.\n" +
                                 "👉 `/status` : Kiểm tra trạng thái hoạt động và số IP bị khóa.\n" +
                                 "👉 `/stats` : Thống kê số cuộc tấn công trong 24h qua.\n" +
                                 "👉 `/whitelist <ip>` : Thêm IP vào danh sách tin cậy (bỏ qua chặn WAF/Rate limit).\n" +
                                 "👉 `/unwhitelist <ip>` : Gỡ IP khỏi danh sách tin cậy.\n" +
                                 "👉 `/restart_all` : Khởi động lại toàn bộ hệ thống container dịch vụ.\n" +
                                 "👉 `/help` : Hiển thị bảng hướng dẫn này.";
                await SendTelegramAlertAsync(helpMsg);
            }
        }
    }

    public class LogEntry
    {
        public int Id { get; set; }
        public DateTime Timestamp { get; set; }
        public string Type { get; set; } = "INFO"; // INFO, ACCESS, WARNING, SUSPICIOUS, CRITICAL, SYSTEM
        public string Message { get; set; } = string.Empty;
    }

    public class BlockedIpRecord
    {
        public int Id { get; set; }
        public string Ip { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
    }

    public class WhitelistedIpRecord
    {
        public int Id { get; set; }
        public string Ip { get; set; } = string.Empty;
        public string Time { get; set; } = string.Empty;
    }

    public class SeenIpRecord
    {
        public int Id { get; set; }
        public string Ip { get; set; } = string.Empty;
        public string Country { get; set; } = string.Empty;
        public DateTime FirstSeen { get; set; }
    }

    public class LoginDto
    {
        public string Email { get; set; } = string.Empty;
        public string Code { get; set; } = string.Empty;
    }

    public class ContainerMetrics
    {
        public string Name { get; set; } = string.Empty;
        public double CpuUsage { get; set; } = 0.0;
        public double MemUsage { get; set; } = 0.0;
        public double MemRawMb { get; set; } = 0.0;
        public long MemLimitMb { get; set; } = 0;
        public bool IsRunning { get; set; } = false;
    }

    public class MonitorDbContext : DbContext
    {
        public DbSet<LogEntry> Logs { get; set; } = null!;
        public DbSet<BlockedIpRecord> BlockedIps { get; set; } = null!;
        public DbSet<WhitelistedIpRecord> WhitelistedIps { get; set; } = null!;
        public DbSet<SeenIpRecord> SeenIps { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseSqlite("Data Source=data/monitor.db");
        }
    }

    public static class TotpHelper
    {
        // Khóa Secret Base32 mới cố định cho Google Authenticator: K5AU2VI5EBEUTK7P
        public const string Secret = "K5AU2VI5EBEUTK7P";

        public static bool VerifyCode(string code)
        {
            if (string.IsNullOrEmpty(code) || code.Length != 6) return false;

            try
            {
                byte[] secretBytes = Base32Decode(Secret);
                long unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                long timeStep = unixTime / 30;

                // Mở rộng cửa sổ lệch giờ +/- 2 bước (60 giây) đề phòng lệch giờ giữa Server và Điện thoại
                for (long i = -2; i <= 2; i++)
                {
                    long checkStep = timeStep + i;
                    if (GetCode(secretBytes, checkStep) == code) return true;
                }
            }
            catch
            {
                // Bỏ qua lỗi
            }
            return false;
        }

        private static string GetCode(byte[] secret, long step)
        {
            byte[] stepAsBytes = BitConverter.GetBytes(step);
            if (BitConverter.IsLittleEndian)
            {
                Array.Reverse(stepAsBytes);
            }
            byte[] buffer = new byte[8];
            Array.Copy(stepAsBytes, 0, buffer, 8 - stepAsBytes.Length, stepAsBytes.Length);

            using (var hmac = new System.Security.Cryptography.HMACSHA1(secret))
            {
                byte[] hash = hmac.ComputeHash(buffer);
                int offset = hash[hash.Length - 1] & 0xf;
                int binary = ((hash[offset] & 0x7f) << 24) |
                             ((hash[offset + 1] & 0xff) << 16) |
                             ((hash[offset + 2] & 0xff) << 8) |
                             (hash[offset + 3] & 0xff);
                int password = binary % 1000000;
                return password.ToString("D6");
            }
        }

        private static byte[] Base32Decode(string base32)
        {
            string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
            base32 = base32.ToUpper().TrimEnd('=');
            if (string.IsNullOrEmpty(base32)) return Array.Empty<byte>();

            int byteCount = base32.Length * 5 / 8;
            byte[] returnBytes = new byte[byteCount];
            
            byte curByte = 0;
            int bitsRemaining = 8;
            int mask = 0;
            int arrayIndex = 0;

            foreach (char c in base32)
            {
                int cValue = chars.IndexOf(c);
                if (cValue < 0) continue;

                if (bitsRemaining > 5)
                {
                    mask = cValue << (bitsRemaining - 5);
                    curByte = (byte)(curByte | mask);
                    bitsRemaining -= 5;
                }
                else
                {
                    mask = cValue >> (5 - bitsRemaining);
                    curByte = (byte)(curByte | mask);
                    if (arrayIndex < returnBytes.Length)
                    {
                        returnBytes[arrayIndex++] = curByte;
                    }
                    curByte = (byte)(cValue << (3 + bitsRemaining));
                    bitsRemaining = bitsRemaining + 8 - 5;
                }
            }
            return returnBytes;
        }
    }
}
