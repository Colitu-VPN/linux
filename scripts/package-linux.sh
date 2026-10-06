#!/usr/bin/env bash
# Packages a published Colitu VPN for Linux build as .deb, .rpm and .tar.gz.
#
#   scripts/package-linux.sh <version> <x64|arm64> <publish-dir> <out-dir>
#
# <publish-dir> is the output of
#   dotnet publish src/ColituVPN -c Release -r linux-<arch> -p:SelfContained=true -p:Version=<version>
# The Xray and sing-box cores (with their rule files) are taken from the prebuilt
# core bundle in 2dust/v2rayN-core-bin, pinned to one commit and checked against
# its SHA-256 (the cores run as root in TUN mode). Update CORE_COMMIT and both
# hashes together. Needs: curl, unzip, rsync, dpkg-deb, rpmbuild, sha256sum.
#
# The .deb also carries the headless client (colitud + colitu, headless/): it is
# cross-compiled with Go (CGO_ENABLED=0). COLITU_HEADLESS=auto (default) includes it when
# Go (or HEADLESS_BIN_DIR, a prebuilt `stage-headless.sh build` dir) is available,
# 1 requires it, 0 leaves it out. The .rpm and .tar.gz do not include it yet.
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:?x64|arm64}"
PUBLISH="${3:?publish dir}"
OUT="${4:?out dir}"
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

# Xray v26.9.30, sing-box v1.14.2, rule files of 2026-10-04.
CORE_COMMIT="f75f07b3b2906909e6bd2961928e7debd2d75d2a"
case "$ARCH" in
  x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64;  CORE_ZIP=v2rayN-linux-64.zip
         CORE_SHA256=41734f3d7137a6eb72a8e52d2e63dacf8ef0f1b4d6c6b142c05eb8c81ef9cc04 ;;
  arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; CORE_ZIP=v2rayN-linux-arm64.zip
         CORE_SHA256=0dcb239900953954c3ac37e4c65019ef63d8af75c100348a486a5cca1eef5c10 ;;
  *) echo "unknown arch $ARCH" >&2; exit 1 ;;
esac

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
APP="$WORK/app"           # becomes /opt/colitu-vpn
mkdir -p "$APP"
rsync -a --exclude '*.pdb' "$PUBLISH/" "$APP/"

