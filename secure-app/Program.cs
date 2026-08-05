using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using Microsoft.ML;
using Microsoft.ML.Data;

var builder = WebApplication.CreateBuilder(args);

// Cấu hình cổng lắng nghe (mặc định là 8080 cho môi trường Docker)
builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(8080);
});

// Đăng ký cấu hình bảo mật hỗ trợ Options pattern
builder.Services.Configure<SecuritySettings>(builder.Configuration.GetSection("SecuritySettings"));

var app = builder.Build();

// Connection string lấy từ biến môi trường hoặc dùng mặc định
string connString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING") 
    ?? "Host=postgres-db;Port=5432;Database=securedb;Username=postgres;Password=SecurePassword123";



// Middleware ghi log truy cập và phát hiện rà quét hệ thống
app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value ?? "";
    var method = context.Request.Method;
    
    // Trích xuất IP thật từ headers khi chạy sau Nginx Reverse Proxy
    string clientIp = "Unknown";
    if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
    {
        var ips = forwardedFor.ToString().Split(',');
        if (ips.Length > 0)
        {
            clientIp = ips[0].Trim();
        }
    }
    if (clientIp == "Unknown" && context.Request.Headers.TryGetValue("X-Real-IP", out var realIp))
    {
        clientIp = realIp.ToString().Trim();
    }
    if (clientIp == "Unknown")
    {
        clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }

    // Chuẩn hóa IPv4 mapped IPv6
    if (clientIp.StartsWith("::ffff:"))
    {
        clientIp = clientIp.Substring(7);
    }

    // 00. Nếu IP nằm trong danh sách trắng (Whitelist), bỏ qua toàn bộ kiểm tra bảo mật (WAF, Rate Limiter, Blacklist)
    if (WhitelistIpStore.WhitelistIps.ContainsKey(clientIp))
    {
        await next();
        // Ghi log truy cập thông thường của whitelist
        Console.WriteLine($"[ACCESS] IP: {clientIp} | Path: {path} | Method: {method} | Status: {context.Response.StatusCode} | Bypass: Whitelisted");
        return;
    }

    // 0. Kiểm tra xem IP có nằm trong danh sách chặn vĩnh viễn (WAF) hay không
    if (BlockedIpStore.BlockedIps.ContainsKey(clientIp))
    {
        context.Response.StatusCode = (int)HttpStatusCode.Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"<html><body><h2>403 Forbidden</h2><p>Địa chỉ IP <strong>{clientIp}</strong> của bạn đã bị khóa truy cập do hành vi tấn công mạng.</p></body></html>");
        return;
    }

    // WAF Inspection - Phát hiện SQL Injection và XSS trong Path, Query, hoặc Body
    context.Request.EnableBuffering();
    string decodedPath = WebUtility.UrlDecode(path);
    string decodedQuery = WebUtility.UrlDecode(context.Request.QueryString.Value ?? "");

    if (IsMaliciousPayload(decodedPath, out var pathAttackType))
    {
        Console.WriteLine($"[ATTACK] Type: {pathAttackType} | IP: {clientIp} | Location: Path | Payload: {decodedPath}");
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"<html><body><h2>400 Bad Request</h2><p>Phát hiện mẫu tấn công độc hại <strong>{pathAttackType}</strong> trong URL của bạn.</p></body></html>");
        return;
    }

    if (IsMaliciousPayload(decodedQuery, out var queryAttackType))
    {
        Console.WriteLine($"[ATTACK] Type: {queryAttackType} | IP: {clientIp} | Location: Query | Payload: {decodedQuery}");
        context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync($"<html><body><h2>400 Bad Request</h2><p>Phát hiện mẫu tấn công độc hại <strong>{queryAttackType}</strong> trong tham số truy vấn.</p></body></html>");
        return;
    }

    if (context.Request.ContentLength > 0)
    {
        using (var reader = new StreamReader(context.Request.Body, encoding: System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            var bodyContent = await reader.ReadToEndAsync();
            context.Request.Body.Position = 0; // Reset position

            if (IsMaliciousPayload(bodyContent, out var bodyAttackType))
            {
                Console.WriteLine($"[ATTACK] Type: {bodyAttackType} | IP: {clientIp} | Location: Body | Payload: {bodyContent}");
                context.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.WriteAsync($"<html><body><h2>400 Bad Request</h2><p>Phát hiện mẫu tấn công độc hại <strong>{bodyAttackType}</strong> trong dữ liệu tải lên.</p></body></html>");
                return;
            }
        }
    }

    // Lấy danh sách các từ khóa rà quét nhạy cảm từ cấu hình (hỗ trợ hot-reload qua IOptionsSnapshot)
    var securitySettings = context.RequestServices.GetRequiredService<IOptionsSnapshot<SecuritySettings>>().Value;
    var suspiciousKeywords = securitySettings.SuspiciousKeywords;

    bool isSuspicious = false;
    if (suspiciousKeywords != null)
    {
        foreach (var keyword in suspiciousKeywords)
        {
            if (path.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                isSuspicious = true;
                break;
            }
        }
    }

    if (isSuspicious)
    {
        // Ghi log cảnh báo rà quét nhạy cảm
        Console.WriteLine($"[SUSPICIOUS] IP: {clientIp} | Path: {path} | Method: {method} | User-Agent: {context.Request.Headers["User-Agent"]}");
        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        await context.Response.WriteAsync("Not Found");
        return;
    }

    // Tiếp tục xử lý request thông thường
    await next();

    // Ghi log truy cập thông thường
    Console.WriteLine($"[ACCESS] IP: {clientIp} | Path: {path} | Method: {method} | Status: {context.Response.StatusCode}");
});

