// Biến toàn cục quản lý biểu đồ
let resourceChart;
let attackPieChart;
let attackBarChart;

const maxDataPoints = 15;
const chartLabels = Array(maxDataPoints).fill('');
const appCpuData = Array(maxDataPoints).fill(0);
const appMemData = Array(maxDataPoints).fill(0);
const dbCpuData = Array(maxDataPoints).fill(0);
const dbMemData = Array(maxDataPoints).fill(0);

// Danh sách log đã tải để filter
let allLogs = [];

let dashboardTimer = null;

// Khởi chạy khi DOM load xong
document.addEventListener('DOMContentLoaded', () => {
    initChart();
    initAttackCharts();
    
    // Đăng ký sự kiện nhấn phím Enter trên ô nhập mã OTP
    const codeInput = document.getElementById('login-code');
    if (codeInput) {
        codeInput.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') {
                e.preventDefault();
                handleLogin();
            }
        });
    }

    // Kiểm tra Token trong sessionStorage (giữ đăng nhập khi nhấn F5, bắt buộc đăng nhập lại khi mở phiên/tab mới)
    const token = sessionStorage.getItem('mfa_token');
    if (!token || token === 'undefined' || token === 'null' || token.trim() === '') {
        sessionStorage.removeItem('mfa_token');
        showLoginOverlay(true);
    } else {
        showLoginOverlay(false);
        startDashboardUpdates();
    }
});

function startDashboardUpdates() {
    if (dashboardTimer) clearInterval(dashboardTimer);
    updateDashboard();
    dashboardTimer = setInterval(updateDashboard, 1000);
}

function stopDashboardUpdates() {
    if (dashboardTimer) {
        clearInterval(dashboardTimer);
        dashboardTimer = null;
    }
}

// Wrapper cho fetch có tích hợp Token JWT và tự động bắt lỗi 401
async function fetchWithAuth(url, options = {}) {
    const token = sessionStorage.getItem('mfa_token');
    options.headers = {
        ...options.headers,
        'Authorization': `Bearer ${token || ''}`
    };
    
    try {
        const response = await fetch(url, options);
        if (response.status === 401) {
            sessionStorage.removeItem('mfa_token');
            localStorage.removeItem('mfa_token');
            stopDashboardUpdates();
            showLoginOverlay(true);
            throw new Error("Unauthorized");
        }
        return response;
    } catch (err) {
        if (err.message === "Unauthorized") throw err;
        console.warn("Fetch error:", err);
        throw err;
    }
}

function showLoginOverlay(show) {
    const overlay = document.getElementById('login-overlay');
    if (overlay) {
        overlay.style.display = show ? 'flex' : 'none';
    }
}

async function handleLogin() {
    const emailInput = document.getElementById('login-email');
    const codeInput = document.getElementById('login-code');
    const errorDiv = document.getElementById('login-error');
    const loginBtn = document.querySelector('#login-overlay button');

    const email = emailInput ? emailInput.value.trim() : '';
    const code = codeInput ? codeInput.value.trim() : '';

    if (!code || code.length !== 6) {
        errorDiv.textContent = 'Mã xác thực phải gồm đúng 6 chữ số.';
        errorDiv.style.display = 'block';
        return;
    }

    if (loginBtn) {
        loginBtn.disabled = true;
        loginBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i> Đang xác thực...';
    }

    try {
        const response = await fetch('/api/auth/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ email: email, code: code })
        });
        
        const data = await response.json();
        if (response.status === 200 && data.token) {
            sessionStorage.setItem('mfa_token', data.token);
            showLoginOverlay(false);
            errorDiv.style.display = 'none';
            if (codeInput) codeInput.value = '';
            
            // Khởi chạy vòng lặp cập nhật
            startDashboardUpdates();
        } else {
            errorDiv.textContent = data.message || 'Mã xác thực không chính xác hoặc đã hết hạn.';
            errorDiv.style.display = 'block';
        }
    } catch (e) {
        errorDiv.textContent = 'Lỗi kết nối tới máy chủ API.';
        errorDiv.style.display = 'block';
    } finally {
        if (loginBtn) {
            loginBtn.disabled = false;
            loginBtn.innerHTML = '<i class="fa-solid fa-key"></i> Xác thực & Đăng nhập';
        }
    }
}

