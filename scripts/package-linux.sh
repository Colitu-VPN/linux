#!/usr/bin/env bash
# Packages a published Colitu VPN for Linux build as .deb, .rpm and .tar.gz.
#
#   scripts/package-linux.sh <version> <x64|arm64> <publish-dir> <out-dir>
#
# <publish-dir> is the output of
#   dotnet publish v2rayN/v2rayN.Desktop -c Release -r linux-<arch> -p:SelfContained=true -p:Version=<version>
# The Xray and sing-box cores (with their rule files) are taken from 2dust/v2rayN-core-bin,
# the same bundle the v2rayN Linux releases ship. Needs: curl, unzip, rsync, dpkg-deb, rpmbuild.
set -euo pipefail

VERSION="${1:?version}"
ARCH="${2:?x64|arm64}"
PUBLISH="${3:?publish dir}"
OUT="${4:?out dir}"
ROOT="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"

case "$ARCH" in
  x64)   DEB_ARCH=amd64; RPM_ARCH=x86_64;  CORE_ZIP=v2rayN-linux-64.zip ;;
  arm64) DEB_ARCH=arm64; RPM_ARCH=aarch64; CORE_ZIP=v2rayN-linux-arm64.zip ;;
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
echo "[+] Cores from 2dust/v2rayN-core-bin/$CORE_ZIP"
curl -fsSL "https://raw.githubusercontent.com/2dust/v2rayN-core-bin/refs/heads/master/$CORE_ZIP" -o "$WORK/core.zip"
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

ICON="$ROOT/v2rayN/v2rayN.Desktop/Assets/Colitu/colitu-icon.png"
install -m 0644 "$ICON" "$STAGE/usr/share/icons/hicolor/256x256/apps/colitu-vpn.png"
install -m 0644 "$ICON" "$STAGE/usr/share/pixmaps/colitu-vpn.png"
install -m 0644 "$ROOT/LICENSE" "$STAGE/usr/share/doc/colitu-vpn/copyright"

SIZE_KB="$(du -sk "$STAGE" | cut -f1)"

# ── .deb ────────────────────────────────────────────────────────────────
DEB="$WORK/deb"
rsync -a "$STAGE/" "$DEB/"
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
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libfontconfig1, libicu-dev | libicu74 | libicu72 | libicu76, ca-certificates
Recommends: sudo, nftables, pkexec | policykit-1, libnotify-bin, xdg-utils
Description: Colitu VPN desktop client for Linux
 Sign in with your Colitu account and connect with one click. Proxy and
 TUN modes, kill switch (nftables), DNS leak protection, ad blocking and
 live support. Based on v2rayN (GPL-3.0) with the Xray and sing-box cores.
EOF
cat > "$DEB/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || true
exit 0
EOF
cp "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
chmod 0755 "$DEB/DEBIAN/postinst" "$DEB/DEBIAN/postrm"
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
live support. Based on v2rayN (GPL-3.0) with the Xray and sing-box cores.

%install
mkdir -p %{buildroot}
cp -a ${STAGE}/. %{buildroot}/

%post
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || :
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q -f /usr/share/icons/hicolor || :

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