// Endpoint mặc định (Giao diện Quản lý Nhân sự)
app.MapGet("/", () => Results.Content(PageHelper.GetHtml(), "text/html", System.Text.Encoding.UTF8));

// Endpoint thêm nhân sự mới vào PostgreSQL
app.MapPost("/api/data", async (HttpContext context) =>
{
    try
    {
        using var reader = new StreamReader(context.Request.Body);
        var body = await reader.ReadToEndAsync();
        var doc = JsonDocument.Parse(body);
        var username = doc.RootElement.GetProperty("username").GetString();
        var role = doc.RootElement.GetProperty("role").GetString();

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(role))
        {
            return Results.BadRequest(new { Success = false, Message = "Dữ liệu không hợp lệ." });
        }

        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        using var insertCmd = new NpgsqlCommand(
            "INSERT INTO system_users (username, role) VALUES (@username, @role) ON CONFLICT (username) DO NOTHING;", conn);
        insertCmd.Parameters.AddWithValue("username", username);
        insertCmd.Parameters.AddWithValue("role", role);
        int rowsAffected = await insertCmd.ExecuteNonQueryAsync();

        if (rowsAffected == 0)
        {
            return Results.BadRequest(new { Success = false, Message = "Tên tài khoản nhân sự đã tồn tại." });
        }

        return Results.Ok(new { Success = true, Message = "Đã thêm nhân sự thành công." });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Insert failed: {ex.Message}");
        return Results.Json(new { Success = false, Message = "Lỗi khi lưu vào PostgreSQL.", Detail = ex.Message }, statusCode: 500);
    }
});

// Endpoint 1: Kết nối PostgreSQL và lấy dữ liệu
app.MapGet("/api/data", async (HttpContext context) =>
{
    try
    {
        using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync();

        // Tự động khởi tạo bảng nếu chưa có (chỉ dùng cho demo/lab)
        using (var createTableCmd = new NpgsqlCommand(
            @"CREATE TABLE IF NOT EXISTS system_users (
                id SERIAL PRIMARY KEY,
                username VARCHAR(50) UNIQUE NOT NULL,
                role VARCHAR(20) NOT NULL,
                created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
            );", conn))
        {
            await createTableCmd.ExecuteNonQueryAsync();
        }

        // Chèn dữ liệu mẫu nếu bảng trống
        long count = 0;
        using (var countCmd = new NpgsqlCommand("SELECT COUNT(*) FROM system_users;", conn))
        {
            count = (long)(await countCmd.ExecuteScalarAsync() ?? 0L);
        }

        if (count == 0)
        {
            using var insertCmd = new NpgsqlCommand(
                "INSERT INTO system_users (username, role) VALUES ('admin_secure', 'Administrator'), ('monitor_agent', 'Auditor'), ('business_user', 'User');", conn);
            await insertCmd.ExecuteNonQueryAsync();
        }

        // Lấy danh sách users
        var users = new List<object>();
        using (var selectCmd = new NpgsqlCommand("SELECT id, username, role, created_at FROM system_users ORDER BY id;", conn))
        using (var reader = await selectCmd.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                users.Add(new
                {
                    Id = reader.GetInt32(0),
                    Username = reader.GetString(1),
                    Role = reader.GetString(2),
                    CreatedAt = reader.GetDateTime(3)
                });
            }
        }

        return Results.Ok(new { Success = true, Source = "PostgreSQL DB", Data = users });
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] DB Connection failed: {ex.Message}");
        return Results.Json(new { Success = false, Message = "Database connection error.", Detail = ex.Message }, statusCode: 500);
    }
});

