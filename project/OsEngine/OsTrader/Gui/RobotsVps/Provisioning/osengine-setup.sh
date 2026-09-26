#!/bin/bash
# OsEngine VPS setup — run as root on a fresh Ubuntu 22.04/24.04 server.
# Automates the manual procedure in D:\ff-research\docs\SERVER_SETUP.md (2026-09-22).
# Idempotent: every step checks the current state first and prints SKIP when already done, so the
# script is safe to run again on a working server ("repair" mode) — it never overwrites the MCP key,
# the robot scripts or an installed build.
#
# Usage: osengine-setup.sh <app-package.tgz> [custom.tgz]
#   app-package.tgz — headless linux-x64 publish (contents of /opt/osengine/app)
#   custom.tgz      — Custom folder (Robots, Indicators, CandleSeries) for /opt/osengine/data
#
# Every line of progress starts with "STEP", "OK", "SKIP", "WARN" or "FAIL" so the client can show it.

set -u
export DEBIAN_FRONTEND=noninteractive

APP_PACKAGE="${1:-}"
CUSTOM_PACKAGE="${2:-}"
# Overridable for extra instances / tests; the defaults are the single-terminal layout of SERVER_SETUP.md.
BASE="${OSENGINE_BASE:-/opt/osengine}"
SERVICE="${OSENGINE_SERVICE:-osengine}"
MCP_PORT="${OSENGINE_MCP_PORT:-6500}"
APP="$BASE/app"
DATA="$BASE/data"
KEY_FILE="$BASE/mcp.key"
UNIT="/etc/systemd/system/$SERVICE.service"

fail() { echo "FAIL $*"; exit 1; }

echo "STEP 1/11 checking the system"
[ "$(id -u)" = "0" ] || fail "must run as root"
command -v apt-get >/dev/null || fail "apt-get not found: only Ubuntu/Debian is supported"
. /etc/os-release
echo "OK $PRETTY_NAME, $(nproc) CPU, $(free -m | awk '/Mem:/ {print $2}') MB RAM, $(df -h / | awk 'NR==2 {print $4}') free disk"

echo "STEP 2/11 time: UTC + NTP"
if [ "$(timedatectl show -p Timezone --value)" != "UTC" ]; then
    timedatectl set-timezone UTC && echo "OK timezone set to UTC"
else
    echo "SKIP timezone is already UTC"
fi
mkdir -p /etc/systemd/timesyncd.conf.d
if [ ! -f /etc/systemd/timesyncd.conf.d/osengine.conf ]; then
    # the hosting of the first VPS blocked ntp.ubuntu.com (UDP 123) — cloudflare answered
    printf '[Time]\nNTP=time.cloudflare.com\nFallbackNTP=time.google.com pool.ntp.org ntp.ubuntu.com\n' \
        > /etc/systemd/timesyncd.conf.d/osengine.conf
    systemctl restart systemd-timesyncd
    echo "OK NTP servers configured"
else
    echo "SKIP NTP already configured"
fi
timedatectl set-ntp true

echo "STEP 3/11 packages: ufw, fail2ban, curl, openssl"
MISSING=""
for p in ufw fail2ban curl openssl tar; do
    dpkg -s "$p" >/dev/null 2>&1 || MISSING="$MISSING $p"
done
if [ -n "$MISSING" ]; then
    apt-get update -qq >/dev/null && apt-get install -y -qq $MISSING >/dev/null || fail "apt-get install$MISSING"
    echo "OK installed:$MISSING"
else
    echo "SKIP all packages present"
fi

echo "STEP 4/11 firewall: only SSH is open"
ufw allow OpenSSH >/dev/null
if ufw status | grep -q "Status: active"; then
    echo "SKIP ufw already active"
else
    ufw --force enable >/dev/null && echo "OK ufw enabled (incoming denied except SSH; MCP port $MCP_PORT stays closed)"
fi

echo "STEP 5/11 fail2ban for SSH password login"
JAIL=/etc/fail2ban/jail.d/osengine-sshd.local
if [ ! -f "$JAIL" ]; then
    printf '[sshd]\nenabled  = true\nbackend  = systemd\nmaxretry = 5\nfindtime = 10m\nbantime  = 1h\n' > "$JAIL"
    systemctl enable fail2ban >/dev/null 2>&1
    systemctl restart fail2ban
    echo "OK fail2ban: 5 wrong passwords in 10 min -> 1 h ban"
else
    systemctl enable --now fail2ban >/dev/null 2>&1
    echo "SKIP fail2ban already configured"
fi

echo "STEP 6/11 service user osengine"
if id osengine >/dev/null 2>&1; then
    echo "SKIP user exists"
else
    useradd --system --home-dir "$BASE" --shell /usr/sbin/nologin osengine && echo "OK user created"
fi

echo "STEP 7/11 folders"
mkdir -p "$APP" "$DATA"
echo "OK $APP, $DATA"

