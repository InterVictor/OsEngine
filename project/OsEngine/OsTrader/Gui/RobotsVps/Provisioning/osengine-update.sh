#!/bin/bash
# Updates the OsEngine build of one terminal on the VPS, with automatic rollback.
# Usage: OSENGINE_BASE=/opt/osengine OSENGINE_SERVICE=osengine OSENGINE_MCP_PORT=6500 osengine-update.sh <app-package.tgz>
#
# 1. unpacks the new build next to the current one (app.next-<time>) — the running terminal is not touched yet;
# 2. stops the service, keeps the current build as app.prev-<time>, puts the new one in place, starts the service;
# 3. waits up to 60 s for the MCP API; if it does not answer, puts the previous build back and starts it again.
# Only the build (app) is replaced: robots, settings, journals and keys in data/ stay as they are.
# Keeps the 3 newest app.prev-* backups. Skips the terminal when it already runs this exact package.
# Every progress line starts with "STEP", "OK", "SKIP", "WARN" or "FAIL" (shown by the client).

set -u

PACKAGE="${1:-}"
BASE="${OSENGINE_BASE:-/opt/osengine}"
SERVICE="${OSENGINE_SERVICE:-osengine}"
MCP_PORT="${OSENGINE_MCP_PORT:-6500}"
APP="$BASE/app"
VERSION_FILE=".package-sha256"

fail() { echo "FAIL $*"; exit 1; }

mcp_answers() {
    for i in $(seq 1 60); do
        # a body is required: an empty POST gets 411 instead of reaching the key check
        CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' "http://127.0.0.1:$MCP_PORT/api/v2/mcp" || true)
        [ "$CODE" = "401" ] || [ "$CODE" = "200" ] && return 0
        sleep 1
    done
    return 1
}

echo "STEP 1/4 checking"
[ "$(id -u)" = "0" ] || fail "must run as root"
[ -x "$APP/OsEngine" ] || fail "no build installed in $APP — use Deploy / repair server first"
[ -n "$PACKAGE" ] && [ -f "$PACKAGE" ] || fail "package not found: $PACKAGE"
NEW=$(sha256sum "$PACKAGE" | cut -c1-64)
OLD=$(cat "$APP/$VERSION_FILE" 2>/dev/null || true)
if [ "$NEW" = "$OLD" ]; then
    echo "SKIP $SERVICE already runs build ${NEW:0:8}"
    echo "DONE"
    exit 0
fi
echo "OK $SERVICE: ${OLD:0:8}${OLD:+ -> }${NEW:0:8}"

TS=$(date -u +%Y%m%dT%H%M%SZ)
NEXT="$BASE/app.next-$TS"
PREV="$BASE/app.prev-$TS"

echo "STEP 2/4 unpacking the new build"
mkdir -p "$NEXT"
tar xzf "$PACKAGE" -C "$NEXT" || { rm -rf "$NEXT"; fail "unpack $PACKAGE"; }
chmod +x "$NEXT/OsEngine"
echo "$NEW" > "$NEXT/$VERSION_FILE"
chown -R osengine:osengine "$NEXT"
echo "OK unpacked ($(du -sh "$NEXT" | cut -f1))"

echo "STEP 3/4 switching $SERVICE to the new build"
systemctl stop "$SERVICE" || fail "systemctl stop $SERVICE"
mv "$APP" "$PREV" || { systemctl start "$SERVICE"; fail "move the current build aside"; }
mv "$NEXT" "$APP" || { mv "$PREV" "$APP"; systemctl start "$SERVICE"; fail "put the new build in place"; }
systemctl start "$SERVICE"
echo "OK service started, previous build kept as $PREV"

echo "STEP 4/4 checking that the terminal answers"
if mcp_answers; then
    echo "OK MCP API answers on 127.0.0.1:$MCP_PORT"
else
    journalctl -u "$SERVICE" -n 15 --no-pager | sed 's/^/WARN /'
    echo "WARN the new build does not answer — rolling back"
    systemctl stop "$SERVICE"
    rm -rf "$APP"
    mv "$PREV" "$APP"
    systemctl start "$SERVICE"
    if mcp_answers; then
        fail "update rolled back: $SERVICE runs the previous build again"
    fi
    fail "update rolled back, but the previous build does not answer either — check journalctl -u $SERVICE"
fi

# keep the 3 newest backups of this terminal
ls -dt "$BASE"/app.prev-* 2>/dev/null | tail -n +4 | xargs -r rm -rf
echo "OK backups kept: $(ls -d "$BASE"/app.prev-* 2>/dev/null | wc -l)"
echo "DONE"