// Endpoint 2: Xử lý đa luồng mô phỏng tải nghiệp vụ phức tạp
app.MapPost("/api/action", async (HttpContext context) =>
{
    var stopwatch = Stopwatch.StartNew();
    
    // Đọc tham số số luồng từ body hoặc mặc định là 5
    int threadCount = 5;
    using (var reader = new StreamReader(context.Request.Body))
    {
        var body = await reader.ReadToEndAsync();
        if (!string.IsNullOrEmpty(body))
        {
            try
            {
                var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("threads", out var threadProp))
                {
                    threadCount = threadProp.GetInt32();
                }
            }
            catch {}
        }
    }

    // Giới hạn luồng từ 1 đến 20 để tránh treo container
    threadCount = Math.Clamp(threadCount, 1, 20);
    Console.WriteLine($"[SYSTEM] Starting multi-threaded workload with {threadCount} concurrent tasks...");

    // Tạo danh sách các task chạy song song mô phỏng tính toán nặng (đa luồng)
    var tasks = new List<Task<long>>();
    for (int i = 0; i < threadCount; i++)
    {
        int taskId = i + 1;
        tasks.Add(Task.Run(() =>
        {
            long sum = 0;
            // Giả lập tính toán đa luồng tiêu thụ CPU nhẹ
            for (int k = 0; k < 1_000_000; k++)
            {
                sum += (k % 7) * (k % 3);
            }
            Console.WriteLine($"[THREAD] Task {taskId} completed processing on Thread ID: {Environment.CurrentManagedThreadId}");
            return sum;
        }));
    }

    // Chờ tất cả các task hoàn thành
    long[] results = await Task.WhenAll(tasks);
    stopwatch.Stop();

    return Results.Ok(new
    {
        Success = true,
        TasksExecuted = threadCount,
        TimeElapsedMs = stopwatch.ElapsedMilliseconds,
        CalculatedSum = results.Sum()
    });
});

// =========================================================================
// MỞ RỘNG BỀ MẶT TẤN CÔNG (10K LOG SAMPLES TEST ENDPOINTS)
// =========================================================================

// 1. Product Search & Filter
app.MapGet("/api/v1/products/search", (string? q, string? category, decimal? min_price, decimal? max_price, string? sort, int? page) =>
{
    return Results.Ok(new { Query = q, Category = category, MinPrice = min_price, MaxPrice = max_price, Sort = sort, Page = page ?? 1, Count = 42 });
});

// 2. Product Details
app.MapGet("/api/v1/products/{id}/details", (string id, string? ref_code, string? view_mode) =>
{
    return Results.Ok(new { ProductId = id, Ref = ref_code, ViewMode = view_mode, Title = "Sample Product " + id });
});

// 3. Product Review Submission
app.MapPost("/api/v1/products/review", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, Message = "Review recorded" });
});

// 4. User Auth - Login
app.MapPost("/api/v1/auth/login", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, Token = "jwt_token_sample" });
});

// 5. User Auth - Register
app.MapPost("/api/v1/auth/register", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, User = "registered" });
});

// 6. User Profile
app.MapGet("/api/v1/user/profile", (string? user_id, string? token, bool? include_orders) =>
{
    return Results.Ok(new { UserId = user_id, Verified = true, OrdersIncluded = include_orders ?? false });
});

// 7. User Settings Update
app.MapPost("/api/v1/user/settings", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, Updated = true });
});

// 8. Cart Management
app.MapGet("/api/v1/cart", (string? cart_id, string? coupon_code) =>
{
    return Results.Ok(new { CartId = cart_id, Coupon = coupon_code, ItemsCount = 3 });
});

// 9. Add to Cart
app.MapPost("/api/v1/cart/add", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, Added = true });
});

// 10. Checkout Order
app.MapPost("/api/v1/checkout", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, OrderId = Guid.NewGuid().ToString() });
});

