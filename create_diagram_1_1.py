import os
import matplotlib.pyplot as plt
import matplotlib.patches as patches

def generate_docker_vs_vm_diagram():
    os.makedirs("d:/DA.ATTT/images", exist_ok=True)
    img_path = "d:/DA.ATTT/images/fig_1_1_docker_vs_vm.png"

    # Set up figure
    fig, ax = plt.subplots(figsize=(12, 6.5), dpi=300)
    ax.set_xlim(0, 12)
    ax.set_ylim(0, 7)
    ax.axis('off')

    # Colors
    c_infra = '#2C3E50'      # Dark slate
    c_host = '#34495E'       # Navy gray
    c_hyper = '#8E44AD'      # Purple
    c_docker = '#006699'     # Docker Blue
    c_guest = '#E67E22'      # Orange
    c_app = '#27AE60'        # Green
    c_bin = '#F39C12'        # Yellow-orange

    # Title
    ax.text(6, 6.6, "SO SÁNH KIẾN TRÚC MÁY ẢO TRUYỀN THỐNG (VM) VÀ DOCKER CONTAINER", 
            ha='center', va='center', fontsize=14, fontweight='bold', color='#003366')

    # ==================== LEFT BOX: VIRTUAL MACHINES ====================
    # Outer Frame Left
    rect_left = patches.Rectangle((0.5, 0.4), 5.2, 5.8, linewidth=1.5, edgecolor='#BDC3C7', facecolor='#F4F6F7')
    ax.add_patch(rect_left)
    ax.text(3.1, 5.9, "MÁY ẢO TRUYỀN THỐNG (VIRTUAL MACHINES)", ha='center', va='center', fontsize=11, fontweight='bold', color='#990000')

    # Base Layers VM
    # Infrastructure
    ax.add_patch(patches.Rectangle((0.8, 0.7), 4.6, 0.6, facecolor=c_infra, edgecolor='white'))
    ax.text(3.1, 1.0, "Infrastructure (Hạ tầng phần cứng / Server)", ha='center', va='center', color='white', fontweight='bold', fontsize=9.5)

    # Host OS
    ax.add_patch(patches.Rectangle((0.8, 1.4), 4.6, 0.6, facecolor=c_host, edgecolor='white'))
    ax.text(3.1, 1.7, "Host Operating System (Hệ điều hành Host)", ha='center', va='center', color='white', fontweight='bold', fontsize=9.5)

    # Hypervisor
    ax.add_patch(patches.Rectangle((0.8, 2.1), 4.6, 0.6, facecolor=c_hyper, edgecolor='white'))
    ax.text(3.1, 2.4, "Hypervisor (Type 1 / Type 2 - ESXi, KVM, VirtualBox)", ha='center', va='center', color='white', fontweight='bold', fontsize=9.5)

    # VM 1 Block
    ax.add_patch(patches.Rectangle((0.8, 2.8), 2.2, 2.8, facecolor='#EAECEE', edgecolor='#8E44AD', linestyle='--', linewidth=1.2))
    ax.text(1.9, 5.3, "Virtual Machine 1", ha='center', va='center', fontsize=9, fontweight='bold', color='#8E44AD')
    # Guest OS 1
    ax.add_patch(patches.Rectangle((0.9, 2.9), 2.0, 0.7, facecolor=c_guest, edgecolor='white'))
    ax.text(1.9, 3.25, "Guest OS (Linux/Win)", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')
    # Bins/Libs 1
    ax.add_patch(patches.Rectangle((0.9, 3.7), 2.0, 0.6, facecolor=c_bin, edgecolor='white'))
    ax.text(1.9, 4.0, "Bins & Libraries", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')
    # App 1
    ax.add_patch(patches.Rectangle((0.9, 4.4), 2.0, 0.7, facecolor=c_app, edgecolor='white'))
    ax.text(1.9, 4.75, "App 1 (Nghiệp vụ)", ha='center', va='center', color='white', fontsize=9, fontweight='bold')

    # VM 2 Block
    ax.add_patch(patches.Rectangle((3.2, 2.8), 2.2, 2.8, facecolor='#EAECEE', edgecolor='#8E44AD', linestyle='--', linewidth=1.2))
    ax.text(4.3, 5.3, "Virtual Machine 2", ha='center', va='center', fontsize=9, fontweight='bold', color='#8E44AD')
    # Guest OS 2
    ax.add_patch(patches.Rectangle((3.3, 2.9), 2.0, 0.7, facecolor=c_guest, edgecolor='white'))
    ax.text(4.3, 3.25, "Guest OS (Linux/Win)", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')
    # Bins/Libs 2
    ax.add_patch(patches.Rectangle((3.3, 3.7), 2.0, 0.6, facecolor=c_bin, edgecolor='white'))
    ax.text(4.3, 4.0, "Bins & Libraries", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')
    # App 2
    ax.add_patch(patches.Rectangle((3.3, 4.4), 2.0, 0.7, facecolor=c_app, edgecolor='white'))
    ax.text(4.3, 4.75, "App 2 (DB Server)", ha='center', va='center', color='white', fontsize=9, fontweight='bold')


    # ==================== RIGHT BOX: DOCKER CONTAINERS ====================
    # Outer Frame Right
    rect_right = patches.Rectangle((6.3, 0.4), 5.2, 5.8, linewidth=1.5, edgecolor='#BDC3C7', facecolor='#F4F6F7')
    ax.add_patch(rect_right)
    ax.text(8.9, 5.9, "DOCKER CONTAINERS (CONTAINER ARCHITECTURE)", ha='center', va='center', fontsize=11, fontweight='bold', color='#006699')

    # Base Layers Docker
    # Infrastructure
    ax.add_patch(patches.Rectangle((6.6, 0.7), 4.6, 0.6, facecolor=c_infra, edgecolor='white'))
    ax.text(8.9, 1.0, "Infrastructure (Hạ tầng phần cứng / AWS EC2)", ha='center', va='center', color='white', fontweight='bold', fontsize=9.5)

    # Host OS + Shared Kernel
    ax.add_patch(patches.Rectangle((6.6, 1.4), 4.6, 0.6, facecolor=c_host, edgecolor='white'))
    ax.text(8.9, 1.7, "Host OS (Linux Kernel - Namespaces & cgroups)", ha='center', va='center', color='white', fontweight='bold', fontsize=9.5)

    # Docker Engine
    ax.add_patch(patches.Rectangle((6.6, 2.1), 4.6, 0.6, facecolor=c_docker, edgecolor='white'))
    ax.text(8.9, 2.4, "Docker Engine (Container Runtime / containerd)", ha='center', va='center', color='white', fontweight='bold', fontsize=10)

    # Container 1 Block
    ax.add_patch(patches.Rectangle((6.6, 2.8), 2.2, 2.8, facecolor='#EBF5FB', edgecolor='#006699', linestyle='-', linewidth=1.2))
    ax.text(7.7, 5.3, "Container 1 (secure-app)", ha='center', va='center', fontsize=9, fontweight='bold', color='#006699')
    # Bins/Libs 1
    ax.add_patch(patches.Rectangle((6.7, 2.9), 2.0, 1.3, facecolor=c_bin, edgecolor='white'))
    ax.text(7.7, 3.55, "Bins & .NET 8 Runtime\n(No Guest OS needed)", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')
    # App 1
    ax.add_patch(patches.Rectangle((6.7, 4.3), 2.0, 0.8, facecolor=c_app, edgecolor='white'))
    ax.text(7.7, 4.7, "secure-app\n(AI WAF Middleware)", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')

    # Container 2 Block
    ax.add_patch(patches.Rectangle((9.0, 2.8), 2.2, 2.8, facecolor='#EBF5FB', edgecolor='#006699', linestyle='-', linewidth=1.2))
    ax.text(10.1, 5.3, "Container 2 (postgres-db)", ha='center', va='center', fontsize=9, fontweight='bold', color='#006699')
    # Bins/Libs 2
    ax.add_patch(patches.Rectangle((9.1, 2.9), 2.0, 1.3, facecolor=c_bin, edgecolor='white'))
    ax.text(10.1, 3.55, "Bins & Postgres Engine\n(Shared Host Kernel)", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')
    # App 2
    ax.add_patch(patches.Rectangle((9.1, 4.3), 2.0, 0.8, facecolor=c_app, edgecolor='white'))
    ax.text(10.1, 4.7, "postgres-db\n(Database CSDL)", ha='center', va='center', color='white', fontsize=8.5, fontweight='bold')

    plt.tight_layout()
    plt.savefig(img_path, bbox_inches='tight', dpi=300)
    plt.close()
    print(f"DIAGRAM GENERATED SUCCESSFULLY AT: {img_path}")

if __name__ == "__main__":
    generate_docker_vs_vm_diagram()
