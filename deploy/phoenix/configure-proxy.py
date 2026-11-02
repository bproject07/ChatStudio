#!/usr/bin/env python3
"""Configure one Nginx host, restricted to Cloudflare peers. Requires an existing TLS certificate."""
import argparse
import ipaddress
import json
import pathlib
import re
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("--hostname", required=True)
args = parser.parse_args()
hostname = args.hostname
if not re.fullmatch(r"[a-z0-9](?:[a-z0-9.-]{0,250}[a-z0-9])?", hostname):
    parser.error("invalid hostname")
certificate = pathlib.Path('/etc/letsencrypt/live') / hostname
if not (certificate / 'fullchain.pem').is_file():
    raise SystemExit('Issue the DNS-01 certificate before configuring the proxy.')
with urllib.request.urlopen('https://api.cloudflare.com/client/v4/ips', timeout=20) as response:
    ip_data = json.load(response)
if not ip_data.get('success'):
    raise SystemExit('Cloudflare IP API failed; refusing to change access rules.')
networks = [str(ipaddress.ip_network(line)) for line in
            ip_data['result']['ipv4_cidrs'] + ip_data['result']['ipv6_cidrs']]
if len(networks) < 10:
    raise SystemExit('Incomplete Cloudflare IP ranges; refusing to change access rules.')
conf = pathlib.Path('/etc/nginx')
(conf / 'conf.d/phoenix-security.conf').write_text(
    '# Managed by deploy/phoenix/configure-proxy.py\n'
    'geo $realip_remote_addr $phoenix_cloudflare_peer {\n    default 0;\n'
    + ''.join(f'    {network} 1;\n' for network in networks)
    + '}\nlimit_req_zone $binary_remote_addr zone=phoenix_auth:10m rate=6r/m;\n'
)
(conf / 'snippets/phoenix-upstream.conf').write_text('''
proxy_pass http://127.0.0.1:6006;
proxy_http_version 1.1;
proxy_set_header Host $host;
proxy_set_header X-Forwarded-Proto https;
proxy_set_header X-Forwarded-For $remote_addr;
proxy_set_header X-Real-IP $remote_addr;
proxy_read_timeout 120s;
proxy_cache off;
proxy_hide_header Cache-Control;
add_header Cache-Control "no-store" always;
add_header X-Content-Type-Options nosniff always;
add_header Referrer-Policy same-origin always;
add_header Strict-Transport-Security "max-age=31536000" always;
'''.lstrip())
trust = ''.join(f'    set_real_ip_from {network};\n' for network in networks)
site = conf / 'sites-available/phoenix.conf'
site.write_text(f'''
# Managed by deploy/phoenix/configure-proxy.py
server {{
    listen 80;
    listen [::]:80;
    server_name {hostname};
    return 301 https://{hostname}$request_uri;
}}
server {{
    listen 443 ssl;
    listen [::]:443 ssl;
    server_name {hostname};
    ssl_certificate {certificate}/fullchain.pem;
    ssl_certificate_key {certificate}/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
{trust}
    real_ip_header CF-Connecting-IP;
    real_ip_recursive on;
    # Original TCP peer, not a spoofable header or the restored visitor address.
    if ($phoenix_cloudflare_peer = 0) {{ return 403; }}
    client_max_body_size 20m;
    location = /v1/traces {{ return 404; }}
    location = /v1/traces/ {{ return 404; }}
    location = /metrics {{ return 404; }}
    location = /openapi.json {{ return 404; }}
    location /auth/ {{
        limit_req zone=phoenix_auth burst=5 nodelay;
        limit_req_status 429;
        include /etc/nginx/snippets/phoenix-upstream.conf;
    }}
    location / {{ include /etc/nginx/snippets/phoenix-upstream.conf; }}
}}
'''.lstrip())
link = conf / 'sites-enabled/phoenix.conf'
if not link.exists():
    link.symlink_to(site)
print('Phoenix proxy prepared. Run nginx -t before reloading.')