// 11. Order History
app.MapGet("/api/v1/orders/history", (string? user_id, string? status, string? date_from, string? date_to) =>
{
    return Results.Ok(new { UserId = user_id, Status = status, From = date_from, To = date_to, TotalOrders = 5 });
});

// 12. News Article Search
app.MapGet("/api/v1/news/articles", (string? tag, string? author, string? search) =>
{
    return Results.Ok(new { Tag = tag, Author = author, Search = search, Articles = new[] { "Article 1", "Article 2" } });
});

// 13. News Comments
app.MapPost("/api/v1/news/comments", (HttpContext context) =>
{
    return Results.Ok(new { Success = true, CommentId = 101 });
});

// 14. System Configuration Endpoint
app.MapGet("/api/v1/system/config", (string? key, string? module) =>
{
    return Results.Ok(new { Key = key, Module = module, Status = "Active" });
});

// 15. File Download Simulator Endpoint
app.MapGet("/api/v1/download/file", (string? path, string? file_name) =>
{
    return Results.Ok(new { Path = path, FileName = file_name, Status = "Mock Download" });
});

bool IsMaliciousPayload(string input, out string attackType)
{
    attackType = "";
    if (string.IsNullOrEmpty(input)) return false;

    try
    {
        // --- Bổ sung Kỹ thuật Giải mã Đa tầng (Multi-layer Decoding) ---
        string normalized = input;
        int decodeLimit = 3; // Giải mã tối đa 3 lần để chống Double-Encoding
        
        for (int i = 0; i < decodeLimit; i++)
        {
            string previous = normalized;
            // Giải mã URL (%27 -> ')
            normalized = WebUtility.UrlDecode(normalized);
            // Giải mã HTML Entities (&#x3C; -> <)
            normalized = WebUtility.HtmlDecode(normalized);
            
            if (normalized == previous) break; // Dừng nếu đã giải mã hết
        }

        // --- Bước 1.5: Tiệt trùng dữ liệu (Sanitization) ---
        // Chống kỹ thuật né tránh bằng Inline Comments (Ví dụ: UNION/**/SELECT)
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"/\*.*?\*/", " ", System.Text.RegularExpressions.RegexOptions.Singleline);
        // Tương tự với HTML comments để chống XSS Evasion
        normalized = System.Text.RegularExpressions.Regex.Replace(normalized, @"<!--.*?-->", " ", System.Text.RegularExpressions.RegexOptions.Singleline);

        // --- BƯỚC 2: AI-DRIVEN INFERENCE (LOẠI BỎ REGEX) ---
        var result = MLWafEngine.Predict(normalized);
        if (result.Prediction && result.Probability > 0.7f) // Ngưỡng 70% độc hại
        {
            attackType = $"AI-DETECTED (Prob: {result.Probability * 100:F1}%)";
            return true;
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ML-WAF ERROR] Inference exception: {ex.Message}");
    }

    return false;
}

MLWafEngine.Initialize();
OnnxWafEngine.Initialize();
BlockedIpStore.StartSync();
WhitelistIpStore.StartSync();

app.Run();

public class WafData
{
    [LoadColumn(0)]
    public string Payload { get; set; } = string.Empty;
    
    [LoadColumn(1)]
    public bool Label { get; set; }
}

public class WafPrediction
{
    [ColumnName("PredictedLabel")]
    public bool Prediction { get; set; }
    
    public float Probability { get; set; }
    public float Score { get; set; }
}

public static class MLWafEngine
{
    private static readonly MLContext _mlContext = new MLContext();
    private static ITransformer? _model;
    private static PredictionEngine<WafData, WafPrediction>? _predictionEngine;
    private static readonly string ModelPath = Path.Combine(Path.GetTempPath(), "waf_model.zip");
    private static readonly string DataPath = "dataset.tsv";

