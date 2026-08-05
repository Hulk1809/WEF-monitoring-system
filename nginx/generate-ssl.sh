#!/bin/sh
mkdir -p ssl
openssl req -x509 -nodes -days 365 -newkey rsa:2048 \
  -keyout ssl/nginx.key \
  -out ssl/nginx.crt \
  -subj "/C=VN/ST=HCM/L=HCM/O=VHU/CN=3.1.210.184"
echo "✅ SSL Certificate and Key generated successfully in nginx/ssl/ directory."
