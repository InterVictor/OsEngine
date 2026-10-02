# Ввод ключей биржи в коннектор OsEngine на сервере через MCP API (SSH-туннель должен быть поднят).
# Ключи спрашиваются здесь, в окне PowerShell, и уходят только на сервер — нигде не печатаются и не сохраняются на ПК.
#
#   powershell -ExecutionPolicy Bypass -File D:\ff-research\tools\set-server-keys.ps1            # ключи BinanceFutures из буфера обмена
#   powershell -ExecutionPolicy Bypass -File D:\ff-research\tools\set-server-keys.ps1 -Typed     # скрытый ввод с клавиатуры
#   powershell -ExecutionPolicy Bypass -File D:\ff-research\tools\set-server-keys.ps1 -Test      # только проверка связи
#
# Параметры: -Type (тип коннектора, по умолчанию BinanceFutures), -Number (номер экземпляра, 0),
#            -McpJson (откуда брать адрес и X-Api-Key сервера).

param(
    [string]$Type = 'BinanceFutures',
    [int]$Number = 0,
    [string]$McpJson = 'D:\OsEngine-masterBB\.mcp.json',
    [switch]$Test,
    [switch]$Typed
)

$ErrorActionPreference = 'Stop'

$cfg = (Get-Content $McpJson -Raw | ConvertFrom-Json).mcpServers.'osengine-server'
$url = $cfg.url
$headers = @{ 'X-Api-Key' = $cfg.headers.'X-Api-Key'; 'Accept' = 'application/json, text/event-stream' }
$script:id = 0

function Invoke-Mcp([string]$method, $params, [switch]$Notify) {
    $body = @{ jsonrpc = '2.0'; method = $method }
    if ($null -ne $params) { $body.params = $params }
    if (-not $Notify) { $script:id++; $body.id = $script:id }
    $json = $body | ConvertTo-Json -Depth 10 -Compress
    $r = Invoke-WebRequest -Uri $url -Method Post -Headers $headers -ContentType 'application/json; charset=utf-8' `
        -Body ([Text.Encoding]::UTF8.GetBytes($json)) -UseBasicParsing
    if ($r.Headers['Mcp-Session-Id']) { $headers['Mcp-Session-Id'] = $r.Headers['Mcp-Session-Id'] }
    if ($Notify) { return $null }
    $text = if ($r.Content -is [byte[]]) { [Text.Encoding]::UTF8.GetString($r.Content) } else { $r.Content }
    # ответ может прийти JSON-ом или потоком SSE (строки "data: {...}")
    $data = ($text -split "`n" | Where-Object { $_ -like 'data:*' } | ForEach-Object { $_.Substring(5).Trim() }) -join ''
    if (-not $data) { $data = $text }
    $resp = $data | ConvertFrom-Json
    if ($resp.error) { throw "MCP ${method}: $($resp.error.message)" }
    return $resp.result
}

function Invoke-Tool([string]$name, $arguments) {
    $res = Invoke-Mcp 'tools/call' @{ name = $name; arguments = $arguments }
    $txt = ($res.content | ForEach-Object { $_.text }) -join ''
    if ($res.isError) { throw "${name}: $txt" }
    return $txt
}

function Read-Secret([string]$prompt) {
    $s = Read-Host -Prompt $prompt -AsSecureString
    $p = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($s)
    try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($p).Trim() }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($p) }
}

try {
    $null = Invoke-Mcp 'initialize' @{ protocolVersion = '2025-03-26'; capabilities = @{}; clientInfo = @{ name = 'set-server-keys'; version = '1.0' } }
    Invoke-Mcp 'notifications/initialized' $null -Notify
} catch {
    Write-Host "Нет связи с сервером ($url). Поднят ли SSH-туннель? $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

Write-Host "Связь есть: ping -> $(Invoke-Tool 'ping' @{})"
Write-Host "Коннектор ${Type} №${Number}: $(Invoke-Tool 'server_instance_get_status' @{ type = $Type; number = $Number })"
if ($Test) { exit 0 }

# По умолчанию ключи берутся из буфера обмена: вставка в скрытое поле в панели терминала Claude
# передаёт только один символ (2026-09-22 так на сервер ушли ключи длиной 1). -Typed — скрытый ввод с клавиатуры.
function Get-Key([string]$label) {
    if ($Typed) { return Read-Secret "$label (ввод скрыт)" }
    Read-Host "Скопируйте $label в буфер обмена (Ctrl+C на сайте биржи) и нажмите Enter" | Out-Null
    $v = Invoke-Clipboard { (Get-Clipboard -Format Text -Raw) -as [string] }
    if ($v) { $v = $v.Trim() }
    Invoke-Clipboard { Set-Clipboard -Value ' ' } | Out-Null   # не оставляем ключ в буфере
    return $v
}

# буфер обмена бывает кратко занят другой программой (браузер, менеджер паролей, история буфера Windows):
# "Requested Clipboard operation did not succeed" — повторяем до 20 раз с паузой
function Invoke-Clipboard([scriptblock]$action) {
    for ($i = 1; ; $i++) {
        try { return & $action }
        catch {
            if ($i -ge 20) { Write-Host 'Буфер обмена занят другой программой. Ничего не отправлено, запустите скрипт ещё раз.' -ForegroundColor Red; exit 1 }
            Start-Sleep -Milliseconds 150
        }
    }
}

function Test-Key([string]$label, [string]$v) {
    # ключи Binance — 64 латинских буквы/цифры; печатаем только длину, не значение
    if ($v -notmatch '^[A-Za-z0-9]{32,128}$') {
        $len = if ($v) { $v.Length } else { 0 }
        Write-Host "$label не похож на ключ (длина $len, допустимы только латинские буквы и цифры, 32–128 символов). Ничего не отправлено." -ForegroundColor Red
        exit 1
    }
    Write-Host "$label принят: $($v.Length) символов"
}

$public = Get-Key 'Public key (API Key)'
Test-Key 'Public key' $public
$secret = Get-Key 'Secret key'
Test-Key 'Secret key' $secret
if ($public -eq $secret) { Write-Host 'Public и Secret совпадают — видимо, буфер не обновился. Ничего не отправлено.' -ForegroundColor Red; exit 1 }

$null = Invoke-Tool 'server_instance_set_params' @{ type = $Type; number = $Number; parameters = @(
    @{ name = 'Public key'; value = $public },
    @{ name = 'Secret key'; value = $secret }) }
$public = $null; $secret = $null

Write-Host 'Ключи записаны на сервере. Подключение коннектора — отдельной командой (через Claude).' -ForegroundColor Green
