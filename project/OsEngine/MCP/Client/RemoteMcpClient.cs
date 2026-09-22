/*
 * Your rights to use code governed by this license https://github.com/AlexWan/OsEngine/blob/master/LICENSE
 * Ваши права на использование кода регулируются данной лицензией http://o-s-a.net/doc/license_simple_engine.pdf
*/

using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OsEngine.Logging;
using OsEngine.Market;

namespace OsEngine.MCP.Client
{
    /// <summary>
    /// Async client for a REMOTE OsEngine MCP API (v2, Streamable HTTP) — used by the terminal itself
    /// (not tests) to drive another OsEngine instance, e.g. one running on a VPS. Reused/adapted from
    /// Tests/McpTestStand/OsEngine.McpApi.TestStand/McpApiClient.cs (that one stays sync, test-only).
    /// Клиент для УДАЛЁННОГО MCP API другого экземпляра OsEngine (например, на VPS): используется терминалом,
    /// а не тестами. Держит сессию v2 и фоновый поток чтения SSE-событий (server_instance.*, terminal.*, ...).
    /// </summary>
    public class RemoteMcpClient : IDisposable
    {
        private const string ProtocolVersion = "2024-11-05";

        /// <summary>
        /// full RPC endpoint, e.g. "http://localhost:6510/api/v2/mcp" (same value as .mcp.json's
        /// mcpServers.osengine-server.url — through the same SSH tunnel used elsewhere in this project).
        /// Not a server root: nothing is appended to it.
        /// </summary>
        public string EndpointUrl { get; }
        public string ApiKey { get; }

        /// <summary>
        /// true after a successful initialize() call. Ping()/CallTool() do not require this,
        /// they are used to establish it.
        /// </summary>
        public bool IsConnected { get; private set; }

        /// <summary>
        /// fired for every server-sent event (params.name from notifications/message), e.g.
        /// "server_instance.status_changed", "terminal.mode_changed". Raised on the SSE reader thread —
        /// callers touching WPF controls must marshal onto the Dispatcher themselves.
        /// </summary>
        public event Action<string, JsonElement> EventReceived;

        /// <summary>
        /// fired when the SSE stream drops (network hiccup, server restart). ReconnectLoop already retries;
        /// this is only informational for the UI (e.g. show a "reconnecting" badge).
        /// </summary>
        public event Action<Exception> Disconnected;

        private readonly HttpClient _httpClient;
        private string _sessionId;
        private readonly object _sessionLocker = new object();
        private CancellationTokenSource _sseCts;
        private Task _sseTask;

        public RemoteMcpClient(string endpointUrl, string apiKey)
        {
            EndpointUrl = endpointUrl;
            ApiKey = apiKey;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        }

        #region Connect / tool calls

        /// <summary>
        /// initialize() + start the background SSE reader. Throws on failure (bad url, bad key, network) —
        /// callers show the message and leave IsConnected == false.
        /// </summary>
        public async Task ConnectAsync(CancellationToken cancel = default)
        {
            // capabilities.logging обязателен: без него McpMaster не включает LoggingEnabled для сессии
            // и события (server_instance.*, terminal.*) в GET-поток не идут — только тихий ответ на tools/call.
            await SendRequestAsync("initialize", new
            {
                protocolVersion = ProtocolVersion,
                capabilities = new { logging = new { } },
                clientInfo = new { name = "OsEngine.RobotsVps", version = "1.0.0" }
            }, cancel).ConfigureAwait(false);

            IsConnected = true;

            _sseCts = new CancellationTokenSource();
            _sseTask = Task.Run(() => SseReaderLoop(_sseCts.Token));
        }

        public void Disconnect()
        {
            IsConnected = false;
            _sseCts?.Cancel();
            lock (_sessionLocker) { _sessionId = null; }
        }

        public async Task<JsonElement> PingAsync(CancellationToken cancel = default)
        {
            return await SendRequestAsync("tools/call", new { name = "ping", arguments = new { } }, cancel).ConfigureAwait(false);
        }

        /// <summary>
        /// calls an MCP tool (tools/call) and returns the parsed tool result (already unwrapped from the
        /// content[0].text envelope, since every OsEngine MCP tool replies with a single JSON text block).
        /// </summary>
        public async Task<JsonElement> CallToolAsync(string toolName, object arguments, CancellationToken cancel = default)
        {
            JsonElement envelope = await SendRequestAsync("tools/call",
                new { name = toolName, arguments = arguments ?? new { } }, cancel).ConfigureAwait(false);

            if (envelope.TryGetProperty("isError", out JsonElement isError) && isError.ValueKind == JsonValueKind.True)
            {
                string text = ExtractText(envelope);
                throw new InvalidOperationException($"MCP tool '{toolName}' error: {text}");
            }

            string resultText = ExtractText(envelope);

            if (string.IsNullOrEmpty(resultText))
            {
                return default;
            }

            using JsonDocument doc = JsonDocument.Parse(resultText);
            return doc.RootElement.Clone();
        }

        private static string ExtractText(JsonElement envelope)
        {
            if (envelope.ValueKind == JsonValueKind.Object
                && envelope.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.Array
                && content.GetArrayLength() > 0)
            {
                JsonElement first = content[0];

                if (first.TryGetProperty("text", out JsonElement text))
                {
                    return text.GetString();
                }
            }

            return string.Empty;
        }

