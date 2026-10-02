using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using OsEngine.Alerts;
using OsEngine.Language;

namespace OsEngineVPS
{
    /// <summary>
    /// OsEngineVPS: the VPS connection and administration window as a program of its own. It runs OsEngine.App (its
    /// resources hold the themes, window styles and their handlers) with the VPS window as the main window instead
    /// of OsEngine's MainWindow: no local engine, no MCP host, no connectors are started here.
    /// </summary>
    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            // OsEngine keeps its settings in Engine\ next to the program
            Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);
            Directory.CreateDirectory("Engine");

            // SSH.NET leaves an internal task with the error when the connection drops in the middle of a command;
            // our call has handled it already and the tunnel reconnects — do not let it crash the program
            TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                if (e.Exception != null && e.Exception.InnerExceptions.Count > 0
                    && e.Exception.InnerExceptions.All(ex => ex is Renci.SshNet.Common.SshException))
                {
                    e.SetObserved();
                }
            };

            OsEngine.App app = new OsEngine.App();
            app.InitializeComponent();
            // the main window of this program instead of OsEngine's MainWindow
            app.StartupUri = new Uri("pack://application:,,,/OsEngineVPS;component/MainUi.xaml");

            app.Startup += (s, e) =>
            {
                Thread.CurrentThread.CurrentCulture = OsLocalization.CurCulture;

                // What OsEngine's MainWindow sets on start: its background loops (the chart painter and ~50 others)
                // run only while this flag is up — without it the charts stayed empty
                OsEngine.MainWindow.ProccesIsWorked = true;
                OsEngine.MainWindow.DebuggerIsWork = System.Diagnostics.Debugger.IsAttached;

                // alerts raised by the remote terminals are shown in the usual alert window
                AlertMessageManager.TextBoxFromStaThread = new System.Windows.Controls.TextBox();
            };

            app.Run();
        }
    }
}