# ── Cores and rule files ────────────────────────────────────────────────
echo "[+] Cores from 2dust/v2rayN-core-bin@${CORE_COMMIT:0:12}/$CORE_ZIP"
curl -fsSL "https://raw.githubusercontent.com/2dust/v2rayN-core-bin/$CORE_COMMIT/$CORE_ZIP" -o "$WORK/core.zip"
echo "$CORE_SHA256  $WORK/core.zip" | sha256sum -c - || { echo "core bundle checksum mismatch" >&2; exit 1; }
mkdir -p "$WORK/core"
unzip -q "$WORK/core.zip" -d "$WORK/core"
CORE_BIN="$(find "$WORK/core" -maxdepth 3 -type d -name bin | head -n1)"
[[ -n "$CORE_BIN" ]] || { echo "core bundle has no bin/ folder" >&2; exit 1; }
mkdir -p "$APP/bin"
rsync -a "$CORE_BIN/" "$APP/bin/"
# Colitu runs Xray and sing-box only.
rm -rf "$APP/bin/mihomo" "$APP/bin/hysteria" "$APP/bin/naiveproxy" "$APP/bin/tuic" "$APP/bin/juicity" "$APP/bin/brook" "$APP/bin/shadowquic" 2>/dev/null || true
[[ -x "$APP/bin/xray/xray" || -f "$APP/bin/xray/xray" ]] || { echo "xray missing from the bundle" >&2; exit 1; }
[[ -f "$APP/bin/sing_box/sing-box" ]] || { echo "sing-box missing from the bundle" >&2; exit 1; }
# Russian sites direct: Xray uses the bundle's geosite.dat/geoip.dat, sing-box these rule sets
# (it would otherwise download them from GitHub, which is blocked in Russia).
mkdir -p "$APP/bin/srss"
install -m 0644 "$ROOT"/srss-dosyalari/*.srs "$APP/bin/srss/"
for f in geosite.dat geoip.dat srss/geosite-category-ru.srs srss/geoip-ru.srs; do
  [[ -s "$APP/bin/$f" ]] || { echo "bin/$f missing" >&2; exit 1; }
done

# ── Permissions ─────────────────────────────────────────────────────────
find "$APP" -type d -exec chmod 0755 {} +
find "$APP" -type f -exec chmod 0644 {} +
chmod 0755 "$APP/ColituVPN"
find "$APP/bin" -type f \( -name xray -o -name sing-box \) -exec chmod 0755 {} +
find "$APP" -maxdepth 1 -type f -name '*.so' -exec chmod 0755 {} +
[[ -f "$APP/createdump" ]] && chmod 0755 "$APP/createdump"

# ── Shared files: launcher, desktop entry, icon ─────────────────────────
STAGE="$WORK/stage"
mkdir -p "$STAGE/opt/colitu-vpn" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
         "$STAGE/usr/share/icons/hicolor/256x256/apps" "$STAGE/usr/share/pixmaps" "$STAGE/usr/share/doc/colitu-vpn"
rsync -a "$APP/" "$STAGE/opt/colitu-vpn/"

cat > "$STAGE/usr/bin/colitu-vpn" <<'EOF'
#!/bin/sh
exec /opt/colitu-vpn/ColituVPN "$@"
EOF
chmod 0755 "$STAGE/usr/bin/colitu-vpn"

cat > "$STAGE/usr/share/applications/colitu-vpn.desktop" <<'EOF'
[Desktop Entry]
Type=Application
Name=Colitu VPN
GenericName=VPN
Comment=Secure, private and fast VPN
Comment[ru]=Безопасный, приватный и быстрый VPN
Comment[tr]=Güvenli, gizli ve hızlı VPN
Exec=colitu-vpn
Icon=colitu-vpn
Terminal=false
Categories=Network;Security;
Keywords=vpn;proxy;colitu;
StartupWMClass=ColituVPN
EOF
chmod 0644 "$STAGE/usr/share/applications/colitu-vpn.desktop"

ICON="$ROOT/src/ColituVPN/Assets/Colitu/colitu-icon.png"
install -m 0644 "$ICON" "$STAGE/usr/share/icons/hicolor/256x256/apps/colitu-vpn.png"
install -m 0644 "$ICON" "$STAGE/usr/share/pixmaps/colitu-vpn.png"
install -m 0644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/colitu-vpn/copyright"

# ── .deb ────────────────────────────────────────────────────────────────
DEB="$WORK/deb"
rsync -a "$STAGE/" "$DEB/"

# Headless client: binaries, systemd unit, sysusers file.
HEADLESS_MODE="${COLITU_HEADLESS:-auto}"
HEADLESS_DEB=0
if [[ "$HEADLESS_MODE" != 0 ]]; then
  if [[ -n "${HEADLESS_BIN_DIR:-}" ]] || command -v "${GO:-go}" >/dev/null 2>&1; then
    bash "$ROOT/headless/packaging/stage-headless.sh" stage "$DEB" "$DEB_ARCH" "$VERSION"
    HEADLESS_DEB=1
  elif [[ "$HEADLESS_MODE" == 1 ]]; then
    echo "COLITU_HEADLESS=1 but Go was not found" >&2; exit 1
  else
    echo "[!] Go not found: the .deb is built without the headless client (colitud, colitu)" >&2
  fi
fi
SIZE_KB="$(du -sk "$DEB" | cut -f1)"
mkdir -p "$DEB/DEBIAN"
cat > "$DEB/DEBIAN/control" <<EOF
Package: colitu-vpn
Version: ${VERSION}
Architecture: ${DEB_ARCH}
Maintainer: Colitu <support@colitu.com>
Homepage: https://colitu.com
Section: net
Priority: optional
Installed-Size: ${SIZE_KB}
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libssl3t64 | libssl3, libx11-6, libice6, libsm6, libicu78 | libicu77 | libicu76 | libicu74 | libicu72 | libicu70 | libicu-dev, ca-certificates
Recommends: sudo, nftables, pkexec | policykit-1, libnotify-bin, xdg-utils
Description: Colitu VPN desktop client for Linux
 Sign in with your Colitu account and connect with one click. Proxy and
 TUN modes, kill switch (nftables), DNS leak protection, ad blocking and
 live support. Includes the Xray and sing-box cores.
EOF
if [[ "$HEADLESS_DEB" == 1 ]]; then
  cat >> "$DEB/DEBIAN/control" <<'EOF'
 The package also installs the headless client for servers and Raspberry Pi
 (the colitud service and the colitu command); it is not enabled automatically.
EOF
fi
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
exit 0
EOF
cp "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
# The kill switch fails closed: its nftables table outlives a crashed or killed app.
# Removing the app (not upgrading) removes the table, its marker and an old (<= 1.1.2) watcher.
cat > "$DEB/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if [ "$1" = remove ] || [ "$1" = purge ]; then
  command -v nft >/dev/null 2>&1 && nft delete table inet colitu_killswitch 2>/dev/null || true
  rm -f /run/colitu-killswitch.active
  if [ -r /run/colitu-killswitch.watch ]; then kill "$(cat /run/colitu-killswitch.watch)" 2>/dev/null || true; rm -f /run/colitu-killswitch.watch; fi
fi
exit 0
EOF
if [[ "$HEADLESS_DEB" == 1 ]]; then
  # Insert the headless fragments (group, daemon-reload, stop on removal) before each
  # script's closing "exit 0". The service is never enabled or started automatically.
  for f in postinst postrm prerm; do
    [[ "$(tail -n1 "$DEB/DEBIAN/$f")" == "exit 0" ]] || { echo "$f does not end with exit 0" >&2; exit 1; }
    sed -i '$d' "$DEB/DEBIAN/$f"
    cat "$ROOT/headless/packaging/maintainer-$f.sh" >> "$DEB/DEBIAN/$f"
    echo "exit 0" >> "$DEB/DEBIAN/$f"
  done
fi
chmod 0755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm" "$DEB/DEBIAN/prerm"
DEB_OUT="$OUT/colitu-vpn_${VERSION}_${DEB_ARCH}.deb"
dpkg-deb --root-owner-group -Zxz --build "$DEB" "$DEB_OUT"
echo "[+] $DEB_OUT"

# ── .rpm ────────────────────────────────────────────────────────────────
RPMTOP="$WORK/rpmbuild"
mkdir -p "$RPMTOP"/{BUILD,RPMS,SOURCES,SPECS,SRPMS,BUILDROOT}
cat > "$RPMTOP/SPECS/colitu-vpn.spec" <<EOF
Name:           colitu-vpn
Version:        ${VERSION}
Release:        1
Summary:        Colitu VPN desktop client for Linux
License:        GPL-3.0-only
URL:            https://colitu.com
BuildArch:      ${RPM_ARCH}
AutoReqProv:    no
Requires:       glibc, libgcc, libstdc++, zlib, fontconfig, libicu, ca-certificates
Recommends:     sudo, nftables, polkit, libnotify, xdg-utils

%global debug_package %{nil}
%global __strip /bin/true
%global __brp_strip %{nil}
%global __brp_check_rpaths %{nil}
%global _build_id_links none

%description
Sign in with your Colitu account and connect with one click. Proxy and
TUN modes, kill switch (nftables), DNS leak protection, ad blocking and
live support. Includes the Xray and sing-box cores.

%install
mkdir -p %{buildroot}
cp -a ${STAGE}/. %{buildroot}/

%post
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || :
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || :

%preun
if [ \$1 -eq 0 ]; then
  command -v nft >/dev/null 2>&1 && nft delete table inet colitu_killswitch 2>/dev/null || :
  rm -f /run/colitu-killswitch.active
  if [ -r /run/colitu-killswitch.watch ]; then kill "\$(cat /run/colitu-killswitch.watch)" 2>/dev/null || :; rm -f /run/colitu-killswitch.watch; fi
fi

%postun
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || :

%files
/opt/colitu-vpn
/usr/bin/colitu-vpn
/usr/share/applications/colitu-vpn.desktop
/usr/share/icons/hicolor/256x256/apps/colitu-vpn.png
/usr/share/pixmaps/colitu-vpn.png
/usr/share/doc/colitu-vpn
EOF
rpmbuild -bb --define "_topdir $RPMTOP" --target "$RPM_ARCH" "$RPMTOP/SPECS/colitu-vpn.spec" >/dev/null
RPM_BUILT="$(find "$RPMTOP/RPMS" -name '*.rpm' | head -n1)"
RPM_OUT="$OUT/colitu-vpn-${VERSION}-1.${RPM_ARCH}.rpm"
cp "$RPM_BUILT" "$RPM_OUT"
echo "[+] $RPM_OUT"

# ── .tar.gz (any distribution) ──────────────────────────────────────────
TARDIR="$WORK/colitu-vpn-${VERSION}-linux-${ARCH}"
mkdir -p "$TARDIR"
rsync -a "$APP/" "$TARDIR/"
install -m 0644 "$STAGE/usr/share/applications/colitu-vpn.desktop" "$TARDIR/colitu-vpn.desktop"
install -m 0644 "$ICON" "$TARDIR/colitu-vpn.png"
TAR_OUT="$OUT/colitu-vpn-${VERSION}-linux-${ARCH}.tar.gz"
tar -C "$WORK" --owner=0 --group=0 -czf "$TAR_OUT" "$(basename "$TARDIR")"
echo "[+] $TAR_OUT"
