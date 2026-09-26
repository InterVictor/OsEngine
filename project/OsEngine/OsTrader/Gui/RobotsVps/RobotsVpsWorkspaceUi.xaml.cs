using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Independent VPS workspace using a full visual copy of RobotUiLite.
    /// This window intentionally contains no SSH/deployment settings and does not start a local engine.
    /// It shows one VPS terminal (VpsRemoteSession instance name).
    /// </summary>
    public partial class RobotsVpsWorkspaceUi : Window
    {
        public RobotsVpsWorkspaceUi() : this(VpsRemoteSession.MainInstance)
        {
        }

        public RobotsVpsWorkspaceUi(string instanceName)
        {
            InitializeComponent();
            LiteClone.InstanceName = instanceName;
        }
    }
}
