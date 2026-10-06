# Headless client (colitud): sourced into the package's postinst.
# Creates the "colitu" group and tells systemd about the unit. The service is
# never enabled or started here: that is the user's choice.
if command -v systemd-sysusers >/dev/null 2>&1; then
  systemd-sysusers colitu.conf >/dev/null 2>&1 || true
fi
getent group colitu >/dev/null 2>&1 || groupadd --system colitu >/dev/null 2>&1 || true
if [ -d /run/systemd/system ]; then
  systemctl daemon-reload >/dev/null 2>&1 || true
  # An upgrade restarts a running daemon (it reconnects when autoconnect is on).
  if [ -n "${2:-}" ]; then
    systemctl try-restart colitud.service >/dev/null 2>&1 || true
  fi
fi
if [ -z "${2:-}" ]; then
  echo "Colitu headless client installed. To use it:"
  echo "  sudo systemctl enable --now colitud"
  echo "  sudo usermod -aG colitu \$USER     (then log out and in again)"
  echo "  colitu login"
fi
