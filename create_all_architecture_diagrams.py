import os
import matplotlib.pyplot as plt
import matplotlib.patches as patches

def create_diagrams():
    os.makedirs("d:/DA.ATTT/images", exist_ok=True)
    
    # -------------------------------------------------------------------------
    # HÌNH 1.2: ML.NET AI WAF PIPELINE FLOWCHART
    # -------------------------------------------------------------------------
    fig, ax = plt.subplots(figsize=(11, 4.5), dpi=300)
    ax.set_xlim(0, 11)
    ax.set_ylim(0, 4.5)
    ax.axis('off')
    
    ax.text(5.5, 4.1, "SƠ ĐỒ LUỒNG HUẤN LUYỆN VÀ SUY LUẬN AI-DRIVEN WAF VỚI ML.NET", 
            ha='center', va='center', fontsize=12, fontweight='bold', color='#003366')

    # Steps
    steps = [
        ("1. HTTP Request", "Payload Input\n(Query / Body)", "#34495E"),
        ("2. Tiền xử lý", "Multi-layer Decode\n& Sanitization", "#E67E22"),
        ("3. Trích xuất đặc trưng", "FeaturizeText\n(Character N-grams)", "#8E44AD"),
        ("4. Mô hình AI", "SDCA Logistic\nRegression Model", "#006699"),
        ("5. Kết quả Suy luận", "WafPrediction\n(Probability > 70%)", "#27AE60")
    ]
    
    for i, (title, desc, col) in enumerate(steps):
        x = 0.5 + i * 2.1
        # Box
        ax.add_patch(patches.Rectangle((x, 1.2), 1.8, 2.2, facecolor='#F4F6F7', edgecolor=col, linewidth=1.5))
        # Header
        ax.add_patch(patches.Rectangle((x, 2.8), 1.8, 0.6, facecolor=col, edgecolor='none'))
        ax.text(x + 0.9, 3.1, title, ha='center', va='center', color='white', fontweight='bold', fontsize=8.5)
        # Content
        ax.text(x + 0.9, 2.0, desc, ha='center', va='center', color='#2C3E50', fontweight='bold', fontsize=8.5)
        
        # Arrow
        if i < 4:
            ax.annotate('', xy=(x + 2.1, 2.3), xytext=(x + 1.8, 2.3),
                        arrowprops=dict(arrowstyle="->", color='#003366', lw=2))

    # Threshold Note
    ax.text(5.5, 0.5, "Ngưỡng quyết định: Nếu Probability > 0.70 -> Gắn nhãn AI-DETECTED và chặn 400 Bad Request", 
            ha='center', va='center', fontsize=9.5, fontweight='bold', color='#990000',
            bbox=dict(boxstyle="round,pad=0.4", fc="#FDEDEC", ec="#E74C3C", lw=1))

    plt.tight_layout()
    plt.savefig("d:/DA.ATTT/images/fig_1_2_ai_waf_ml_flow.png", bbox_inches='tight', dpi=300)
    plt.close()

    # -------------------------------------------------------------------------
    # HÌNH 2.1: USE CASE DIAGRAM
    # -------------------------------------------------------------------------
    fig, ax = plt.subplots(figsize=(10, 6), dpi=300)
    ax.set_xlim(0, 10)
    ax.set_ylim(0, 6)
    ax.axis('off')

    ax.text(5, 5.6, "SƠ ĐỒ CA SỬ DỤNG (USE CASE DIAGRAM) HỆ THỐNG BẢO VỆ VÀ GIÁM SÁT", 
            ha='center', va='center', fontsize=12, fontweight='bold', color='#003366')

    # System boundary box
    ax.add_patch(patches.Rectangle((2.2, 0.4), 5.6, 4.8, facecolor='#F9EBEA', edgecolor='#990000', linewidth=1.5, linestyle='--'))
    ax.text(5.0, 4.9, "HỆ THỐNG GIÁM SÁT & BẢO VỆ DOCKER", ha='center', va='center', fontsize=10, fontweight='bold', color='#990000')

    # Actors
    # Left: End User
    ax.add_patch(patches.Circle((1.0, 4.2), 0.35, facecolor='#27AE60', edgecolor='white'))
    ax.text(1.0, 4.2, "User", ha='center', va='center', color='white', fontweight='bold', fontsize=8.5)
    ax.text(1.0, 3.6, "End User\n(Người dùng)", ha='center', va='center', fontsize=8.5, fontweight='bold')

    # Left: Attacker
    ax.add_patch(patches.Circle((1.0, 1.6), 0.35, facecolor='#C0392B', edgecolor='white'))
    ax.text(1.0, 1.6, "Hack", ha='center', va='center', color='white', fontweight='bold', fontsize=8.5)
    ax.text(1.0, 1.0, "Attacker\n(Kẻ tấn công)", ha='center', va='center', fontsize=8.5, fontweight='bold')

    # Right: Admin
    ax.add_patch(patches.Circle((9.0, 2.9), 0.35, facecolor='#006699', edgecolor='white'))
    ax.text(9.0, 2.9, "Admin", ha='center', va='center', color='white', fontweight='bold', fontsize=8.5)
    ax.text(9.0, 2.3, "Security Admin\n(Quản trị viên)", ha='center', va='center', fontsize=8.5, fontweight='bold')

    # Use Cases (Ellipses)
    ucs = [
        (5.0, 4.3, "UC1: Truy cập Web nghiệp vụ", '#E8F8F5', '#27AE60'),
        (5.0, 3.5, "UC2: Lọc độc hại AI WAF", '#EBF5FB', '#006699'),
        (5.0, 2.7, "UC3: Chặn IP & Sliding Window 404", '#FDEDEC', '#C0392B'),
        (5.0, 1.9, "UC4: Giám sát Dashboard & MFA", '#F4ECF7', '#8E44AD'),
        (5.0, 1.1, "UC5: Cảnh báo & Lệnh Telegram", '#FEF9E7', '#F39C12')
    ]

    for x, y, text, bg, border in ucs:
        ax.add_patch(patches.Ellipse((x, y), 3.6, 0.6, facecolor=bg, edgecolor=border, linewidth=1.2))
        ax.text(x, y, text, ha='center', va='center', fontsize=8.5, fontweight='bold', color='#2C3E50')

    # Actor Connections
    # User -> UC1
    ax.plot([1.35, 3.2], [4.2, 4.3], color='#27AE60', lw=1.2)
    # Attacker -> UC2, UC3
    ax.plot([1.35, 3.2], [1.6, 3.5], color='#C0392B', lw=1.2, linestyle=':')
    ax.plot([1.35, 3.2], [1.6, 2.7], color='#C0392B', lw=1.2)
    # Admin -> UC4, UC5
    ax.plot([8.65, 6.8], [2.9, 1.9], color='#006699', lw=1.2)
    ax.plot([8.65, 6.8], [2.9, 1.1], color='#006699', lw=1.2)

    plt.tight_layout()
    plt.savefig("d:/DA.ATTT/images/fig_2_1_use_case_diagram.png", bbox_inches='tight', dpi=300)
    plt.close()

    # -------------------------------------------------------------------------
    # HÌNH 2.2: DEFENSE IN DEPTH ARCHITECTURE
    # -------------------------------------------------------------------------
    fig, ax = plt.subplots(figsize=(11, 6), dpi=300)
    ax.set_xlim(0, 11)
    ax.set_ylim(0, 6)
    ax.axis('off')

    ax.text(5.5, 5.6, "SƠ ĐỒ KIẾN TRÚC PHÒNG THỦ ĐA SÂU (DEFENSE-IN-DEPTH 4 LỚP)", 
            ha='center', va='center', fontsize=12, fontweight='bold', color='#003366')

    layers = [
        ("LỚP 1: GATEWAY HARDENING", "Nginx Reverse Proxy (Ports 80/8080)\nHeader Forwarding (X-Forwarded-For) & SSL", "#2C3E50", 4.6),
        ("LỚP 2: AI WAF & PREPROCESSING", "ASP.NET Core .NET 8 Middleware\nMulti-layer Decode & ML.NET SDCA Model", "#006699", 3.4),
        ("LỚP 3: BEHAVIORAL RATE LIMITING", "Sliding Window 404 Rate Limiter\nBlacklist / Whitelist SQLite Sync Store", "#8E44AD", 2.2),
        ("LỚP 4: ACTIVE MONITORING & RESPONSE", "Monitor Module & Host Docker Socket\nTelegram Bot Alerts & Tiered Response", "#27AE60", 1.0)
    ]

    for title, desc, col, y in layers:
        # Layer outer box
        ax.add_patch(patches.Rectangle((1.0, y - 0.4), 9.0, 0.9, facecolor='#F8F9FA', edgecolor=col, linewidth=1.5))
        # Layer Tag
        ax.add_patch(patches.Rectangle((1.0, y - 0.4), 3.2, 0.9, facecolor=col, edgecolor='none'))
        ax.text(2.6, y, title, ha='center', va='center', color='white', fontweight='bold', fontsize=8.5)
        # Description
        ax.text(6.3, y, desc, ha='center', va='center', color='#2C3E50', fontweight='bold', fontsize=8.5)

    plt.tight_layout()
    plt.savefig("d:/DA.ATTT/images/fig_2_2_defense_in_depth.png", bbox_inches='tight', dpi=300)
    plt.close()

    # -------------------------------------------------------------------------
    # HÌNH 2.3: DOCKER NETWORK ISOLATION
    # -------------------------------------------------------------------------
    fig, ax = plt.subplots(figsize=(10, 5), dpi=300)
    ax.set_xlim(0, 10)
    ax.set_ylim(0, 5)
    ax.axis('off')

    ax.text(5.0, 4.6, "SƠ ĐỒ PHÂN VÙNG MẠNG ẢO DOCKER BRIDGE SECURE-NET VÀ CÁCH LY CSDL", 
            ha='center', va='center', fontsize=12, fontweight='bold', color='#003366')

    # Internet Cloud
    ax.add_patch(patches.Ellipse((1.5, 2.5), 2.0, 1.2, facecolor='#EBF5FB', edgecolor='#006699', linewidth=1.5))
    ax.text(1.5, 2.5, "INTERNET\n(Public Access)", ha='center', va='center', fontweight='bold', fontsize=8.5, color='#006699')

    # Bridge Network Box
    ax.add_patch(patches.Rectangle((3.2, 0.6), 6.3, 3.8, facecolor='#FEF9E7', edgecolor='#F39C12', linewidth=1.5, linestyle='--'))
    ax.text(6.35, 4.1, "DOCKER BRIDGE NETWORK: secure-net (172.x.x.x)", ha='center', va='center', fontweight='bold', fontsize=9, color='#D35400')

    # Nginx Container
    ax.add_patch(patches.Rectangle((3.6, 2.6), 1.8, 1.0, facecolor='#2C3E50', edgecolor='white'))
    ax.text(4.5, 3.1, "nginx-proxy\n(Port 80/8080)", ha='center', va='center', color='white', fontweight='bold', fontsize=8)

    # Secure App Container
    ax.add_patch(patches.Rectangle((5.8, 2.6), 1.8, 1.0, facecolor='#006699', edgecolor='white'))
    ax.text(6.7, 3.1, "secure-app\n(Non-root USER app)", ha='center', va='center', color='white', fontweight='bold', fontsize=8)

    # Postgres DB Container
    ax.add_patch(patches.Rectangle((7.4, 1.0), 1.8, 1.2, facecolor='#27AE60', edgecolor='white'))
    ax.text(8.3, 1.6, "postgres-db\n(No Port Exposed!)\nISOLATED DB", ha='center', va='center', color='white', fontweight='bold', fontsize=8)

    # Arrows
    ax.annotate('', xy=(3.6, 2.5), xytext=(2.5, 2.5), arrowprops=dict(arrowstyle="->", color='#006699', lw=2))
    ax.annotate('', xy=(5.8, 3.1), xytext=(5.4, 3.1), arrowprops=dict(arrowstyle="->", color='#2C3E50', lw=2))
    ax.annotate('', xy=(8.3, 2.2), xytext=(7.6, 2.6), arrowprops=dict(arrowstyle="->", color='#006699', lw=2))

    plt.tight_layout()
    plt.savefig("d:/DA.ATTT/images/fig_2_3_network_isolation.png", bbox_inches='tight', dpi=300)
    plt.close()

    # -------------------------------------------------------------------------
    # HÌNH 3.1: PREPROCESSING & SANITIZATION FLOW
    # -------------------------------------------------------------------------
    fig, ax = plt.subplots(figsize=(10, 4.5), dpi=300)
    ax.set_xlim(0, 10)
    ax.set_ylim(0, 4.5)
    ax.axis('off')

    ax.text(5.0, 4.1, "LUỒNG GIẢI MÃ ĐA TẦNG VÀ TIỆT TRÙNG COMMENT TRONG AI WAF", 
            ha='center', va='center', fontsize=12, fontweight='bold', color='#003366')

    blocks = [
        ("Raw Payload", "%2527UNION/**/SELECT%2527", "#C0392B", 1.2),
        ("Multi-layer Decode", "UrlDecode x3 & HtmlDecode\n-> 'UNION/**/SELECT'", "#E67E22", 3.4),
        ("Sanitization", "Regex Replace /*...*/ -> ' '\n-> 'UNION SELECT'", "#8E44AD", 5.6),
        ("AI Inference Engine", "FeaturizeText & Predict\n-> Malicious Score > 70%", "#27AE60", 7.8)
    ]

    for title, desc, col, x in blocks:
        ax.add_patch(patches.Rectangle((x - 0.9, 1.2), 1.8, 2.2, facecolor='#F4F6F7', edgecolor=col, linewidth=1.5))
        ax.add_patch(patches.Rectangle((x - 0.9, 2.8), 1.8, 0.6, facecolor=col, edgecolor='none'))
        ax.text(x, 3.1, title, ha='center', va='center', color='white', fontweight='bold', fontsize=8.5)
        ax.text(x, 2.0, desc, ha='center', va='center', color='#2C3E50', fontweight='bold', fontsize=8)

        if x < 7.0:
            ax.annotate('', xy=(x + 1.3, 2.3), xytext=(x + 0.9, 2.3), arrowprops=dict(arrowstyle="->", color='#003366', lw=2))

    plt.tight_layout()
    plt.savefig("d:/DA.ATTT/images/fig_3_1_decoding_sanitization_flow.png", bbox_inches='tight', dpi=300)
    plt.close()
    
    print("ALL 6 ARCHITECTURE DIAGRAMS GENERATED SUCCESSFULLY IN d:/DA.ATTT/images/")

if __name__ == "__main__":
    create_diagrams()
