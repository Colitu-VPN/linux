# Headless client (colitud): sourced into the package's prerm, which gets
# "remove" (or "purge") on removal and "upgrade" on upgrade. On removal the
# daemon is stopped first, so sing-box takes its routes down cleanly.
if [ "$1" = remove ] || [ "$1" = purge ]; then
  if [ -d /run/systemd/system ]; then
    systemctl stop colitud.service >/dev/null 2>&1 || true
    systemctl disable colitud.service >/dev/null 2>&1 || true
  fi
fi
