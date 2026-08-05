import urllib.request
import csv
import sys
import os

def build_mega_dataset():
    print("==========================================================")
    print("STARTING MEGA DATASET COMPILATION FROM GITHUB SECURITY REPOS")
    print("Author / Maintainer: Hulk1809 <voquocthang18092005@gmail.com>")
    print("==========================================================")

    payload_urls = {
        # 1. SQL Injection
        "SQLi_1": "https://raw.githubusercontent.com/ajinmathew/SQL-data/master/sqliv2.csv",
        
        # 2. XSS
        "XSS_Jhaddix": "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Fuzzing/XSS/robot-friendly/XSS-Jhaddix.txt",
        
        # 3. Command Injection (CMDi)
        "CMDi_Payloads": "https://raw.githubusercontent.com/payloadbox/command-injection-payload-list/master/README.md",
        
        # 4. Local File Inclusion / Path Traversal (LFI)
        "LFI_SecLists": "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Fuzzing/LFI/LFI-Jhaddix.txt",
        
        # 5. Server-Side Template Injection (SSTI)
        "SSTI_Payloads": "https://raw.githubusercontent.com/payloadbox/ssti-payloads/master/README.md",
        
        # 6. SSRF
        "SSRF_SecLists": "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Fuzzing/SSRF/SSRF-Payloads.txt",
        
        # 7. User-Agent Fuzzing / Scanners
        "Scanners_SecLists": "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Fuzzing/User-Agents/UserAgents-Malicious.txt"
    }

    final_dataset = []
    seen = set()

    # Helper function to add payload with label
    def add_payload(text, label):
        text = text.replace('\r', '').replace('\n', ' ').replace('\t', ' ').strip()
        if text and len(text) > 1 and text not in seen:
            seen.add(text)
            final_dataset.append((text, label))

    # Download & Process SQLi
    print("\n[1/7] Fetching SQLi Mega Dataset...")
    try:
        req = urllib.request.Request(payload_urls["SQLi_1"], headers={'User-Agent': 'Mozilla/5.0'})
        with urllib.request.urlopen(req) as resp:
            sqli_raw = resp.read().decode('utf-16', errors='ignore')
            reader = csv.reader(sqli_raw.strip().split('\n'))
            next(reader, None) # skip header
            for row in reader:
                if len(row) >= 2:
                    p, l = row[0].strip(), row[1].strip()
                    try:
                        lbl = int(l)
                        add_payload(p, 1 if lbl == 1 else 0)
                    except ValueError:
                        pass
        print(f" -> Collected SQLi queries.")
    except Exception as e:
        print(f" -> SQLi Fetch Warning: {e}")

    # Fetch Text Lists (XSS, LFI, SSRF, Scanners, CMDi, SSTI)
    for category, url in payload_urls.items():
        if category == "SQLi_1": continue
        print(f"\n[*] Fetching {category} from GitHub...")
        try:
            req = urllib.request.Request(url, headers={'User-Agent': 'Mozilla/5.0'})
            with urllib.request.urlopen(req) as resp:
                raw_text = resp.read().decode('utf-8', errors='ignore')
                for line in raw_text.strip().split('\n'):
                    line = line.strip()
                    if line and not line.startswith('#') and not line.startswith('```') and len(line) > 2:
                        add_payload(line, 1) # All are malicious payloads (Label = 1)
            print(f" -> Successfully parsed {category}.")
        except Exception as e:
            print(f" -> Warning fetching {category}: {e}")

    # Add Safe / Benign Data (Label = 0)
    print("\n[*] Adding Benign / Safe Web Queries (Label = 0)...")
    safe_samples = [
        "hello world", "good morning", "what is your name", "order status check", 
        "search?q=cotton+shirts", "category/shoes/running", "profile/settings", 
        "get_all_products", "register?username=john_doe&email=john@example.com",
        "index.html", "main.js", "style.css", "images/logo.png", "favicon.ico",
        "feedback?rating=5&comment=great+product", "check_out_cart",
        "john.doe@company.com", "2026-07-23", "page=3&sort=price_asc",
        "{\"action\":\"refresh\",\"target\":\"dashboard\"}", "reset_password_request",
        "vietnam travel guide", "nguyen van a", "ho chi minh city", "ha noi capital",
        "docker compose up", "dotnet publish -c Release", "learn machine learning",
        "voquocthang18092005@gmail.com", "Hulk1809", "github.com/Hulk1809",
        "/api/v1/products/search?q=phone&category=electronics&min_price=100",
        "/api/v1/cart?cart_id=12345&coupon_code=DISCOUNT10",
        "/api/v1/news/articles?search=security&tag=tech",
        "/api/v1/user/profile?user_id=101&token=sample_jwt",
        "/api/v1/orders/history?status=completed&date_from=2026-01-01"
    ]
    for s in safe_samples:
        add_payload(s, 0)

    # Save TSV format (Tab Separated Values)
    for path in ["dataset.tsv", "secure-app/dataset.tsv"]:
        print(f"\nSAVING TSV DATASET TO {path}...")
        with open(path, 'w', encoding='utf-8', newline='') as f:
            f.write("Payload\tLabel\n")
            for p, l in final_dataset:
                f.write(f"{p}\t{l}\n")

    print(f"\n==========================================================")
    print(f"COMPILATION COMPLETE! Total Unique Clean Samples: {len(final_dataset)}")
    print("==========================================================")

if __name__ == "__main__":
    build_mega_dataset()
