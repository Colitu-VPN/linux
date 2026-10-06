# Headless client (colitud): sourced into the package's postrm.
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload >/dev/null 2>&1 || true
fi
# Purging also deletes the saved sign-in (tokens). The "colitu" group stays:
# other software might use it and group ids are not worth recycling.
if [ "$1" = purge ]; then
  rm -rf /var/lib/colitu
fi