// Khởi tạo Chart.js
function initChart() {
    const ctx = document.getElementById('resourceChart').getContext('2d');
    
    resourceChart = new Chart(ctx, {
        type: 'line',
        data: {
            labels: chartLabels,
            datasets: [
                {
                    label: 'App CPU (%)',
                    data: appCpuData,
                    borderColor: '#7c3aed', // Violet
                    backgroundColor: 'rgba(124, 58, 237, 0.05)',
                    borderWidth: 2,
                    tension: 0.3,
                    pointRadius: 0
                },
                {
                    label: 'App RAM (%)',
                    data: appMemData,
                    borderColor: '#3b82f6', // Blue
                    backgroundColor: 'rgba(59, 130, 246, 0.05)',
                    borderWidth: 2,
                    tension: 0.3,
                    pointRadius: 0
                },
                {
                    label: 'DB CPU (%)',
                    data: dbCpuData,
                    borderColor: '#06b6d4', // Cyan
                    backgroundColor: 'rgba(6, 182, 212, 0.05)',
                    borderWidth: 2,
                    tension: 0.3,
                    pointRadius: 0
                },
                {
                    label: 'DB RAM (%)',
                    data: dbMemData,
                    borderColor: '#10b981', // Emerald
                    backgroundColor: 'rgba(16, 185, 129, 0.05)',
                    borderWidth: 2,
                    tension: 0.3,
                    pointRadius: 0
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                y: {
                    min: 0,
                    max: 100,
                    grid: {
                        color: 'rgba(255, 255, 255, 0.04)'
                    },
                    ticks: {
                        color: '#94a3b8',
                        font: { size: 10 }
                    }
                },
                x: {
                    grid: {
                        display: false
                    },
                    ticks: {
                        display: false
                    }
                }
            },
            plugins: {
                legend: {
                    position: 'top',
                    labels: {
                        color: '#e2e8f0',
                        boxWidth: 12,
                        font: {
                            size: 11,
                            family: 'Inter'
                        }
                    }
                }
            }
        }
    });
}

// Khởi tạo các biểu đồ phân tích tấn công
function initAttackCharts() {
    const pieCtx = document.getElementById('attackPieChart').getContext('2d');
    attackPieChart = new Chart(pieCtx, {
        type: 'pie',
        data: {
            labels: ['SQL Injection', 'XSS', 'Rate Limit'],
            datasets: [{
                data: [0, 0, 0],
                backgroundColor: ['#7c3aed', '#06b6d4', '#f59e0b'],
                borderWidth: 1,
                borderColor: '#141423'
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            plugins: {
                legend: {
                    position: 'bottom',
                    labels: {
                        color: '#cbd5e1',
                        font: { size: 10, family: 'Inter' },
                        boxWidth: 10
                    }
                }
            }
        }
    });

    const barCtx = document.getElementById('attackBarChart').getContext('2d');
    attackBarChart = new Chart(barCtx, {
        type: 'bar',
        data: {
            labels: [],
            datasets: [{
                label: 'Số lần',
                data: [],
                backgroundColor: 'rgba(239, 68, 68, 0.4)',
                borderColor: '#ef4444',
                borderWidth: 1.5,
                borderRadius: 4
            }]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            scales: {
                y: {
                    beginAtZero: true,
                    grid: { color: 'rgba(255, 255, 255, 0.04)' },
                    ticks: { color: '#94a3b8', font: { size: 9 } }
                },
                x: {
                    grid: { display: false },
                    ticks: { color: '#94a3b8', font: { size: 9 } }
                }
            },
            plugins: {
                legend: { display: false }
            }
        }
    });
}

// Hàm chính cập nhật Dashboard
async function updateDashboard() {
    try {
        await Promise.all([
            fetchStatus(),
            fetchLogs(),
            fetchWhitelist(),
            fetchStats()
        ]);
    } catch (error) {
        console.error("Lỗi cập nhật dashboard:", error);
    }
}

// Lấy thông tin trạng thái & tài nguyên
async function fetchStatus() {
    const response = await fetchWithAuth('/api/status');
    const data = await response.json();

    // 1. Cập nhật Badge An ninh
    const badge = document.getElementById('security-badge');
    const statusText = document.getElementById('security-status-text');
    const banner = document.getElementById('alert-banner');
    const alertDesc = document.getElementById('alert-description');

    badge.className = 'security-badge';
    
    if (data.status === 'SAFE') {
        statusText.innerHTML = '<i class="fa-solid fa-circle-check"></i> HỆ THỐNG AN TOÀN';
        banner.style.display = 'none';
    } else if (data.status === 'UNDER_ATTACK') {
        badge.classList.add('warning');
        statusText.innerHTML = '<i class="fa-solid fa-triangle-exclamation"></i> PHÁT HIỆN RÀ QUÉT';
        banner.style.display = 'none';
    } else if (data.status === 'ISOLATED') {
        badge.classList.add('danger');
        statusText.innerHTML = '<i class="fa-solid fa-shield-halved"></i> ĐÃ CÔ LẬP APP';
        banner.style.display = 'flex';
        alertDesc.textContent = data.threatDetails;
    }

    // 2. Cập nhật metrics cho từng container
    data.containers.forEach(container => {
        const name = container.name;
        
        // Trạng thái badge chạy
        const statusBadge = document.getElementById(`status-${name}`);
        const card = document.getElementById(`card-${name}`);
        
        // Bỏ qua container không có card trên Dashboard (ví dụ: nginx-proxy)
        if (!statusBadge || !card) return;

        if (container.isRunning) {
            statusBadge.textContent = 'Đang chạy';
            statusBadge.className = 'badge running';
            card.style.borderColor = 'rgba(255, 255, 255, 0.08)';
        } else {
            statusBadge.textContent = 'Đã dừng';
            statusBadge.className = 'badge stopped';
            card.style.borderColor = 'rgba(239, 68, 68, 0.2)';
        }

        // Giá trị CPU
        const cpuText = document.getElementById(`cpu-${name}`);
        const cpuBar = document.getElementById(`cpu-bar-${name}`);
        if (!cpuText || !cpuBar) return;
        const cpuVal = container.isRunning ? container.cpuUsage : 0;
        cpuText.textContent = `${cpuVal.toFixed(2)}%`;
        cpuBar.style.width = `${cpuVal}%`;

        // Giá trị Memory
        const memText = document.getElementById(`mem-${name}`);
        const memBar = document.getElementById(`mem-bar-${name}`);
        if (!memText || !memBar) return;
        const memVal = container.isRunning ? container.memUsage : 0;
        const memRaw = container.isRunning ? container.memRawMb : 0;
        memText.textContent = container.isRunning 
            ? `${memRaw}MB / ${container.memLimitMb}MB (${memVal.toFixed(1)}%)`
            : '0MB / 0MB';
        memBar.style.width = `${memVal}%`;
    });

    // 3. Cập nhật biểu đồ Chart.js
    const appStats = data.containers.find(c => c.name === 'secure-app');
    const dbStats = data.containers.find(c => c.name === 'postgres-db');

    updateChartData(
        appStats && appStats.isRunning ? appStats.cpuUsage : 0,
        appStats && appStats.isRunning ? appStats.memUsage : 0,
        dbStats && dbStats.isRunning ? dbStats.cpuUsage : 0,
        dbStats && dbStats.isRunning ? dbStats.memUsage : 0
    );

    // 4. Cập nhật danh sách IP bị khóa
    const tbody = document.getElementById('blacklist-tbody');
    if (data.blockedIps.length === 0) {
        tbody.innerHTML = '<tr><td colspan="3" class="empty-table">Chưa phát hiện IP tấn công rà quét</td></tr>';
    } else {
        tbody.innerHTML = '';
        data.blockedIps.forEach(blocked => {
            const tr = document.createElement('tr');
            tr.innerHTML = `
                <td><span style="color:#ef4444; font-weight:600;"><i class="fa-solid fa-ban"></i> ${blocked.ip}</span></td>
                <td>${blocked.time}</td>
                <td>
                    <span class="badge stopped" style="padding:4px 10px; margin-right: 8px;">Đã cách ly</span>
                    <button class="btn-unblock" onclick="unblockIp('${blocked.ip}')" style="background:#10b981; color:#fff; border:none; padding:4px 8px; border-radius:4px; cursor:pointer; font-size:11px; font-weight:600;"><i class="fa-solid fa-unlock"></i> Mở chặn</button>
                </td>
            `;
            tbody.appendChild(tr);
        });
    }
}

// Cập nhật điểm dữ liệu Chart.js
function updateChartData(appCpu, appMem, dbCpu, dbMem) {
    // Đẩy dữ liệu mới và shift bỏ phần cũ
    appCpuData.push(appCpu);
    appCpuData.shift();
    
    appMemData.push(appMem);
    appMemData.shift();

    dbCpuData.push(dbCpu);
    dbCpuData.shift();

    dbMemData.push(dbMem);
    dbMemData.shift();

    // Vẽ lại chart
    resourceChart.update('none'); // Update mượt không cần animation chạy lại
}

// Lấy danh sách Logs
async function fetchLogs() {
    const response = await fetchWithAuth('/api/logs');
    const logs = await response.json();
    
    // Lưu vào biến toàn cục để filter
    allLogs = logs;
    
    renderLogs();
}

// Hiển thị logs dựa trên filter
function renderLogs() {
    const terminal = document.getElementById('log-terminal');
    const filter = document.getElementById('log-filter-select').value;
    
    // Lưu vị trí scroll của người dùng trước khi update
    const shouldScroll = Math.abs(terminal.scrollHeight - terminal.clientHeight - terminal.scrollTop) < 20;

    terminal.innerHTML = '';

    const filteredLogs = allLogs.filter(log => {
        if (filter === 'ALL') return true;
        return log.type.toUpperCase() === filter.toUpperCase();
    });

    if (filteredLogs.length === 0) {
        terminal.innerHTML = '<div style="color: #64748b; font-style: italic;">Không có logs nào...</div>';
        return;
    }

    filteredLogs.forEach(log => {
        const time = new Date(log.timestamp).toLocaleTimeString();
        const line = document.createElement('div');
        
        // Gán class màu sắc dựa trên type
        line.className = `log-line log-${log.type.toLowerCase()}`;
        
        line.innerHTML = `<span class="log-time">[${time}]</span> ${escapeHtml(log.message)}`;
        terminal.appendChild(line);
    });

    // Tự động cuộn xuống dưới cùng nếu người dùng đang ở cuối
    if (shouldScroll) {
        terminal.scrollTop = terminal.scrollHeight;
    }
}

// Filter logs thay đổi
function filterLogs() {
    renderLogs();
}

// Điều khiển Container qua API
async function controlContainer(containerName, action) {
    try {
        const response = await fetchWithAuth('/api/control', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ containerName, action })
        });
        const result = await response.json();
        if (result.success) {
            updateDashboard();
        } else {
            alert(`Lỗi thực thi hành động: ${result.message}`);
        }
    } catch (e) {
        alert(`Lỗi kết nối tới máy chủ: ${e.message}`);
    }
}