echo "STEP 8/11 OsEngine build"
if [ -x "$APP/OsEngine" ]; then
    echo "SKIP build already installed (updates are a separate action)"
elif [ -z "$APP_PACKAGE" ] && [ "$BASE" != "/opt/osengine" ] && [ -x /opt/osengine/app/OsEngine ]; then
    # an extra terminal: same build as the main one, copied on the server (no 60 MB upload)
    cp -a /opt/osengine/app/. "$APP/" || fail "copy the build of the main terminal"
    echo "OK build copied from the main terminal ($(du -sh "$APP" | cut -f1))"
else
    [ -n "$APP_PACKAGE" ] && [ -f "$APP_PACKAGE" ] || fail "build package not found: $APP_PACKAGE"
    tar xzf "$APP_PACKAGE" -C "$APP" || fail "unpack $APP_PACKAGE"
    chmod +x "$APP/OsEngine"
    echo "OK build installed ($(du -sh "$APP" | cut -f1))"
fi

echo "STEP 9/11 robot scripts (Custom)"
if [ -d "$DATA/Custom/Robots" ]; then
    echo "SKIP Custom already present ($(ls "$DATA/Custom/Robots" | wc -l) robot files)"
elif [ -n "$CUSTOM_PACKAGE" ] && [ -f "$CUSTOM_PACKAGE" ]; then
    tar xzf "$CUSTOM_PACKAGE" -C "$DATA" || fail "unpack $CUSTOM_PACKAGE"
    echo "OK Custom unpacked ($(ls "$DATA/Custom/Robots" 2>/dev/null | wc -l) robot files)"
elif [ "$BASE" != "/opt/osengine" ] && [ -d /opt/osengine/data/Custom/Robots ]; then
    cp -a /opt/osengine/data/Custom "$DATA/" || fail "copy Custom of the main terminal"
    # the robot description cache (wiki_robots_list) is valid for the same scripts — saves ~20 s on the first "Add bot"
    [ -f /opt/osengine/data/BotsDescription.txt ] && cp /opt/osengine/data/BotsDescription.txt "$DATA/"
    echo "OK Custom copied from the main terminal ($(ls "$DATA/Custom/Robots" | wc -l) robot files)"
else
    echo "WARN no Custom package given — robot scripts must be uploaded later"
fi

echo "STEP 10/11 MCP API key"
if [ -s "$KEY_FILE" ]; then
    echo "SKIP key exists (kept)"
else
    openssl rand -hex 32 > "$KEY_FILE" || fail "openssl rand"
    echo "OK new key generated"
fi
chown osengine:osengine "$KEY_FILE"
chmod 600 "$KEY_FILE"
chown -R osengine:osengine "$BASE"

echo "STEP 11/11 systemd service"
NEW_UNIT=$(cat <<EOF
# systemd unit of the headless OsEngine build (written by osengine-setup.sh)
[Unit]
Description=OsEngine headless (Robots Light)
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=osengine
WorkingDirectory=$DATA
ExecStart=$APP/OsEngine --root $DATA --mcp-port $MCP_PORT --mcp-key-file $KEY_FILE
KillSignal=SIGTERM
TimeoutStopSec=30
Restart=on-failure
RestartSec=10
Environment=DOTNET_gcServer=0

[Install]
WantedBy=multi-user.target
EOF
)
if [ -f "$UNIT" ] && [ "$(cat "$UNIT")" = "$NEW_UNIT" ]; then
    echo "SKIP unit unchanged"
elif [ -f "$UNIT" ]; then
    # an existing, hand-written unit (e.g. the first VPS) is left alone: it already works
    echo "SKIP unit exists (not written by this script) — kept as is"
else
    echo "$NEW_UNIT" > "$UNIT"
    systemctl daemon-reload
    echo "OK unit written"
fi
systemctl enable "$SERVICE" >/dev/null 2>&1
if systemctl is-active --quiet "$SERVICE"; then
    echo "SKIP service already running"
else
    systemctl start "$SERVICE" || fail "systemctl start $SERVICE"
    echo "OK service started"
fi

# health check: the MCP API must answer (401 without a key means it is up)
for i in $(seq 1 30); do
    # a body is required: an empty POST gets 411 Length Required instead of reaching the key check
    CODE=$(curl -s -o /dev/null -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d '{}' "http://127.0.0.1:$MCP_PORT/api/v2/mcp" || true)
    [ "$CODE" = "401" ] || [ "$CODE" = "200" ] && break
    sleep 1
done
if [ "$CODE" = "401" ] || [ "$CODE" = "200" ]; then
    echo "OK MCP API answers on 127.0.0.1:$MCP_PORT"
else
    journalctl -u "$SERVICE" -n 20 --no-pager | sed 's/^/WARN /'
    fail "MCP API does not answer (last HTTP code: $CODE)"
fi

echo "DONE"
