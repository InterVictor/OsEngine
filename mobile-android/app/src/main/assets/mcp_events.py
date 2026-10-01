"""Stream the terminal's loopback MCP event feed to stdout; key stays on VPS."""
import base64
import sys
import urllib.request

port = int(sys.argv[1])
key = open(base64.b64decode(sys.argv[2]).decode("utf-8"), encoding="utf-8").read().strip()
request = urllib.request.Request("http://127.0.0.1:%d/api/v1/events" % port,
                                 headers={"X-Api-Key": key, "Accept": "text/event-stream"})
with urllib.request.urlopen(request, timeout=90) as response:
    for line in response:
        sys.stdout.write(line.decode("utf-8", "replace"))
        sys.stdout.flush()