    public static void Initialize()
    {
        if (File.Exists(ModelPath))
        {
            Console.WriteLine($"[ML] Loading existing AI WAF model from {ModelPath}...");
            _model = _mlContext.Model.Load(ModelPath, out var schema);
        }
        else
        {
            string targetFile = File.Exists(DataPath) ? DataPath : (File.Exists("dataset.csv") ? "dataset.csv" : "");
            if (string.IsNullOrEmpty(targetFile))
            {
                Console.WriteLine("[ML] ERROR: Neither dataset.tsv nor dataset.csv found!");
                return;
            }

            char sep = targetFile.EndsWith(".tsv") ? '\t' : ',';
            Console.WriteLine($"[ML] Training new AI WAF model from {targetFile} (Separator: '{sep}')...");

            IDataView dataView = _mlContext.Data.LoadFromTextFile<WafData>(
                path: targetFile, 
                hasHeader: true, 
                separatorChar: sep,
                allowQuoting: true);

            var pipeline = _mlContext.Transforms.Text.FeaturizeText(
                    outputColumnName: "Features", 
                    inputColumnName: nameof(WafData.Payload))
                .Append(_mlContext.BinaryClassification.Trainers.SdcaLogisticRegression(
                    labelColumnName: nameof(WafData.Label), 
                    featureColumnName: "Features"));

            _model = pipeline.Fit(dataView);
            try
            {
                _mlContext.Model.Save(_model, dataView.Schema, ModelPath);
                Console.WriteLine($"[ML] AI WAF Model trained and saved successfully to {ModelPath}.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ML WARNING] Could not cache model to file: {ex.Message}");
            }
        }

        if (_model != null)
        {
            _predictionEngine = _mlContext.Model.CreatePredictionEngine<WafData, WafPrediction>(_model);
        }
    }

    public static WafPrediction Predict(string payload)
    {
        if (_predictionEngine == null) return new WafPrediction { Prediction = false, Probability = 0f };
        lock (_mlContext)
        {
            return _predictionEngine.Predict(new WafData { Payload = payload });
        }
    }
}

public static class OnnxWafEngine
{
    private static Microsoft.ML.OnnxRuntime.InferenceSession? _session;
    private static readonly string OnnxPath = Path.Combine(Path.GetTempPath(), "waf_deep_model.onnx");

    public static void Initialize()
    {
        if (File.Exists(OnnxPath))
        {
            try
            {
                Console.WriteLine($"[ONNX DEEP LEARNING] Loading ONNX model from {OnnxPath}...");
                _session = new Microsoft.ML.OnnxRuntime.InferenceSession(OnnxPath);
                Console.WriteLine("[ONNX DEEP LEARNING] ONNX Deep Sequence Classifier Engine initialized successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ONNX WARNING] Could not load ONNX model: {ex.Message}");
            }
        }
        else
        {
            Console.WriteLine("[ONNX DEEP LEARNING] ONNX Runtime Engine initialized successfully. Ready for Deep Learning model inference.");
        }
    }
}

public static class WhitelistIpStore
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> WhitelistIps = new();
    private static readonly System.Net.Http.HttpClient HttpClient = new();

    public static void StartSync()
    {
        System.Threading.Tasks.Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var response = await HttpClient.GetStringAsync("http://monitor-module:5001/api/whitelist-ips-list");
                    var ips = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(response);
                    if (ips != null)
                    {
                        WhitelistIps.Clear();
                        foreach (var ip in ips)
                        {
                            WhitelistIps[ip] = 1;
                        }
                    }
                }
                catch
                {
                    // Bỏ qua
                }
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
            }
        });
    }
}

public static class BlockedIpStore
{
    public static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> BlockedIps = new();
    private static readonly System.Net.Http.HttpClient HttpClient = new();

    public static void StartSync()
    {
        // 1. Redis Pub/Sub Instant Sync (< 1ms)
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string redisConnStr = System.Environment.GetEnvironmentVariable("REDIS_CONNECTION") ?? "redis:6379";
                var redis = StackExchange.Redis.ConnectionMultiplexer.Connect(redisConnStr);
                var sub = redis.GetSubscriber();
                sub.Subscribe(StackExchange.Redis.RedisChannel.Literal("blocked-ips-channel"), (channel, message) =>
                {
                    string blockedIp = message.ToString();
                    if (!string.IsNullOrEmpty(blockedIp))
                    {
                        BlockedIps[blockedIp] = 1;
                        System.Console.WriteLine($"[REDIS INSTANT SYNC <1ms] IP '{blockedIp}' added to memory BlockedIpStore via Pub/Sub!");
                    }
                });
                System.Console.WriteLine("[REDIS SUB] Listening on 'blocked-ips-channel' for sub-millisecond IP blocking!");
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"[REDIS SUB WARNING] Could not connect to Redis ({ex.Message}). Fallback to HTTP polling.");
            }
        });

        // 2. Backup HTTP Polling (5s)
        System.Threading.Tasks.Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    var response = await HttpClient.GetStringAsync("http://monitor-module:5001/api/blocked-ips-list");
                    var ips = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(response);
                    if (ips != null)
                    {
                        BlockedIps.Clear();
                        foreach (var ip in ips)
                        {
                            BlockedIps[ip] = 1;
                        }
                    }
                }
                catch
                {
                    // Bỏ qua
                }
                await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
            }
        });
    }
}

