#!/bin/bash
# What changed in the official OsEngine (AlexWan/OsEngine master) since the fork last took it, and what it means
# for the fork and for the VPS server build. Read-only: fetches upstream into FETCH_HEAD, changes no branch.
#
# Usage: bash check-upstream.sh
#   FORK_DIR     fork checkout             (default: репозиторий со скриптом)
#   FORK_BRANCH  branch to compare with    (default osengine-vps)
#   SERVER_LIST  server source list        (default: headless/files.txt этого репозитория)

set -euo pipefail

_SELF=$(cd "$(dirname "$0")/.." && { pwd -W 2>/dev/null || pwd; })
FORK_DIR=${FORK_DIR:-$_SELF}
FORK_BRANCH=${FORK_BRANCH:-osengine-vps}
SERVER_LIST=${SERVER_LIST:-$_SELF/headless/files.txt}
UPSTREAM=https://github.com/AlexWan/OsEngine.git
PREFIX=project/OsEngine/
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT

cd "$FORK_DIR"
git fetch -q "$UPSTREAM" master
BASE=$(git merge-base FETCH_HEAD "$FORK_BRANCH")
COUNT=$(git rev-list --count "$BASE"..FETCH_HEAD)

echo "Upstream AlexWan/OsEngine master: $(git log -1 --format='%h %cd' --date=short FETCH_HEAD)"
echo "Fork $FORK_BRANCH last took upstream at: $(git log -1 --format='%h %cd' --date=short "$BASE")"
echo "New upstream commits: $COUNT"

if [ "$COUNT" = "0" ]; then
    echo "Nothing to take."

    # The main-menu "Update (N)" button asks the official update server how many commits came after the date in
    # Engine\Updater\LastUpdatesInfo.txt. The fork never runs the built-in updater, so after a git merge that date is
    # stale and the button shows a count although nothing is missing — set it to the newest upstream commit taken.
    UPDATER_FILE="$FORK_DIR/project/OsEngine/bin/Debug/Engine/Updater/LastUpdatesInfo.txt"
    if [ -d "$(dirname "$UPDATER_FILE")" ]; then
        SYNCED=$(date -u -d "$(git log -1 --format=%cI FETCH_HEAD)" +%Y-%m-%dT%H:%M:%S.0000000Z)
        printf '%s' "$SYNCED" > "$UPDATER_FILE"
        echo "Update button baseline set to $SYNCED (restart the terminal to see 0)."
    fi
    exit 0
fi

echo
git log --format='  %h %cd %s' --date=short "$BASE"..FETCH_HEAD | head -40
[ "$COUNT" -gt 40 ] && echo "  ... and $((COUNT - 40)) more"

git diff --name-only "$BASE" FETCH_HEAD | sed "s#^$PREFIX##" | sort > "$TMP/upstream"
git diff --name-only "$BASE" "$FORK_BRANCH" | sed "s#^$PREFIX##" | grep -v '^bin/' | sort > "$TMP/fork"
tr -d '\r' < "$SERVER_LIST" | sed 's#\\#/#g' | sort > "$TMP/server"

comm -12 "$TMP/upstream" "$TMP/server" > "$TMP/server_changed"
comm -12 "$TMP/upstream" "$TMP/fork" > "$TMP/conflict"
grep '^MCP/' "$TMP/upstream" > "$TMP/mcp" || true

echo
echo "Files changed upstream: $(wc -l < "$TMP/upstream")"

# Groups — what each change means for this fork. Everything is merged anyway (git keeps track of what was taken);
# the groups only decide what has to be rebuilt and deployed.
#   connectors  server-side files under Market/Servers/      -> rebuild + deploy the VPS server
#   server      other server-side files (core, MCP, robots)  -> rebuild + deploy the VPS server
#   unused      parts neither OsEngineVPS nor the server run: connectors/robots not on the server, OsEngine main window,
#               OsData, optimizer, converter, test stands, docs, binaries
#   client      any other OsEngine source (journal, charts, entities, themes...) -> rebuild OsEngineVPS
UNUSED_RE='^(Market/Servers/|Robots/|MainWindow.|OsData/|OsOptimizer/|OsConverter/|OsMiner/|OsTrader/Gui/BotStation|bin/|project/Tests/|Tests/|project/[^/]*\.md$|[^/]*\.md$|.*/[^/]*\.md$|project/OsEngine\.sln$|DividendsUpdater/)'
grep '^Market/Servers/' "$TMP/server_changed" > "$TMP/g_connectors" || true
grep -v '^Market/Servers/' "$TMP/server_changed" > "$TMP/g_server" || true
comm -23 "$TMP/upstream" "$TMP/server" > "$TMP/not_server"
grep -E "$UNUSED_RE" "$TMP/not_server" > "$TMP/g_unused" || true
grep -vE "$UNUSED_RE" "$TMP/not_server" > "$TMP/g_client" || true

show_group() { # file title
    echo
    if [ -s "$1" ]; then
        echo "$2 ($(wc -l < "$1")):"
        head -25 "$1" | sed 's/^/  /'
        [ "$(wc -l < "$1")" -gt 25 ] && echo "  ... and $(( $(wc -l < "$1") - 25 )) more"
    else
        echo "$2: none"
    fi
    return 0
}

show_group "$TMP/g_connectors" "CONNECTORS (server)"
show_group "$TMP/g_server" "SERVER CORE / MCP"
show_group "$TMP/g_client" "USED BY OsEngineVPS (client)"
show_group "$TMP/g_unused" "NOT USED here (OsData, optimizer, tests, docs...)"

echo
echo "WHAT TO DO after the merge:"
if [ -s "$TMP/server_changed" ]; then
    echo "  - VPS server: rebuild (headless/build-package.sh) and deploy — ask the user first while the keys are trading"
else
    echo "  - VPS server: nothing — no server-side file changed, terminals are not restarted"
fi
if [ -s "$TMP/g_client" ] || [ -s "$TMP/server_changed" ]; then
    echo "  - OsEngineVPS: rebuild (dotnet build OsEngineVPS/OsEngineVPS.csproj) and restart it (the SSH tunnel lives in it)"
else
    echo "  - OsEngineVPS: nothing to rebuild"
fi
[ -s "$TMP/g_unused" ] && [ ! -s "$TMP/g_client" ] && [ ! -s "$TMP/server_changed" ] && echo "  - only unused parts changed: merge and push, no build, no deploy"

echo
if [ -s "$TMP/conflict" ]; then
    echo "CONFLICT RISK: changed both upstream and in the fork ($(wc -l < "$TMP/conflict")):"
    sed 's/^/  /' "$TMP/conflict"
else
    echo "CONFLICT RISK: none — upstream did not touch files the fork changed."
fi

if [ -s "$TMP/mcp" ]; then
    echo
    echo "MCP API changed upstream (check Robots.VPS tools and CONTEXT_MCP_V2.md):"
    sed 's/^/  /' "$TMP/mcp"
fi