// Nút giải quyết mối đe dọa (Khôi phục App)
async function clearThreats() {
    // Khôi phục bằng cách START lại secure-app, API điều khiển C# sẽ reset alert status
    await controlContainer('secure-app', 'start');
}

// Gửi yêu cầu gỡ chặn IP cụ thể lên máy chủ
async function unblockIp(ip) {
    if (!confirm(`Bạn có chắc chắn muốn mở chặn cho IP ${ip} không?`)) return;
    try {
        const response = await fetchWithAuth('/api/unblock', {
            method: 'POST',
            headers: {
                'Content-Type': 'application/json'
            },
            body: JSON.stringify({ ip })
        });
        const result = await response.json();
        if (result.success) {
            updateDashboard();
        } else {
            alert(`Lỗi: ${result.message}`);
        }
    } catch (e) {
        alert(`Lỗi kết nối: ${e.message}`);
    }
}

// Helper tránh lỗi XSS trong hiển thị log
function escapeHtml(text) {
    return text
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#039;");
}

async function fetchWhitelist() {
    try {
        const response = await fetchWithAuth('/api/whitelist');
        const whitelist = await response.json();
        
        const tbody = document.getElementById('whitelist-tbody');
        if (whitelist.length === 0) {
            tbody.innerHTML = '<tr><td colspan="3" class="empty-table">Chưa có IP nào trong danh sách tin cậy</td></tr>';
        } else {
            tbody.innerHTML = '';
            whitelist.forEach(item => {
                const tr = document.createElement('tr');
                tr.innerHTML = `
                    <td><span style="color:#10b981; font-weight:600;"><i class="fa-solid fa-user-shield"></i> ${item.ip}</span></td>
                    <td>${item.time}</td>
                    <td>
                        <button onclick="removeWhitelistIp('${item.ip}')" style="background:#ef4444; color:#fff; border:none; padding:4px 8px; border-radius:4px; cursor:pointer; font-size:11px; font-weight:600;"><i class="fa-solid fa-trash-can"></i> Gỡ bỏ</button>
                    </td>
                `;
                tbody.appendChild(tr);
            });
        }
    } catch (e) {
        console.error("Lỗi tải danh sách whitelist:", e);
    }
}

