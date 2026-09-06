import urllib.request
import ssl
import time
import sys

# Disable SSL verification for self-signed certificates on HTTPS 8443
ctx = ssl.create_default_context()
ctx.check_hostname = False
ctx.verify_mode = ssl.CERT_NONE

BASE_URL = "https://3.1.210.184:8443"

def send_request(url, desc=""):
    print(f"\n[*] Sending Request: {desc}")
    print(f"    URL: {url}")
    try:
        req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)'})
        with urllib.request.urlopen(req, context=ctx, timeout=5) as resp:
            print(f"    --> Response: HTTP {resp.status} (OK / Allowed)")
    except urllib.error.HTTPError as e:
        if e.code == 400:
            print(f"    --> Response: HTTP 400 BAD REQUEST [BLOCKED BY AI WAF!]")
        elif e.code == 403:
            print(f"    --> Response: HTTP 403 FORBIDDEN [IP IS BLACKLISTED!]")
        elif e.code == 404:
            print(f"    --> Response: HTTP 404 NOT FOUND (Scanning path)")
        elif e.code == 429:
            print(f"    --> Response: HTTP 429 TOO MANY REQUESTS (Rate Limited)")
        else:
            print(f"    --> Response: HTTP {e.code} ({e.reason})")
    except Exception as e:
        print(f"    --> Error: {e}")

def test_sqli():
    print("\n" + "="*60)
    print("KỊCH BẢN 1: TẤN CÔNG SQL INJECTION")
    print("="*60)
    payload = BASE_URL + "/?id=1%27%20UNION%20SELECT%201,2,username,password%20FROM%20users--"
    send_request(payload, "SQL Injection Attack")

def test_xss():
    print("\n" + "="*60)
    print("KỊCH BẢN 2: TẤN CÔNG CROSS-SITE SCRIPTING (XSS)")
    print("="*60)
    payload = BASE_URL + "/?search=%3Cscript%3Ealert(%27XSS%27)%3C/script%3E"
    send_request(payload, "XSS Script Injection")

def test_evasion():
    print("\n" + "="*60)
    print("KỊCH BẢN 3: TẤN CÔNG EVASION NGỤY TRANG (INLINE COMMENT)")
    print("="*60)
    payload = BASE_URL + "/?id=1%27%20UNION/*anti_waf*/SELECT/*secret*/1,2--"
    send_request(payload, "Obfuscated SQLi with Inline Comments")

def test_404_bruteforce():
    print("\n" + "="*60)
    print("KỊCH BẢN 4: RÀ QUÉT THƯ MỤC 404 BRUTE-FORCE (15 REQUESTS / 60S)")
    print("="*60)
    paths = [
        "/admin.php", "/wp-login.php", "/config.json", "/.env", "/backup.zip",
        "/db_dump.sql", "/phpmyadmin/", "/api/v1/debug", "/test.php", "/shell.php",
        "/administrator/", "/web.config", "/server-status", "/console/", "/actuator/health",
        "/final-check"
    ]
    for i, p in enumerate(paths, 1):
        print(f"\n[{i}/{len(paths)}] Scanning path: {p}")
        send_request(BASE_URL + p, f"Brute-force scan #{i}")
        time.sleep(0.3)

def main_menu():
    while True:
        print("\n" + "="*60)
        print("TOOL KIỂM THỬ TẤN CÔNG TỰ ĐỘNG — DOCKER SECURITY SHIELD")
        print(f"Target Server: {BASE_URL}")
        print("="*60)
        print("1. Chạy Kịch bản 1: Tấn công SQL Injection")
        print("2. Chạy Kịch bản 2: Tấn công XSS (Cross-Site Scripting)")
        print("3. Chạy Kịch bản 3: Tấn công Evasion ngụy trang mã độc")
        print("4. Chạy Kịch bản 4: Rà quét 404 Brute-force (Kích hoạt Blacklist)")
        print("5. Chạy TẤT CẢ kịch bản liên hoàn")
        print("0. Thoát")
        print("="*60)
        choice = input("Nhập lựa chọn của bạn (0-5): ").strip()
        if choice == '1':
            test_sqli()
        elif choice == '2':
            test_xss()
        elif choice == '3':
            test_evasion()
        elif choice == '4':
            test_404_bruteforce()
        elif choice == '5':
            test_sqli()
            time.sleep(1)
            test_xss()
            time.sleep(1)
            test_evasion()
            time.sleep(1)
            test_404_bruteforce()
        elif choice == '0':
            print("Kết thúc chương trình.")
            break
        else:
            print("Lựa chọn không hợp lệ!")

if __name__ == "__main__":
    if len(sys.argv) > 1:
        if sys.argv[1] == "all":
            test_sqli()
            test_xss()
            test_evasion()
            test_404_bruteforce()
        elif sys.argv[1] == "sqli":
            test_sqli()
        elif sys.argv[1] == "xss":
            test_xss()
        elif sys.argv[1] == "scan":
            test_404_bruteforce()
    else:
        main_menu()