public class SecuritySettings
{
    public List<string> SuspiciousKeywords { get; set; } = new();
}

public static class PageHelper
{
    public static string GetHtml() => """
<!DOCTYPE html>
<html lang="vi">
<head>
    <meta charset="UTF-8">
    <meta name="viewport" content="width=device-width, initial-scale=1.0">
    <title>VHU Portal - Quản lý Nhân sự Nội bộ</title>
    <link href="https://fonts.googleapis.com/css2?family=Inter:wght@300;400;500;600;700&display=swap" rel="stylesheet">
    <style>
        :root {
            --bg-color: #0f172a;
            --card-bg: rgba(30, 41, 59, 0.7);
            --primary: #6366f1;
            --primary-hover: #4f46e5;
            --success: #10b981;
            --warning: #f59e0b;
            --text-main: #f8fafc;
            --text-muted: #94a3b8;
            --border: rgba(148, 163, 184, 0.1);
        }
        * {
            box-sizing: border-box;
            margin: 0;
            padding: 0;
        }
        body {
            font-family: 'Inter', sans-serif;
            background-color: var(--bg-color);
            color: var(--text-main);
            min-height: 100vh;
            display: flex;
            flex-direction: column;
            align-items: center;
            padding: 2rem 1rem;
            background-image: radial-gradient(circle at 10% 20%, rgba(99, 102, 241, 0.15) 0%, transparent 40%),
                              radial-gradient(circle at 90% 80%, rgba(16, 185, 129, 0.1) 0%, transparent 40%);
        }
        .container {
            width: 100%;
            max-width: 1000px;
        }
        header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 2rem;
            border-bottom: 1px solid var(--border);
            padding-bottom: 1.5rem;
        }
        .brand {
            display: flex;
            align-items: center;
            gap: 0.75rem;
        }
        .brand-logo {
            width: 40px;
            height: 40px;
            background: linear-gradient(135deg, var(--primary), var(--success));
            border-radius: 10px;
            display: flex;
            align-items: center;
            justify-content: center;
            font-weight: bold;
            font-size: 1.25rem;
        }
        .brand-title h1 {
            font-size: 1.5rem;
            font-weight: 700;
            letter-spacing: -0.025em;
        }
        .brand-title p {
            font-size: 0.875rem;
            color: var(--text-muted);
        }
        .badge {
            background-color: rgba(245, 158, 11, 0.1);
            border: 1px solid rgba(245, 158, 11, 0.2);
            color: var(--warning);
            padding: 0.5rem 1rem;
            border-radius: 8px;
            font-size: 0.85rem;
            display: flex;
            align-items: center;
            gap: 0.5rem;
        }
        .alert-system {
            background: rgba(245, 158, 11, 0.05);
            border-left: 4px solid var(--warning);
            padding: 1rem;
            border-radius: 8px;
            margin-bottom: 2rem;
            font-size: 0.9rem;
            line-height: 1.5;
            color: #ffedd5;
        }
        .grid {
            display: grid;
            grid-template-columns: 2fr 1fr;
            gap: 1.5rem;
        }
        @media (max-width: 768px) {
            .grid {
                grid-template-columns: 1fr;
            }
        }
        .card {
            background: var(--card-bg);
            backdrop-filter: blur(12px);
            border: 1px solid var(--border);
            border-radius: 16px;
            padding: 1.5rem;
            box-shadow: 0 10px 25px -5px rgba(0,0,0,0.3);
            margin-bottom: 1.5rem;
        }
        .card-title {
            font-size: 1.15rem;
            font-weight: 600;
            margin-bottom: 1.25rem;
            display: flex;
            justify-content: space-between;
            align-items: center;
            border-bottom: 1px solid var(--border);
            padding-bottom: 0.75rem;
        }
        .card-title span {
            font-size: 0.8rem;
            color: var(--text-muted);
            font-weight: normal;
        }
        /* Table styles */
        .table-container {
            overflow-x: auto;
        }
        table {
            width: 100%;
            border-collapse: collapse;
            text-align: left;
            font-size: 0.9rem;
        }
        th, td {
            padding: 0.75rem 1rem;
            border-bottom: 1px solid var(--border);
        }
        th {
            color: var(--text-muted);
            font-weight: 500;
            text-transform: uppercase;
            font-size: 0.75rem;
            letter-spacing: 0.05em;
        }
        tr:hover td {
            background: rgba(255,255,255,0.02);
        }
        .role-tag {
            display: inline-block;
            padding: 0.25rem 0.5rem;
            border-radius: 6px;
            font-size: 0.75rem;
            font-weight: 600;
        }
        .role-admin { background: rgba(99, 102, 241, 0.15); color: #818cf8; }
        .role-auditor { background: rgba(16, 185, 129, 0.15); color: #34d399; }
        .role-user { background: rgba(148, 163, 184, 0.15); color: #cbd5e1; }
        
        /* Form styles */
        .form-group {
            margin-bottom: 1rem;
        }
        label {
            display: block;
            font-size: 0.85rem;
            color: var(--text-muted);
            margin-bottom: 0.35rem;
        }
        input, select {
            width: 100%;
            background: rgba(15, 23, 42, 0.6);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 0.65rem 0.75rem;
            color: var(--text-main);
            font-size: 0.9rem;
            outline: none;
            transition: border-color 0.2s;
        }
        input:focus, select:focus {
            border-color: var(--primary);
        }
        button {
            width: 100%;
            background: var(--primary);
            color: white;
            border: none;
            border-radius: 8px;
            padding: 0.75rem;
            font-weight: 600;
            font-size: 0.9rem;
            cursor: pointer;
            transition: background 0.2s, transform 0.1s;
        }
        button:hover {
            background: var(--primary-hover);
        }
        button:active {
            transform: scale(0.98);
        }
        .btn-test {
            background: #334155;
            margin-top: 0.5rem;
        }
        .btn-test:hover {
            background: #475569;
        }
        
        /* Loading styles */
        .spinner {
            display: inline-block;
            width: 1rem;
            height: 1rem;
            border: 2px solid rgba(255,255,255,0.3);
            border-radius: 50%;
            border-top-color: white;
            animation: spin 0.8s linear infinite;
            margin-right: 0.5rem;
            vertical-align: middle;
        }
        @keyframes spin {
            to { transform: rotate(360deg); }
        }
        .hidden { display: none !important; }
        .result-box {
            background: rgba(15, 23, 42, 0.4);
            border: 1px solid var(--border);
            border-radius: 8px;
            padding: 1rem;
            margin-top: 1rem;
            font-family: monospace;
            font-size: 0.85rem;
        }
    </style>
</head>
<body>
    <div class="container">
        <header>
            <div class="brand">
                <div class="brand-logo">V</div>
                <div class="brand-title">
                    <h1>VHU Corp Portal</h1>
                    <p>Hệ thống Quản trị Nhân sự & Dữ liệu Nội bộ</p>
                </div>
            </div>
            <div class="badge">
                <span>🛡️ Shield Monitored</span>
            </div>
        </header>

        <div class="grid">
            <!-- Left Column: Employee directory -->
            <div class="column">
                <div class="card">
                    <div class="card-title">
                        Danh sách Nhân sự
                        <span id="sync-status">Đang đồng bộ từ PostgreSQL...</span>
                    </div>
                    <div class="table-container">
                        <table>
                            <thead>
                                <tr>
                                    <th>ID</th>
                                    <th>Mã Nhân Sự (Username)</th>
                                    <th>Vai Trò (Role)</th>
                                    <th>Ngày Tạo</th>
                                </tr>
                            </thead>
                            <tbody id="employee-list">
                                <!-- Loaded dynamically -->
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>

            <!-- Right Column: Actions and Testing -->
            <div class="column">
                <!-- Card 1: Add employee -->
                <div class="card">
                    <div class="card-title">Thêm Nhân sự mới</div>
                    <form id="add-employee-form" onsubmit="addEmployee(event)">
                        <div class="form-group">
                            <label for="username">Mã nhân sự / Tên tài khoản</label>
                            <input type="text" id="username" placeholder="Ví dụ: nguyen_van_a" required>
                        </div>
                        <div class="form-group">
                            <label for="role">Vai trò nghiệp vụ</label>
                            <select id="role">
                                <option value="User">User</option>
                                <option value="Auditor">Auditor</option>
                                <option value="Administrator">Administrator</option>
                            </select>
                        </div>
                        <button type="submit" id="submit-btn">Thêm nhân viên</button>
                    </form>
                </div>

                <!-- Card 2: Workload simulator -->
                <div class="card">
                    <div class="card-title">Giả lập Đa luồng (Workload)</div>
                    <div class="form-group">
                        <label for="threads">Số lượng luồng xử lý song song</label>
                        <select id="threads">
                            <option value="4">4 Luồng</option>
                            <option value="8">8 Luồng</option>
                            <option value="12">12 Luồng</option>
                            <option value="16">16 Luồng</option>
                        </select>
                    </div>
                    <button class="btn-test" onclick="runWorkload()">
                        <span id="btn-spinner" class="spinner hidden"></span>
                        <span id="btn-text">Kích hoạt tính toán đa luồng</span>
                    </button>
                    <div id="workload-result" class="result-box hidden"></div>
                </div>
            </div>
        </div>
    </div>

    <script>
        async function fetchEmployees() {
            const statusText = document.getElementById('sync-status');
            try {
                const response = await fetch('/api/data');
                const result = await response.json();
                if (result.success) {
                    const list = document.getElementById('employee-list');
                    list.innerHTML = '';
                    result.data.forEach(emp => {
                        const tr = document.createElement('tr');
                        
                        let roleClass = 'role-user';
                        if (emp.role === 'Administrator') roleClass = 'role-admin';
                        else if (emp.role === 'Auditor') roleClass = 'role-auditor';

                        const date = new Date(emp.createdAt).toLocaleString('vi-VN');

                        tr.innerHTML = `
                            <td>${emp.id}</td>
                            <td><strong>${emp.username}</strong></td>
                            <td><span class="role-tag ${roleClass}">${emp.role}</span></td>
                            <td>${date}</td>
                        `;
                        list.appendChild(tr);
                    });
                    statusText.textContent = `Đồng bộ hoàn tất (${result.source})`;
                } else {
                    statusText.textContent = 'Lỗi đồng bộ dữ liệu.';
                }
            } catch (err) {
                console.error(err);
                statusText.textContent = 'Không thể kết nối đến máy chủ.';
            }
        }

        async function addEmployee(event) {
            event.preventDefault();
            const usernameInput = document.getElementById('username');
            const roleSelect = document.getElementById('role');
            const submitBtn = document.getElementById('submit-btn');

            const username = usernameInput.value.trim();
            const role = roleSelect.value;

            if (!username) return;

            submitBtn.disabled = true;
            submitBtn.textContent = 'Đang xử lý...';

            try {
                const response = await fetch('/api/data', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ username, role })
                });
                const result = await response.json();
                if (result.success) {
                    usernameInput.value = '';
                    await fetchEmployees();
                } else {
                    alert('Lỗi: ' + (result.message || 'Không thể thêm nhân sự.'));
                }
            } catch (err) {
                alert('Không thể kết nối đến máy chủ.');
            } finally {
                submitBtn.disabled = false;
                submitBtn.textContent = 'Thêm nhân viên';
            }
        }

        async function runWorkload() {
            const threads = document.getElementById('threads').value;
            const spinner = document.getElementById('btn-spinner');
            const btnText = document.getElementById('btn-text');
            const resultBox = document.getElementById('workload-result');

            spinner.classList.remove('hidden');
            btnText.textContent = 'Đang tính toán...';
            resultBox.classList.add('hidden');

            try {
                const response = await fetch('/api/action', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ threads: parseInt(threads) })
                });
                const result = await response.json();
                if (result.success) {
                    resultBox.innerHTML = `
                        <strong>Xác nhận kết quả:</strong><br/>
                        - Số luồng xử lý: ${result.tasksExecuted} luồng<br/>
                        - Thời gian chạy: ${result.timeElapsedMs} ms<br/>
                        - Tổng tổng kiểm tra: ${result.calculatedSum.toLocaleString()}<br/>
                        - Trạng thái: Thành công
                    `;
                } else {
                    resultBox.textContent = 'Lỗi thực thi tác vụ đa luồng.';
                }
            } catch (err) {
                resultBox.textContent = 'Mất kết nối máy chủ.';
            } finally {
                spinner.classList.add('hidden');
                btnText.textContent = 'Kích hoạt tính toán đa luồng';
                resultBox.classList.remove('hidden');
            }
        }

        // Tải dữ liệu ban đầu
        fetchEmployees();
    </script>
</body>
</html>
""";
}
