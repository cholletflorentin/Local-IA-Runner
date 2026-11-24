#!/usr/bin/env sh
set -eu
script_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
server=$(find "$script_dir/llama-bin" -type f -name llama-server -print -quit)
if [ -z "$server" ]; then
    echo "llama-server est introuvable dans le dossier llama-bin." >&2
    exit 1
fi
chmod +x "$server"
exec "$server" "$@"
