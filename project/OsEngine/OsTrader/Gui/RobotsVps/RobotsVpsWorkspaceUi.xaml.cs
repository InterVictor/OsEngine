using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Independent VPS workspace using a full visual copy of RobotUiLite.
    /// This window intentionally contains no SSH/deployment settings and does not start a local engine.
    /// </summary>
    public partial class RobotsVpsWorkspaceUi : Window
    {
        public RobotsVpsWorkspaceUi()
        {
            InitializeComponent();
        }
    }
}
