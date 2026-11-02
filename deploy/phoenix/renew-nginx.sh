#!/bin/sh
# Install as a Certbot deploy hook; reload only for the Phoenix certificate.
set -eu
case " ${RENEWED_DOMAINS:-} " in
  *" phoenix.example.org "*) /usr/sbin/nginx -t && /usr/bin/systemctl reload nginx ;;
esac
