# Colitu VPN for Linux

The open-source Linux desktop client of [Colitu VPN](https://colitu.com). It
keeps the core management, system proxy, TUN, routing and DNS layers of
[v2rayN](https://github.com/2dust/v2rayN) (the Avalonia build, the same project
the [Windows client](https://github.com/cyberlexs/colitu-windows) is built on)
and puts a single Colitu window on top that handles the account, servers and
connection through the Colitu API.

| | |
|---|---|
| App | `Colitu VPN` (`/opt/colitu-vpn/ColituVPN`, launcher `colitu-vpn`), Avalonia on .NET 10 |
| Version | `1.0.0` (`v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj`) |
| Base | [v2rayN](https://github.com/2dust/v2rayN) `7.25.4` |
| OS | Debian/Ubuntu/Mint (`.deb`), Fedora/RHEL/openSUSE (`.rpm`), any distribution (`.tar.gz`); x64 and arm64 |
| Languages | Russian, English, Turkish |
| License | [GPL-3.0](LICENSE) |

<p align="center">
  <img src="docs/screenshots/home.png" alt="Colitu VPN for Linux: home" width="49%">
  <img src="docs/screenshots/locations.png" alt="Locations with live pings" width="49%">
  <img src="docs/screenshots/sign-in.png" alt="Sign in" width="49%">
  <img src="docs/screenshots/support.png" alt="Live support" width="49%">
</p>

**Download:** [colitu.com/download/linux](https://colitu.com/download/linux) · [Releases](../../releases)

> A Colitu account is required to connect. The app has no hard-coded servers:
> the server list and connection profiles are issued per device by the Colitu API.

## Features

- **Automatic protocol selection.** Hysteria2 runs on the bundled sing-box core;
  VLESS Reality, VLESS XHTTP, Trojan and Shadowsocks on Xray. Candidates are
  tried in order until traffic actually flows, and the result is reported back
  to the panel.
- **Proxy and TUN modes.** Proxy mode sets the desktop's system proxy (GNOME,
  KDE) and needs no extra rights. TUN mode sends all traffic through the tunnel;
  like v2rayN it runs the core as root through `sudo`, so the app asks for the
  sudo password once per run and keeps it in memory only.
- **Kill switch** on nftables (TUN mode). While it is on, only loopback, the
  tunnel, the local network, DHCP, the VPN servers and the Colitu API can be
  reached. A root watcher removes the rules as soon as the app exits, so a crash
  never leaves the computer offline.
- **Ad blocking** (optional): DNS through Colitu's ad-blocking servers, the same as on the phones and Windows.
- **One window for everything:** sign-in and registration, e-mail
  verification, password reset, locations with live pings, plan, account and
  devices, and live support with attachments.
- **Private local data.** Sessions and cached connection settings are
  AES-GCM encrypted with a key bound to the machine id and the user, and the
  data folder (`~/.local/share/ColituVPN`) is readable by its owner only. Core
  logs never record the sites a user visits.
- **Verified updates.** `downloads/linux/latest.json` is signed offline with the
  Colitu release key (ECDSA P-256); the matching `.deb`/`.rpm` is checked
  against the signed SHA-256 and installed with `pkexec`.

## Layout

| Path | Contents |
|---|---|
| `v2rayN/v2rayN.Desktop/Views/ColituMainWindow*` | the Colitu window (sign-in, home, locations, plan, account, settings, support) |
| `v2rayN/v2rayN.Desktop/Services/Colitu*` | API, session, VPN, kill switch, updates, support, localization |
| `v2rayN/v2rayN.Desktop/Assets/Colitu` | theme, fonts, logo and flags |
| `v2rayN/ServiceLib` | v2rayN's core, config and proxy logic (a few small Colitu patches) |
| `v2rayN/ColituVPN.Tests`, `v2rayN/ServiceLib.Tests` | tests |
| `package-debian.sh`, `package-rhel.sh` | packages |
| `scripts/sign-linux-manifest.ps1` | signs the update manifest |

The v2rayN Windows (WPF) app, the macOS packaging and the Windows global-hotkey
library of upstream v2rayN are not part of this repository.

## Build

Requires the .NET 10 SDK.

```sh
cd v2rayN
dotnet publish v2rayN.Desktop/v2rayN.Desktop.csproj -c Release -r linux-x64 -p:SelfContained=true -o ../out/linux-x64
dotnet test --project ColituVPN.Tests
```

The ad-blocking DNS servers are Colitu's own nodes and are not in the source;
release builds pass them with `-p:ColituAdBlockDoh=https://...` (the package
scripts read `COLITU_ADBLOCK_DOH`). Without them the ad-block switch is hidden.

## Release

1. Set the version in `v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj`.
2. Push a tag `vX.Y.Z`. The `Release Linux` workflow tests, publishes x64 and
   arm64, packages `.deb`, `.rpm` and `.tar.gz` (`scripts/package-linux.sh`,
   cores from 2dust/v2rayN-core-bin) and attaches them with `SHA256SUMS` to
   the GitHub release. The ad-block DNS list comes from the `COLITU_ADBLOCK_DOH`
   repository secret.
3. Sign the update manifest offline (PowerShell 7, release key outside the repository):
   `pwsh scripts/sign-linux-manifest.ps1 -Version X.Y.Z -DebPath <amd64.deb> -RpmPath <x86_64.rpm>`
4. Upload the packages and `latest.json` from `release-linux/` to the
   website's `downloads/linux/` folder.

`package-debian.sh` and `package-rhel.sh` build the same packages on a Debian or
RHEL-family machine without CI.

## Install

Debian, Ubuntu, Linux Mint, Pop!_OS:

```sh
sudo apt install ./colitu-vpn_X.Y.Z_amd64.deb
```

Fedora, RHEL, Rocky, AlmaLinux, openSUSE:

```sh
sudo dnf install ./colitu-vpn-X.Y.Z-1.x86_64.rpm
```

Any other distribution: unpack `colitu-vpn-X.Y.Z-linux-x64.tar.gz` and run
`./ColituVPN`. TUN mode and the kill switch need `sudo` and `nftables`.

## Upstream

The first commit is v2rayN `7.25.4` with the Colitu changes on top. To take a
newer v2rayN, apply its diff since 7.25.4 (`git diff 7.25.4 <new tag>` in a
v2rayN checkout) to `v2rayN/ServiceLib` and `v2rayN/v2rayN.Desktop` and run the tests.

v2rayN is © 2017 2dust and contributors, licensed under GPL-3.0. Xray-core
(MPL-2.0) and sing-box (GPL-3.0) are bundled as separate programs.
