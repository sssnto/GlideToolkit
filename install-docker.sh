#!/bin/sh
set -eu
container=${1:?请提供 Jellyfin 容器名称}
case "$container" in *[!a-zA-Z0-9_.-]*|'') echo '容器名称格式不正确' >&2; exit 1;; esac
package_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$package_dir"
if command -v sha256sum >/dev/null 2>&1; then sha256sum -c SHA256SUMS
elif command -v shasum >/dev/null 2>&1; then shasum -a 256 -c SHA256SUMS
else echo '需要 sha256sum 或 shasum 校验安装包' >&2; exit 1; fi
running=$(docker inspect --format '{{.State.Running}}' "$container")
if [ "$running" != false ]; then echo '请先停止 Jellyfin 容器，再安装；不要在旋转任务运行时更新 DLL。' >&2; exit 1; fi
# Retain the original installation directory to avoid loading two plugin copies.
# Custom installation paths should use the manual upgrade instructions.
temporary=$(mktemp -d)
trap 'rm -rf "$temporary"' EXIT HUP INT TERM
mkdir "$temporary/GlideToolkit_1.0.0.0"
cp Jellyfin.Plugin.GlideToolkit.dll meta.json "$temporary/GlideToolkit_1.0.0.0/"
chmod 755 "$temporary/GlideToolkit_1.0.0.0"
chmod 644 "$temporary/GlideToolkit_1.0.0.0/"*
docker cp "$temporary/GlideToolkit_1.0.0.0" "$container:/config/plugins/"
echo '插件已更新至 1.1.1，尚未启动。确认无误后执行：'
printf 'docker start %s\n' "$container"
echo '重启后在 Jellyfin 网页管理后台的“我的插件”中确认 Glide Video Toolkit。'
