#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-or-later
set -euo pipefail
repo_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "$repo_dir"
tarkov_data_dir="${TARKOV_DATA_DIR:-$repo_dir/userdata/tarkovstation}"
if [[ ! -f bin/Content.Server/Content.Goobstation.Server.dll ]]; then
    echo 'Сначала собери Content.Goobstation.Server в Release.' >&2
    exit 1
fi
if [[ ! -f bin/Content.Server/Content.Client.zip ]]; then
    echo 'Сначала упакуй клиент и скопируй release/SS14.Client.zip в bin/Content.Server/Content.Client.zip.' >&2
    exit 1
fi
mkdir -p "$tarkov_data_dir/logs"
tarkov_data_dir="$(cd -- "$tarkov_data_dir" && pwd)"
exec dotnet bin/Content.Server/Content.Goobstation.Server.dll \
    --config-file "${TARKOV_CONFIG_FILE:-$repo_dir/Resources/ConfigPresets/_TarkovStation/production.toml}" \
    --data-dir "$tarkov_data_dir" --cvar "log.path=$tarkov_data_dir/logs" "$@"