        #endregion

        #region JSON-RPC transport

        private async Task<JsonElement> SendRequestAsync(string method, object parameters, CancellationToken cancel)
        {
            var request = new
            {
                jsonrpc = "2.0",
                method,
                @params = parameters,
                id = Guid.NewGuid().ToString()
            };

            string json = JsonSerializer.Serialize(request);

            using HttpRequestMessage httpRequest = new HttpRequestMessage(HttpMethod.Post, EndpointUrl)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            httpRequest.Headers.Add("Accept", "application/json, text/event-stream");

            if (method != "initialize")
            {
                lock (_sessionLocker)
                {
                    if (_sessionId != null)
                    {
                        httpRequest.Headers.Add("Mcp-Session-Id", _sessionId);
                        httpRequest.Headers.Add("MCP-Protocol-Version", ProtocolVersion);
                    }
                }
            }

            using HttpResponseMessage response = await _httpClient.SendAsync(httpRequest, cancel).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body}");
            }

            CaptureSession(response);

            // v2 отвечает либо чистым JSON, либо однокадровым SSE-потоком ("data: {...}") — оба варианта встречались
            string jsonBody = ExtractSseDataIfNeeded(body);

            using JsonDocument document = JsonDocument.Parse(jsonBody);
            JsonElement root = document.RootElement;

            if (root.TryGetProperty("error", out JsonElement errorElement) && errorElement.ValueKind != JsonValueKind.Null)
            {
                string message = errorElement.TryGetProperty("message", out JsonElement messageElement)
                    ? messageElement.GetString() ?? "unknown"
                    : "unknown";
                throw new InvalidOperationException($"MCP error ({method}): {message}");
            }

            return root.TryGetProperty("result", out JsonElement resultElement) ? resultElement.Clone() : default;
        }

        private static string ExtractSseDataIfNeeded(string body)
        {
            if (!body.TrimStart().StartsWith("data:", StringComparison.Ordinal))
            {
                return body;
            }

            foreach (string line in body.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("data:", StringComparison.Ordinal))
                {
                    return trimmed.Substring("data:".Length).Trim();
                }
            }

            return body;
        }

        private void CaptureSession(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                foreach (string value in values)
                {
                    lock (_sessionLocker) { _sessionId = value; }
                    return;
                }
            }
        }

        #endregion

        #region SSE (server-sent events: server_instance.*, terminal.*, bot journal pushes when they land)

        // GET /api/v2/mcp с текущей сессией держит поток событий открытым; при обрыве — переподключение с паузой.
        // На бота/сделки событий сегодня нет (см. docs/SERVER_SETUP.md, этап 1) — окно опрашивает журналы отдельно.
        private async Task SseReaderLoop(CancellationToken cancel)
        {
            while (!cancel.IsCancellationRequested)
            {
                try
                {
                    string sessionId;
                    lock (_sessionLocker) { sessionId = _sessionId; }

                    if (sessionId == null)
                    {
                        await Task.Delay(500, cancel).ConfigureAwait(false);
                        continue;
                    }

                    using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, EndpointUrl);
                    request.Headers.Add("Accept", "text/event-stream");
                    request.Headers.Add("Mcp-Session-Id", sessionId);
                    request.Headers.Add("MCP-Protocol-Version", ProtocolVersion);

                    using HttpResponseMessage response = await _httpClient
                        .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    using Stream stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
                    using StreamReader reader = new StreamReader(stream, Encoding.UTF8);

                    while (!cancel.IsCancellationRequested)
                    {
                        string line = await reader.ReadLineAsync().ConfigureAwait(false);

                        if (line == null)
                        {
                            break; // сервер закрыл поток — переподключаемся во внешнем while
                        }

                        if (!line.StartsWith("data:", StringComparison.Ordinal))
                        {
                            continue;
                        }

                        RaiseEvent(line.Substring("data:".Length).Trim());
                    }
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Disconnected?.Invoke(ex);
                }

                if (!cancel.IsCancellationRequested)
                {
                    try { await Task.Delay(2000, cancel).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        private void RaiseEvent(string dataJson)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(dataJson);
                JsonElement root = doc.RootElement;

                // форма кадра — McpMaster.BuildMessageFrame: {method:"notifications/message", params:{level, logger, data:{event, payload}}}
                if (root.TryGetProperty("method", out JsonElement methodEl)
                    && methodEl.GetString() == "notifications/message"
                    && root.TryGetProperty("params", out JsonElement paramsEl)
                    && paramsEl.TryGetProperty("data", out JsonElement dataEl))
                {
                    string name = dataEl.TryGetProperty("event", out JsonElement nameEl) ? nameEl.GetString() : "";
                    JsonElement payload = dataEl.TryGetProperty("payload", out JsonElement payloadEl) ? payloadEl.Clone() : default;
                    EventReceived?.Invoke(name ?? "", payload);
                }
            }
            catch (Exception ex)
            {
                ServerMaster.SendNewLogMessage("RemoteMcpClient: bad SSE frame: " + ex.Message, LogMessageType.Error);
            }
        }

        #endregion

        public void Dispose()
        {
            Disconnect();
            try { _sseTask?.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
            _httpClient?.Dispose();
        }
    }
}
