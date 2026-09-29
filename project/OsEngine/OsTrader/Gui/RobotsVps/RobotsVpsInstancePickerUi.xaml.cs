using System.Collections.Generic;
using System.Windows;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>Asks which VPS terminal a new Robots.VPS workspace should show (only when there are several).</summary>
    public partial class RobotsVpsInstancePickerUi : Window
    {
        public string SelectedInstance { get; private set; }

        public RobotsVpsInstancePickerUi(IReadOnlyList<string> instances)
        {
            InitializeComponent();
            PanelInstances.ItemsSource = instances;
        }

        private void ButtonInstance_Click(object sender, RoutedEventArgs e)
        {
            if (!((sender as FrameworkElement)?.DataContext is string name))
            {
                return;
            }

            SelectedInstance = name;
            DialogResult = true;
        }
    }
}
