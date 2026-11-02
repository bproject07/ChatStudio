#!/usr/bin/env python3
"""Remove the bootstrap administrator bearer token after the collector is provisioned."""
import argparse
import pathlib

parser = argparse.ArgumentParser()
parser.add_argument('--directory', required=True)
args = parser.parse_args()
directory = pathlib.Path(args.directory).resolve() / 'secrets'
if (directory / 'collector-key-role').read_text().strip() != 'MEMBER' or not (directory / 'collector-key').is_file():
    raise SystemExit('Provision the member collector before removing bootstrap access.')
environment = directory / 'phoenix.env'
lines = [line for line in environment.read_text().splitlines() if not line.startswith('PHOENIX_ADMIN_SECRET=')]
temporary = directory / 'phoenix.env.new'
temporary.touch(mode=0o600, exist_ok=False)
temporary.write_text('\n'.join(lines) + '\n')
temporary.replace(environment)
(directory / 'bootstrap-token').unlink(missing_ok=True)
print('Bootstrap credential removed. Recreate the Phoenix container to revoke the running token.')
