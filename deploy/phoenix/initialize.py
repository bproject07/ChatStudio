#!/usr/bin/env python3
"""Create local Phoenix credentials once. Run on the target host; never prints secrets."""
import argparse
import os
import pathlib
import secrets
from urllib.parse import urlparse

parser = argparse.ArgumentParser()
parser.add_argument("--directory", required=True)
parser.add_argument("--public-url", required=True)
args = parser.parse_args()
url = urlparse(args.public_url)
if url.scheme != "https" or not url.hostname or url.path not in ("", "/") or url.username or url.query or url.fragment:
    parser.error("public-url must be an HTTPS origin")
root = pathlib.Path(args.directory).resolve()
directory = root / "secrets"
directory.mkdir(parents=True, exist_ok=True, mode=0o700)
os.chmod(directory, 0o700)
if any(directory.iterdir()):
    raise SystemExit("Secrets already exist. Refusing to rotate credentials or overwrite an existing installation.")
password = secrets.token_hex(24)
login = secrets.token_urlsafe(28) + "!Aa7"
admin = secrets.token_hex(32)
signing = secrets.token_hex(32)
files = {
    "db-password": password + "\n",
    "admin-password": login + "\n",
    "bootstrap-token": admin + "\n",
    "phoenix.env": (
        f"PHOENIX_SQL_DATABASE_URL=postgresql://phoenix:{password}@db:5432/phoenix\n"
        f"PHOENIX_SECRET={signing}\n"
        f"PHOENIX_ADMIN_SECRET={admin}\n"
        f"PHOENIX_DEFAULT_ADMIN_INITIAL_PASSWORD={login}\n"
        f"PHOENIX_ROOT_URL={args.public_url.rstrip('/')}\n"
        f"PHOENIX_CSRF_TRUSTED_ORIGINS={args.public_url.rstrip('/')}\n"
    ),
}
for name, value in files.items():
    fd = os.open(directory / name, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(fd, "w") as stream:
        stream.write(value)
print("Created private credential files. Phoenix login: admin@localhost (password in secrets/admin-password).")
