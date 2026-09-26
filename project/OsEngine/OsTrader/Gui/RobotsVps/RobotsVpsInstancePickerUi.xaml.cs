using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>Asks which VPS terminal a new Robots.VPS workspace should show (only when there are several).</summary>
    public partial class RobotsVpsInstancePickerUi : Window
    {
        public string SelectedInstance { get; private set; }

        public RobotsVpsInstancePickerUi(IReadOnlyList<string> instances)
        {
            InitializeComponent();

            foreach (string name in instances)
            {
                ListBoxInstances.Items.Add(name);
            }

            if (ListBoxInstances.Items.Count > 0)
            {
                ListBoxInstances.SelectedIndex = 0;
            }
        }

        private void ButtonOpen_Click(object sender, RoutedEventArgs e) => Accept();

        private void ListBoxInstances_MouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();

        private void Accept()
        {
            if (ListBoxInstances.SelectedItem == null)
            {
                return;
            }

            SelectedInstance = ListBoxInstances.SelectedItem.ToString();
            DialogResult = true;
        }
    }
}
