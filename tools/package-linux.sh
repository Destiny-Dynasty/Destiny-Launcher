#!/usr/bin/env bash
set -euo pipefail
umask 022

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
rid="${1:-linux-x64}"
version="${2:-1.2.0}"
case "$rid" in
  linux-x64) deb_arch="amd64" ;;
  linux-arm64) deb_arch="arm64" ;;
  *) echo "Uso: $0 [linux-x64|linux-arm64] [version]" >&2; exit 2 ;;
esac
if [[ ! "$version" =~ ^[0-9]+(\.[0-9]+){1,3}([~-][A-Za-z0-9.+~]+)?$ ]]; then
  echo "Versión Debian inválida: $version" >&2
  exit 2
fi
if [[ -z "${DESTINY_PUBLISH_DIR:-}" ]]; then
  command -v dotnet >/dev/null || { echo "Falta .NET SDK 10." >&2; exit 1; }
fi
command -v dpkg-deb >/dev/null || { echo "Falta dpkg-deb (paquete dpkg-dev)." >&2; exit 1; }

stage="$(mktemp -d)"
trap 'rm -rf "$stage"' EXIT
payload="$stage/pkg"
mkdir -p "$payload/opt/destiny-launcher" \
  "$payload/usr/bin" \
  "$payload/usr/share/applications" \
  "$payload/usr/share/pixmaps" \
  "$payload/usr/share/metainfo" \
  "$payload/usr/share/doc/destiny-launcher" \
  "$payload/DEBIAN"

if [[ -n "${DESTINY_PUBLISH_DIR:-}" ]]; then
  [[ -f "$DESTINY_PUBLISH_DIR/DestinyLauncher.Linux" ]] || { echo "Falta la publicación Linux." >&2; exit 1; }
  cp -a "$DESTINY_PUBLISH_DIR/." "$payload/opt/destiny-launcher/"
  find "$payload/opt/destiny-launcher" -type d -exec chmod 0755 {} +
  find "$payload/opt/destiny-launcher" -type f -exec chmod 0644 {} +
  chmod 0755 "$payload/opt/destiny-launcher/DestinyLauncher.Linux"
  [[ ! -f "$payload/opt/destiny-launcher/createdump" ]] || chmod 0755 "$payload/opt/destiny-launcher/createdump"
else
  dotnet publish "$repo_root/DestinyLauncher.Linux/DestinyLauncher.Linux.csproj" \
    --configuration Release --runtime "$rid" --self-contained true \
    --output "$stage/publish" -p:Version="$version"
  cp -a "$stage/publish/." "$payload/opt/destiny-launcher/"
fi
install -m 0755 /dev/stdin "$payload/usr/bin/destiny-launcher" <<'WRAPPER'
#!/bin/sh
exec /opt/destiny-launcher/DestinyLauncher.Linux "$@"
WRAPPER
install -m 0644 "$repo_root/DestinyLauncher.Linux/packaging/destiny-launcher.desktop" \
  "$payload/usr/share/applications/destiny-launcher.desktop"
install -m 0644 "$repo_root/DestinyLauncher.Linux/packaging/com.destinydynasty.Launcher.metainfo.xml" \
  "$payload/usr/share/metainfo/com.destinydynasty.Launcher.metainfo.xml"
install -m 0644 "$repo_root/DestinyLauncher/Assets/experiment/obsidian-gold-v1/destiny-emblem.png" \
  "$payload/usr/share/pixmaps/destiny-launcher.png"
install -m 0644 "$repo_root/LICENSE" "$payload/usr/share/doc/destiny-launcher/copyright"
install -m 0644 "$repo_root/THIRD_PARTY_NOTICES.md" "$payload/usr/share/doc/destiny-launcher/third-party-notices.md"

cat > "$payload/DEBIAN/control" <<CONTROL
Package: destiny-launcher
Version: $version
Section: games
Priority: optional
Architecture: $deb_arch
Maintainer: Destiny Dynasty contributors
Homepage: https://github.com/Destiny-Dynasty/Destiny-Launcher
Depends: libx11-6, libice6, libsm6, libfontconfig1, fonts-liberation, xdg-utils, libsecret-tools, gnome-keyring, libvlc5, libvlccore9, vlc-plugin-base, vlc-plugin-video-output, pkexec, dpkg
Description: Destiny Launcher para Minecraft
 Instala y actualiza los modpacks de Destiny Dynasty y prepara Minecraft Java
 para jugar desde un launcher de escritorio.
CONTROL

output_dir="$repo_root/artifacts/linux"
mkdir -p "$output_dir"
dpkg-deb --build --root-owner-group "$payload" "$output_dir/destiny-launcher_${version}_${deb_arch}.deb"
echo "Paquete creado en $output_dir/destiny-launcher_${version}_${deb_arch}.deb"
