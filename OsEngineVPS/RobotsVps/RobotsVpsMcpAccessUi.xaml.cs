using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using OsEngine.Entity;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Folders whose AI agent sessions get the VPS terminals (through their .mcp.json): shows what each folder sees,
    /// adds and removes folders.
    /// </summary>
    public partial class RobotsVpsMcpAccessUi : Window
    {
        public sealed class FolderRow
        {
            public string Folder { get; set; }
            public string Terminals { get; set; }
        }

        private readonly List<string> _folders;
        private readonly Action _changed;

        // folders: the VPS window's list, edited in place; changed: saves it and writes the terminals right away
        internal RobotsVpsMcpAccessUi(List<string> folders, Action changed)
        {
            InitializeComponent();
            _folders = folders;
            _changed = changed;
            Render();
        }

        private void Render()
        {
            DataGridFolders.ItemsSource = _folders.Select(f => new FolderRow { Folder = f, Terminals = Describe(f) }).ToList();
            LabelStatus.Content = _folders.Count == 0 ? "No folders — no MCP access to the VPS" : _folders.Count + " folder(s)";
        }

        private static string Describe(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return "folder not found";
                List<string> terminals = McpJsonConfig.ListTerminals(folder);
                return terminals.Count == 0 ? "none yet — connect to the VPS" : string.Join(", ", terminals);
            }
            catch (Exception ex)
            {
                return ".mcp.json unreadable: " + ex.Message;
            }
        }

        private void ButtonAdd_Click(object sender, RoutedEventArgs e)
        {
            OpenFolderDialog dialog = new OpenFolderDialog { Title = "Project folder of the AI agent (where its .mcp.json lives)" };

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            string folder = dialog.FolderName.TrimEnd('\\');

            if (_folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            _folders.Add(folder);
            _changed();
            Render();
        }

        private void ButtonRemove_Click(object sender, RoutedEventArgs e)
        {
            if (!(DataGridFolders.SelectedItem is FolderRow row))
            {
                MessageBox.Show("Select a folder first");
                return;
            }

            AcceptDialogUi confirm = new AcceptDialogUi(
                $"Remove MCP access to the VPS from\n{row.Folder}?\n\nThe VPS terminals are taken out of its .mcp.json; "
                + "other settings of that file stay. Sessions already open there keep access until they are closed.");
            confirm.ShowDialog();

            if (!confirm.UserAcceptAction)
            {
                return;
            }

            try
            {
                if (Directory.Exists(row.Folder)) McpJsonConfig.RemoveAll(row.Folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not change .mcp.json: " + ex.Message);
                return;
            }

            _folders.RemoveAll(f => string.Equals(f, row.Folder, StringComparison.OrdinalIgnoreCase));
            _changed();
            Render();
        }
    }
}
