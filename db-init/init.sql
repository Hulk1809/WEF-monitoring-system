-- Khởi tạo bảng system_users
CREATE TABLE IF NOT EXISTS system_users (
    id SERIAL PRIMARY KEY,
    username VARCHAR(50) UNIQUE NOT NULL,
    role VARCHAR(20) NOT NULL,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);

-- Chèn dữ liệu mẫu
INSERT INTO system_users (username, role) 
VALUES 
    ('admin_secure', 'Administrator'), 
    ('monitor_agent', 'Auditor'), 
    ('business_user', 'User')
ON CONFLICT (username) DO NOTHING;
