#!/usr/bin/env python3
"""Provision a MEMBER collector account; revoke the temporary system collector key. Never sends email."""
import argparse
import http.cookies
import json
import os
import pathlib
import secrets
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--directory', required=True)
args = parser.parse_args()
directory = pathlib.Path(args.directory).resolve() / 'secrets'
os.umask(0o077)
base = 'http://127.0.0.1:6006'

def call(path, headers, payload=None, method=None):
    body = None if payload is None else json.dumps(payload).encode()
    request = urllib.request.Request(base + path, headers={**headers, 'Content-Type': 'application/json'},
                                     data=body, method=method)
    return urllib.request.urlopen(request, timeout=15)

if (directory / 'collector-key-role').exists():
    print('Member collector already provisioned.')
    raise SystemExit(0)
bootstrap = (directory / 'bootstrap-token').read_text().strip()
admin_headers = {'Authorization': 'Bearer ' + bootstrap}
email = 'chat-telemetry@monitoring.invalid'
password_file = directory / 'collector-password'
if not password_file.exists():
    password_file.write_text(secrets.token_urlsafe(32) + '!Aa7\n')
users = json.load(call('/v1/users', admin_headers))['data']
if not any(user.get('email') == email for user in users):
    call('/v1/users', admin_headers, {'user': {'email': email, 'username': 'Chat telemetry',
         'role': 'MEMBER', 'auth_method': 'LOCAL', 'password': password_file.read_text().strip()},
         'send_welcome_email': False}).close()
login = call('/auth/login', {}, {'email': email, 'password': password_file.read_text().strip()})
cookie = http.cookies.SimpleCookie()
for value in login.headers.get_all('Set-Cookie', []):
    cookie.load(value)
login.close()
member_headers = {'Cookie': 'phoenix-access-token=' + cookie['phoenix-access-token'].value}
created = json.load(call('/v1/user/api_keys', member_headers,
                        {'data': {'name': 'chat-studio-telemetry', 'description': 'Local OTLP ingestion'}}))['data']
path = directory / 'collector-key'
temporary = directory / 'collector-key.new'
temporary.write_text(created['key'] + '\n')
temporary.replace(path)
path.chmod(0o600)
for key in json.load(call('/v1/system/api_keys', admin_headers))['data']:
    if key['name'] == 'chat-studio-collector':
        call('/v1/system/api_keys/' + key['id'], admin_headers, method='DELETE').close()
(directory / 'collector-key-role').write_text('MEMBER\n')
print('Member collector key created; temporary system collector key revoked.')
