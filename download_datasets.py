import urllib.request
import csv
import sys
import os

def download_and_clean():
    print("Starting dataset download and cleaning script...")
    
    # 1. Download SQLi dataset (UTF-16 encoded)
    sqli_url = "https://raw.githubusercontent.com/ajinmathew/SQL-data/master/sqliv2.csv"
    print(f"Downloading SQLi dataset from {sqli_url}...")
    try:
        req = urllib.request.Request(sqli_url, headers={'User-Agent': 'Mozilla/5.0'})
        with urllib.request.urlopen(req) as response:
            sqli_raw = response.read().decode('utf-16')
    except Exception as e:
        print(f"Error downloading SQLi dataset: {e}")
        return

    # Parse SQLi dataset
    sqli_lines = sqli_raw.strip().split('\n')
    sqli_data = []
    
    # Reader to parse CSV with quotes correctly
    reader = csv.reader(sqli_lines)
    header = next(reader) # Sentence, Label
    
    for row in reader:
        if len(row) >= 2:
            payload = row[0].strip()
            label = row[1].strip()
            # Clean double quotes and normalizations
            if payload:
                # Label is 1 or 0
                try:
                    lbl = int(label)
                    sqli_data.append((payload, lbl))
                except ValueError:
                    pass
    
    print(f"Loaded {len(sqli_data)} SQLi queries.")

    # 2. Download XSS dataset
    xss_url = "https://raw.githubusercontent.com/danielmiessler/SecLists/master/Fuzzing/XSS/robot-friendly/XSS-Jhaddix.txt"
    print(f"Downloading XSS payloads from {xss_url}...")
    xss_data = []
    try:
        req = urllib.request.Request(xss_url, headers={'User-Agent': 'Mozilla/5.0'})
        with urllib.request.urlopen(req) as response:
            xss_raw = response.read().decode('utf-8', errors='ignore')
            for line in xss_raw.strip().split('\n'):
                line = line.strip()
                if line and len(line) > 3:
                    xss_data.append((line, 1)) # All are malicious
    except Exception as e:
        print(f"Error downloading XSS dataset: {e}")
        return

    print(f"Loaded {len(xss_data)} XSS payloads.")

    # 3. Add more Safe queries (Benign) to balance the dataset
    safe_samples = [
        "hello world", "good morning", "what is your name", "order status check", 
        "search?q=cotton+shirts", "category/shoes/running", "profile/settings", 
        "get_all_products", "register?username=john_doe&email=john@example.com",
        "index.html", "main.js", "style.css", "images/logo.png",
        "feedback?rating=5&comment=great+product", "check_out_cart",
        "john.doe@company.com", "2026-07-21", "page=3&sort=price_asc",
        "{\"action\":\"refresh\",\"target\":\"dashboard\"}", "reset_password_request",
        "vietnam travel guide", "nguyen van a", "ho chi minh city", "ha noi capital",
        "docker compose up", "dotnet publish -c Release", "learn machine learning"
    ]
    
    for sample in safe_samples:
        sqli_data.append((sample, 0))

    # Merge everything
    final_dataset = []
    seen = set()
    
    # Add SQLi & Safe
    for payload, label in sqli_data:
        if payload not in seen:
            seen.add(payload)
            final_dataset.append((payload, label))
            
    # Add XSS
    for payload, label in xss_data:
        if payload not in seen:
            seen.add(payload)
            final_dataset.append((payload, label))

    # Write to target CSV
    target_path = "dataset.csv"
    print(f"Saving merged dataset to {target_path}...")
    with open(target_path, 'w', encoding='utf-8', newline='') as f:
        writer = csv.writer(f)
        writer.writerow(["Payload", "Label"])
        for payload, label in final_dataset:
            writer.writerow([payload, label])
            
    print(f"Dataset compiled successfully. Total records: {len(final_dataset)}")

if __name__ == "__main__":
    download_and_clean()
