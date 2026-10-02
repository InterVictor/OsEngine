using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using OsEngine.OsTrader.Gui.RobotsVps;

namespace OsEngineVPS
{
    /// <summary>
    /// Main window of OsEngineVPS: a tab per VPS terminal with its Robots.VPS workspace (RobotsVpsLiteClone) and the
    /// "Settings" button for the VPS connection and administration window (RobotsVpsUi), which keeps the SSH tunnel
    /// and the MCP clients. Closing this window ends the program.
    /// </summary>
    public partial class MainUi : Window
    {
        private readonly RobotsVpsUi _settings;
        private readonly Dictionary<string, TabItem> _tabs = new Dictionary<string, TabItem>(StringComparer.OrdinalIgnoreCase);

        public MainUi()
        {
            InitializeComponent();

            // the connection window lives hidden for the whole run: it keeps the SSH tunnel and the MCP clients
            _settings = new RobotsVpsUi();
            Loaded += (s, e) =>
            {
                _settings.Owner = this;
                _settings.Show();
                _settings.Hide();

                if (RobotsVpsUi.IsAutoConnectOnStartupEnabled())
                {
                    _settings.StartAutomaticConnect();
                }
                else
                {
                    _settings.Show(); // nothing to connect to yet — show where to enter the VPS
                }
            };

            VpsRemoteSession.InstancesChanged += OnInstancesChanged;
            Closing += (s, e) =>
            {
                VpsRemoteSession.InstancesChanged -= OnInstancesChanged;
                OsEngine.MainWindow.ProccesIsWorked = false; // stops OsEngine's background loops, as on OsEngine exit
                _settings.ShutdownConnection();
                Application.Current.Shutdown();
            };

            UpdateTerminals();
        }

        private void ButtonSettings_Click(object sender, RoutedEventArgs e)
        {
            _settings.Show();
            _settings.Activate();
        }

        private void OnInstancesChanged() => Dispatcher.BeginInvoke(new Action(UpdateTerminals));

        // A tab appears for every terminal found on the VPS and stays while the program runs: on a reconnect the
        // workspace inside picks up its terminal again by name.
        private void UpdateTerminals()
        {
            IReadOnlyList<string> names = VpsRemoteSession.InstanceNames;

            foreach (string name in names.Where(n => !_tabs.ContainsKey(n)))
            {
                TabItem tab = new TabItem
                {
                    Header = name,
                    Content = new RobotsVpsLiteClone { InstanceName = name }
                };

                _tabs[name] = tab;
                TabControlTerminals.Items.Add(tab);
            }

            if (TabControlTerminals.SelectedItem == null && TabControlTerminals.Items.Count > 0)
            {
                TabControlTerminals.SelectedIndex = 0;
            }

            TextBlockConnection.Text = names.Count == 0
                ? "Not connected to the VPS — press Settings"
                : "Connected: " + string.Join(", ", names);
        }
    }
}
