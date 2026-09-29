package net.osa.osenginemobile;

import android.content.Context;
import android.util.Base64;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.nio.charset.StandardCharsets;
import java.util.HashMap;
import java.util.Map;

final class McpBridge {
    private final String source;
    private final Map<String, Target> targets = new HashMap<>();

    private static final class Target {
        int port;
        String keyFile;
        String session = "";
    }

    McpBridge(Context context) throws IOException {
        try (InputStream input = context.getAssets().open("mcp_bridge.py")) {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream();
            byte[] buffer = new byte[4096];
            int count;
            while ((count = input.read(buffer)) != -1) bytes.write(buffer, 0, count);
            source = encoded(bytes.toByteArray());
        }
    }

    static JSONObject call(String name, JSONObject arguments) throws JSONException {
        return new JSONObject().put("name", name).put("arguments",
            arguments == null ? new JSONObject() : arguments);
    }

    synchronized Map<String, Object> callBatch(String instanceName, JSONObject... calls)
        throws IOException, JSONException {
        Target target = targets.get(instanceName);
        if (target == null) {
            target = discover(instanceName);
            targets.put(instanceName, target);
        }
        JSONArray request = new JSONArray();
        for (JSONObject call : calls) request.put(call);
        String command = "python3 -c 'import base64;exec(base64.b64decode(\"" + source
            + "\"))' " + target.port + " " + encoded(target.keyFile)
            + " " + (target.session.isEmpty() ? "-" : encoded(target.session))
            + " " + encoded(request.toString());
        JSONObject response = new JSONObject(RemoteSsh.run(command));
        if (response.has("session")) target.session = response.optString("session", "");
        if (response.has("error")) throw new IOException(response.optString("error"));
        JSONArray results = response.getJSONArray("results");
        Map<String, Object> data = new HashMap<>();
        for (int i = 0; i < results.length(); i++) {
            JSONObject item = results.getJSONObject(i);
            String name = item.getString("name");
            if (item.has("error")) data.put(name, new IOException(item.getString("error")));
            else data.put(name, item.get("data"));
        }
        return data;
    }

    private static Target discover(String instanceName) throws IOException {
        String service = "main".equals(instanceName) ? "osengine" : "osengine-" + instanceName;
        if (!service.matches("osengine(?:-[a-z0-9-]+)?"))
            throw new IOException("Недопустимое имя терминала");
        String unit = "/etc/systemd/system/" + service + ".service";
        String command = "ex=$(grep '^ExecStart=' " + unit + "); "
            + "port=$(printf '%s' \"$ex\" | sed -n 's/.*--mcp-port \\([0-9]*\\).*/\\1/p'); "
            + "key=$(printf '%s' \"$ex\" | sed -n 's/.*--mcp-key-file \\([^ ]*\\).*/\\1/p'); "
            + "printf '%s|%s' \"$port\" \"$key\"";
        String[] parts = RemoteSsh.run(command).trim().split("\\|", 2);
        if (parts.length != 2 || !parts[0].matches("[0-9]{2,5}") || parts[1].isEmpty())
            throw new IOException("В службе терминала не найдены MCP-порт и файл ключа");
        int port = Integer.parseInt(parts[0]);
        if (port < 1 || port > 65535) throw new IOException("Недопустимый MCP-порт");
        Target target = new Target();
        target.port = port;
        target.keyFile = parts[1];
        return target;
    }

    private static String encoded(String value) {
        return encoded(value.getBytes(StandardCharsets.UTF_8));
    }

    private static String encoded(byte[] value) {
        return Base64.encodeToString(value, Base64.NO_WRAP);
    }
}
