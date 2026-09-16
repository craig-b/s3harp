#!/usr/bin/env bash
# Runs Ceph's s3-tests against a built S3Harp and gates on must-pass.txt.
#
#   dotnet build src/S3Harp.Server --configuration Release
#   tests/conformance/run.sh [extra pytest arguments]
#
# The s3-tests checkout and Python environment live in .conformance/ at the
# repository root and are reused between runs.
set -euo pipefail

here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
work="$repo/.conformance"
server=${S3HARP_BIN:-$repo/src/S3Harp.Server/bin/Release/net10.0/s3harp}
s3tests_repo=https://github.com/ceph/s3-tests.git
s3tests_rev=5522d1c351f75bc00ae0f64f742f3f095f5939d9
port=18080

checkout="$work/s3-tests"
if [ "$(git -C "$checkout" rev-parse HEAD 2>/dev/null || true)" != "$s3tests_rev" ]; then
    rm -rf "$checkout"
    git init -q "$checkout"
    git -C "$checkout" fetch -q --depth 1 "$s3tests_repo" "$s3tests_rev"
    git -C "$checkout" checkout -q FETCH_HEAD
fi

venv="$work/venv"
if [ ! -x "$venv/bin/pytest" ]; then
    uv venv -q --python 3.12 "$venv"
    uv pip install -q --python "$venv/bin/python" -r "$checkout/requirements.txt" pytest-timeout
fi

data="$work/data"
rm -rf "$data"
mkdir -p "$data"
S3HARP_ACCESS_KEY_ID=S3HARPCONFORMANCEKEY \
S3HARP_SECRET_ACCESS_KEY=s3harpconformancesecrets3harpconformance \
S3HARP_DATA_DIR="$data" \
    "$server" --bind 127.0.0.1 --port "$port" >"$work/server.log" 2>&1 &
server_pid=$!
trap 'kill "$server_pid" 2>/dev/null || true' EXIT

for _ in $(seq 1 100); do
    if curl -s -o /dev/null "http://127.0.0.1:$port/"; then
        break
    fi
    if ! kill -0 "$server_pid" 2>/dev/null; then
        echo "S3Harp exited before accepting connections; see $work/server.log" >&2
        exit 1
    fi
    sleep 0.2
done

cd "$checkout"
S3TEST_CONF="$here/s3tests.conf" PYTHONPATH="$here" \
    "$venv/bin/pytest" -p ratchet -p no:cacheprovider \
    s3tests/functional/test_s3.py s3tests/functional/test_headers.py \
    -q --timeout=60 \
    -m 'not lifecycle and not lifecycle_expiration and not lifecycle_transition and not cloud_transition and not cloud_restore' \
    "$@"