async function addWhitelistIp() {
    const input = document.getElementById('whitelist-input');
    const ip = input.value.trim();
    if (!ip) return;
    
    try {
        const response = await fetchWithAuth('/api/whitelist/add', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip })
        });
        const result = await response.json();
        if (result.success) {
            input.value = '';
            updateDashboard();
        } else {
            alert(`Lỗi: ${result.message}`);
        }
    } catch (e) {
        alert(`Lỗi kết nối: ${e.message}`);
    }
}

async function removeWhitelistIp(ip) {
    if (!confirm(`Bạn có chắc chắn muốn gỡ IP ${ip} khỏi danh sách tin cậy không?`)) return;
    try {
        const response = await fetchWithAuth('/api/whitelist/remove', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ ip })
        });
        const result = await response.json();
        if (result.success) {
            updateDashboard();
        } else {
            alert(`Lỗi: ${result.message}`);
        }
    } catch (e) {
        alert(`Lỗi kết nối: ${e.message}`);
    }
}

async function fetchStats() {
    try {
        const response = await fetchWithAuth('/api/stats');
        const data = await response.json();

        // 1. Cập nhật biểu đồ tròn
        attackPieChart.data.datasets[0].data = [
            data.attackTypes.sqli,
            data.attackTypes.xss,
            data.attackTypes.rateLimit
        ];
        attackPieChart.update();

        // 2. Cập nhật biểu đồ cột
        const labels = data.timeline.map(t => t.date);
        const counts = data.timeline.map(t => t.count);
        attackBarChart.data.labels = labels;
        attackBarChart.data.datasets[0].data = counts;
        attackBarChart.update();

        // 3. Cập nhật danh sách quốc gia
        const tbody = document.getElementById('country-tbody');
        if (data.countries.length === 0) {
            tbody.innerHTML = '<tr><td colspan="2" class="empty-table">Chưa ghi nhận cuộc tấn công nào</td></tr>';
        } else {
            tbody.innerHTML = '';
            data.countries.forEach(item => {
                const tr = document.createElement('tr');
                tr.innerHTML = `
                    <td style="padding: 8px 6px; font-weight: 500; color: #fff; text-align: left;">
                        <i class="fa-solid fa-location-dot" style="margin-right: 6px; color: var(--danger);"></i> ${item.country}
                    </td>
                    <td style="padding: 8px 12px; text-align: right; font-weight: 600; color: var(--warning);">
                        ${item.count} IP
                    </td>
                `;
                tbody.appendChild(tr);
            });
        }
    } catch (e) {
        console.error("Lỗi tải thông tin thống kê:", e);
    }
}
