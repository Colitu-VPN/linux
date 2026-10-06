#!/usr/bin/env bash
# Builds the headless client (colitud + colitu) and stages it for a package.
#
#   stage-headless.sh build <out-dir> [amd64|arm64 ...]
#       cross-compiles (CGO_ENABLED=0) into <out-dir>/linux-<arch>/{colitud,colitu}
#   stage-headless.sh stage <stage-root> <amd64|arm64> <version>
#       installs the binaries, the systemd unit and the sysusers file into the
#       package root (builds first unless HEADLESS_BIN_DIR points at a build dir)
#
# Needs Go 1.22+ (GO=/path/to/go to override) unless HEADLESS_BIN_DIR is set.
set -euo pipefail

HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
MODULE_DIR="$(cd "$HERE/.." && pwd)"
GO="${GO:-go}"
PKG=github.com/colitu/colitu-linux/headless

norm_arch() {
  case "$1" in
    amd64|x64|x86_64)   echo amd64 ;;
    arm64|aarch64)      echo arm64 ;;
    *) echo "unsupported architecture '$1' (amd64|arm64)" >&2; return 1 ;;
  esac
}

build_one() {
  local out="$1" arch version="${3:-0.0.0-dev}"
  arch="$(norm_arch "$2")"
  mkdir -p "$out/linux-$arch"
  ( cd "$MODULE_DIR" && \
    CGO_ENABLED=0 GOOS=linux GOARCH="$arch" "$GO" build -trimpath \
      -ldflags "-s -w -X $PKG/internal/version.Version=$version" \
      -o "$out/linux-$arch/" ./cmd/colitud ./cmd/colitu )
  echo "[+] headless $arch -> $out/linux-$arch"
}

cmd="${1:-}"
case "$cmd" in
  build)
    out="${2:?out dir}"; shift 2
    [[ $# -gt 0 ]] || set -- amd64 arm64
    for a in "$@"; do build_one "$out" "$a" "${HEADLESS_VERSION:-0.0.0-dev}"; done
    ;;
  stage)
    root="${2:?stage root}"; arch="$(norm_arch "${3:?arch}")"; version="${4:?version}"
    bindir="${HEADLESS_BIN_DIR:-}"
    if [[ -z "$bindir" ]]; then
      tmp="$(mktemp -d)"; trap 'rm -rf "$tmp"' EXIT
      build_one "$tmp" "$arch" "$version"
      bindir="$tmp/linux-$arch"
    fi
    [[ -f "$bindir/colitud" && -f "$bindir/colitu" ]] || { echo "colitud/colitu missing in $bindir" >&2; exit 1; }
    install -d -m 0755 "$root/usr/bin" "$root/usr/lib/systemd/system" "$root/usr/lib/sysusers.d"
    install -m 0755 "$bindir/colitud" "$root/usr/bin/colitud"
    install -m 0755 "$bindir/colitu" "$root/usr/bin/colitu"
    install -m 0644 "$HERE/colitud.service" "$root/usr/lib/systemd/system/colitud.service"
    install -m 0644 "$HERE/colitu.sysusers" "$root/usr/lib/sysusers.d/colitu.conf"
    echo "[+] headless client ($arch, $version) staged in $root"
    ;;
  *)
    sed -n '2,11p' "${BASH_SOURCE[0]}" >&2
    exit 2
    ;;
esac
