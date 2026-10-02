// Точка входа серверной (headless) сборки OsEngine: «Роботы Light» без окон.
// Повторяет MainWindow.ButtonRobotLight_Click + RobotUiLite, но все окна = null.
//
// Запуск: OsEngine [--root <папка с Engine/Custom/Data>] [--mcp-port N] [--no-mcp]
// Остановка: Ctrl+C или SIGTERM (systemd).

using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using OsEngine.Entity;
using OsEngine.Logging;
using OsEngine.MCP;
using OsEngine.Market;
using OsEngine.OsTrader;

namespace OsEngine.Headless
{
    public static class HeadlessProgram
    {
        private static readonly ManualResetEventSlim Exit = new ManualResetEventSlim(false);

        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string root = Arg(args, "--root") ?? Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(root);

            // Культура как у Windows-терминала: в ней записаны числа и даты в файлах настроек и журналах.
            // На Windows все потоки OsEngine берут культуру системы; на Linux она зависит от LANG, поэтому задаём явно.
            var culture = new System.Globalization.CultureInfo(Arg(args, "--culture") ?? "ru-RU");
            System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
            System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
            Thread.CurrentThread.CurrentCulture = culture;
            Thread.CurrentThread.CurrentUICulture = culture;

            Console.WriteLine($"OsEngine.Headless {typeof(HeadlessProgram).Assembly.GetName().Version}, root: {root}, culture: {culture.Name}");

            Console.CancelKeyPress += (s, e) => { e.Cancel = true; Exit.Set(); };
            // SIGTERM (systemctl stop) и SIGHUP: мягкая остановка вместо немедленного завершения
            using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; Exit.Set(); });
            using var sighup = System.Runtime.InteropServices.PosixSignalRegistration.Create(
                System.Runtime.InteropServices.PosixSignal.SIGHUP, ctx => { ctx.Cancel = true; Exit.Set(); });
            AppDomain.CurrentDomain.UnhandledException += (s, e) => Console.Error.WriteLine("UNHANDLED: " + e.ExceptionObject);

            MainWindow.ProccesIsWorked = true;
            MainWindow.DebuggerIsWork = Debugger.IsAttached;

            if (Arg(args, "--check-bot") is string botClass)
            {
                // диагностика: скомпилировать скрипт робота и создать экземпляр без запуска торговли
                ServerMaster.LogMessageEvent += (m, t) => Console.WriteLine($"[{t}] {m}");
                try
                {
                    var bot = OsEngine.Robots.BotFactory.GetStrategyForName(botClass, "check" + botClass, StartProgram.IsOsTrader, true);
                    Console.WriteLine(bot == null ? "NULL" : $"OK: {bot.GetType().FullName}, вкладок: {bot.GetTabs().Count}");

                    // FF142/FF143: отладочный график спектра вызывается до расчёта сигналов — он не должен падать
                    var spectrum = bot?.GetType().GetMethod("UpdateSpectrumChart", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public);
                    if (spectrum != null)
                    {
                        var ps = spectrum.GetParameters();
                        object[] a = new object[ps.Length];
                        for (int i = 0; i < ps.Length; i++)
                            a[i] = ps[i].ParameterType == typeof(double[]) ? new double[] { 0, 1, 0.5, 0.3, 0.2 }
                                : ps[i].ParameterType == typeof(int) ? 100 : ps[i].ParameterType == typeof(double) ? 0.5
                                : ps[i].ParameterType == typeof(bool) ? false : null;
                        try { spectrum.Invoke(bot, a); Console.WriteLine("UpdateSpectrumChart: OK"); }
                        catch (System.Reflection.TargetInvocationException tie) { Console.WriteLine("UpdateSpectrumChart: " + tie.InnerException); }
                    }
                    bot?.Delete();
                }
                catch (Exception ex) { Console.WriteLine("EXCEPTION: " + ex); }
                return 0;
            }

            ServerMaster.LogMessageEvent += (message, type) => Console.WriteLine($"{DateTime.Now:HH:mm:ss} [{type}] {message}");

            // как MainWindow: сбор нагрузки (RAM/CPU/очереди) для system_load_* в MCP
            OsEngine.OsTrader.SystemAnalyze.SystemUsageAnalyzeMaster.Activate();

            ServerMaster.Activate();
            ServerMaster.RealStarted();
            ServerMaster.GetServers();

            OsTraderMaster master = new OsTraderMaster(null,
                null, null, null, null,
                null, new System.Windows.Forms.Integration.WindowsFormsHost(), null, null, null, null, null,
                null, StartProgram.IsOsTrader, null, null); // hostLogPrime: RobotUiLite передаёт настоящее окно

            McpMaster mcp = null;
            if (!Has(args, "--no-mcp"))
            {
                int port = int.TryParse(Arg(args, "--mcp-port"), out int p) ? p : McpSettings.Port;
                // ключ API из файла (сервер): без него McpSettings отдаёт общеизвестный ключ по умолчанию
                string apiKey = McpSettings.ApiKey;
                if (Arg(args, "--mcp-key-file") is string keyFile)
                {
                    apiKey = File.ReadAllText(keyFile).Trim();
                    if (apiKey.Length < 16) throw new InvalidOperationException("MCP key file: ключ короче 16 символов");
                }
                else if (apiKey == "osengine-mcp-default-key")
                    Console.WriteLine("ВНИМАНИЕ: MCP API с ключом по умолчанию. На сервере используйте --mcp-key-file.");
                mcp = new McpMaster(port, apiKey, getTerminalStatus: () => new McpTerminalStatus
                {
                    Mode = StartProgram.IsOsTrader.ToString(),
                    Version = typeof(HeadlessProgram).Assembly.GetName().Version?.ToString(),
                    ProcessStarted = Process.GetCurrentProcess().StartTime,
                    IsMainWindowVisible = false
                });
                if (McpSettings.IsEnabled || Has(args, "--mcp-port"))
                    mcp.Start();
                else
                    Console.WriteLine("MCP API выключен в настройках (McpSettings.IsEnabled = false)");
            }

            Console.WriteLine("Роботы запущены. Ctrl+C — остановка.");
            Exit.Wait();

            Console.WriteLine("Остановка...");
            try { mcp?.Stop(); } catch (Exception ex) { Console.Error.WriteLine(ex); }
            // как MainWindow: снять флаг работы, дать потокам 5 секунд, завершить процесс
            // (потоки OsEngine не фоновые и сами процесс не отпускают)
            MainWindow.ProccesIsWorked = false;
            Thread.Sleep(5000);
            Console.WriteLine("Остановлено.");
            Console.Out.Flush();
            Environment.Exit(0);
            return 0;
        }

        private static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        private static bool Has(string[] args, string name) => Array.IndexOf(args, name) >= 0;
    }
}
