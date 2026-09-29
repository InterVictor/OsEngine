"""Short MCP call batch through the selected terminal's loopback API; key stays on VPS."""
import base64
import json
import sys
import urllib.error
import urllib.request


def decoded(index):
    return base64.b64decode(sys.argv[index]).decode("utf-8")


port = int(sys.argv[1])
key_file = decoded(2)
session = "" if sys.argv[3] == "-" else decoded(3)
calls = json.loads(decoded(4))
api_key = open(key_file, encoding="utf-8").read().strip()
endpoint = "http://127.0.0.1:%d/api/v2/mcp" % port


def request(method, parameters, notification=False):
    global session
    message = {"jsonrpc": "2.0", "method": method, "params": parameters}
    if not notification:
        message["id"] = "android"
    headers = {"X-Api-Key": api_key, "Accept": "application/json, text/event-stream",
               "Content-Type": "application/json", "MCP-Protocol-Version": "2024-11-05"}
    if session:
        headers["Mcp-Session-Id"] = session
    req = urllib.request.Request(endpoint, data=json.dumps(message).encode("utf-8"),
                                 headers=headers, method="POST")
    with urllib.request.urlopen(req, timeout=12) as response:
        if response.headers.get("Mcp-Session-Id"):
            session = response.headers["Mcp-Session-Id"]
        body = response.read().decode("utf-8").strip()
    if notification:
        return {}
    if body.startswith("data:"):
        body = next((line[5:].strip() for line in body.splitlines()
                     if line.startswith("data:")), body)
    answer = json.loads(body)
    if answer.get("error"):
        raise RuntimeError(str(answer["error"].get("message", answer["error"])))
    return answer.get("result", {})


def initialize():
    global session
    session = ""
    request("initialize", {"protocolVersion": "2024-11-05",
                           "capabilities": {"logging": {}},
                           "clientInfo": {"name": "OsEngine.Mobile", "version": "0.1"}})
    request("notifications/initialized", {}, notification=True)


try:
    if not session:
        initialize()
    results = []
    for call in calls:
        try:
            try:
                result = request("tools/call", {"name": call["name"],
                                                "arguments": call.get("arguments", {})})
            except urllib.error.HTTPError as error:
                if error.code != 404:
                    raise
                initialize()
                result = request("tools/call", {"name": call["name"],
                                                "arguments": call.get("arguments", {})})
            if result.get("isError"):
                raise RuntimeError(str(result.get("content", [{}])[0].get("text", "MCP error")))
            content = result.get("content", [])
            text = content[0].get("text", "{}") if content else "{}"
            results.append({"name": call["name"], "data": json.loads(text)})
        except Exception as error:
            results.append({"name": call["name"], "error": str(error)})
    print(json.dumps({"session": session, "results": results}, ensure_ascii=False))
except Exception as error:
    print(json.dumps({"session": session, "error": str(error)}, ensure_ascii=False))
