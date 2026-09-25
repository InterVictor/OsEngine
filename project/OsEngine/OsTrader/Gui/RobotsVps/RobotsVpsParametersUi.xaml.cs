using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OsEngine.Entity;
using OsEngine.Language;
using OsEngine.Layout;
using OsEngine.MCP.Client;
using OsEngine.OsTrader.Panels;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    /// <summary>
    /// Remote parameter editor that uses the same ParamTabPainter and native parameter editors as
    /// StrategyParametersUi. The temporary parameter objects only paint/edit values on this client;
    /// all reads and writes still go through the VPS MCP connection.
    /// </summary>
    public partial class RobotsVpsParametersUi : Window
    {
        private readonly RemoteMcpClient _client;
        private readonly string _botId;
        private readonly List<ParameterTab> _tabs = new List<ParameterTab>();
        private readonly ParamGuiSettings _guiSettings = new ParamGuiSettings();
        private readonly List<string> _customTabNames = new List<string>();

        public RobotsVpsParametersUi(RemoteMcpClient client, string botId)
        {
            InitializeComponent();
            _client = client;
            _botId = botId;

            StickyBorders.Listen(this);
            StartupLocation.Start_MouseInCentre(this);

            Title = OsLocalization.Entity.TitleParametersUi + " / " + botId;
            ButtonAccept.Content = OsLocalization.Entity.ButtonAccept;
            ButtonUpdate.Content = OsLocalization.Entity.ButtonUpdate;
            ButtonSaveSettings.Content = OsLocalization.Entity.ButtonSave;
            ButtonLoadSettings.Content = OsLocalization.Entity.ButtonLoad;
            ButtonStrategyParameterPosts.Visibility = Visibility.Hidden;

            Closed += ParametersWindow_Closed;
            Loaded += async (s, e) => await LoadAsync();
            GlobalGUILayout.Listen(this, "botPanelParametersVps_" + botId);
        }

        private async System.Threading.Tasks.Task LoadAsync()
        {
            try
            {
                JsonElement result = await _client.CallToolAsync("bot_get_params", new { bot_id = _botId }).ConfigureAwait(true);
                JsonElement parameters = FindParametersArray(result);
                if (parameters.ValueKind != JsonValueKind.Array)
                {
                    ShowLoadMessage("The VPS API response did not contain a parameter list. Update/restart the MCP server on the VPS.");
                    return;
                }

                if (result.TryGetProperty("first_tab_label", out JsonElement firstTab)
                    && firstTab.ValueKind == JsonValueKind.String)
                    _guiSettings.FirstTabLabel = firstTab.GetString();
                ReadCustomTabNames(result);
                LoadParameterDesigns(result);

                if (result.TryGetProperty("window_title", out JsonElement titleValue)
                    && titleValue.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(titleValue.GetString()))
                    Title = titleValue.GetString() + " / " + _botId;

                if (result.TryGetProperty("window_width", out JsonElement widthValue)
                    && widthValue.TryGetDouble(out double width) && width >= MinWidth)
                    Width = width;
                if (result.TryGetProperty("window_height", out JsonElement heightValue)
                    && heightValue.TryGetDouble(out double height) && height >= MinHeight)
                    Height = height;

                List<IIStrategyParameter> remoteParameters = new List<IIStrategyParameter>();
                foreach (JsonElement item in parameters.EnumerateArray())
                {
                    IIStrategyParameter parameter = CreateParameter(item);
                    if (parameter != null)
                        remoteParameters.Add(parameter);
                }

                if (remoteParameters.Count == 0)
                {
                    if (_customTabNames.Count == 0)
                    {
                        ShowLoadMessage(parameters.GetArrayLength() == 0
                            ? "The VPS returned no strategy parameters for robot '" + _botId + "'."
                            : "The VPS returned parameters, but none used a supported parameter type.");
                        return;
                    }
                    CreateCustomTabPlaceholders();
                    TabControlSettings.SelectedIndex = 0;
                    return;
                }

                CreateParameterTabs(remoteParameters);
                CreateCustomTabPlaceholders();
                if (TabControlSettings.Items.Count > 0)
                    TabControlSettings.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not load robot parameters: " + ex.Message, "VPS");
            }
        }

        private IIStrategyParameter CreateParameter(JsonElement p)
        {
            if (p.ValueKind != JsonValueKind.Object
                || !p.TryGetProperty("type", out JsonElement typeElement)
                || !p.TryGetProperty("name", out JsonElement nameElement))
                return null;

            string type = typeElement.GetString();
            string name = nameElement.GetString() ?? string.Empty;
            string tabName = p.TryGetProperty("tab_name", out JsonElement tab)
                && tab.ValueKind == JsonValueKind.String ? tab.GetString() : null;

            switch (type)
            {
                case "Int":
                {
                    int value = ReadInt(p, "value", 0);
                    int start = ReadInt(p, "start", value);
                    int stop = ReadInt(p, "stop", value);
                    if (start > stop) { start = value; stop = value; }
                    return new StrategyParameterInt(name, value, start, stop, Math.Max(1, ReadInt(p, "step", 1)), tabName);
                }
                case "Decimal":
                {
                    decimal value = ReadDecimal(p, "value", 0m);
                    decimal start = ReadDecimal(p, "start", value);
                    decimal stop = ReadDecimal(p, "stop", value);
                    if (start > stop) { start = value; stop = value; }
                    return new StrategyParameterDecimal(name, value, start, stop,
                        Math.Max(0.00000001m, ReadDecimal(p, "step", 1m)), tabName);
                }
                case "String":
                {
                    string value = ReadString(p, "value", string.Empty);
                    List<string> values = new List<string>();
                    if (p.TryGetProperty("values", out JsonElement options) && options.ValueKind == JsonValueKind.Array)
                        values = options.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                            .Select(v => v.GetString()).ToList();
                    return new StrategyParameterString(name, value, values, tabName);
                }
                case "Bool":
                    return new StrategyParameterBool(name, ReadBool(p, "value"), tabName);
                case "TimeOfDay":
                {
                    string value = ReadString(p, "value", "00:00:00");
                    if (!TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan time))
                        TimeSpan.TryParse(value, CultureInfo.CurrentCulture, out time);
                    return new StrategyParameterTimeOfDay(name, time.Hours, time.Minutes, time.Seconds,
                        time.Milliseconds, tabName);
                }
                case "CheckBox":
                    return new StrategyParameterCheckBox(name,
                        string.Equals(ReadString(p, "value", "Unchecked"), "Checked", StringComparison.OrdinalIgnoreCase), tabName);
                case "DecimalCheckBox":
                {
                    decimal value = ReadDecimal(p, "value", 0m);
                    decimal start = ReadDecimal(p, "start", value);
                    decimal stop = ReadDecimal(p, "stop", value);
                    if (start > stop) { start = value; stop = value; }
                    bool isChecked = string.Equals(ReadString(p, "check_state", "Unchecked"), "Checked", StringComparison.OrdinalIgnoreCase);
                    return new StrategyParameterDecimalCheckBox(name, value, start, stop,
                        Math.Max(0.00000001m, ReadDecimal(p, "step", 1m)), isChecked, tabName);
                }
                case "Label":
                    return new StrategyParameterLabel(name, ReadString(p, "label", name),
                        ReadString(p, "value", string.Empty), ReadInt(p, "row_height", 25),
                        ReadInt(p, "text_height", 12), System.Drawing.Color.FromArgb(ReadInt(p, "color", System.Drawing.Color.Gray.ToArgb())), tabName);
                case "Button":
                {
                    StrategyParameterButton button = new StrategyParameterButton(name, tabName);
                    button.UserClickOnButtonEvent += () => ClickRemoteButton(name);
                    return button;
                }
                default:
                    return null;
            }
        }

        private void LoadParameterDesigns(JsonElement result)
        {
            if (!result.TryGetProperty("parameter_designs", out JsonElement designs)
                || designs.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement design in designs.EnumerateArray())
            {
                string type = ReadString(design, "design_type", string.Empty);
                string name = ReadString(design, "parameter_name", string.Empty);
                int colorValue = ReadInt(design, "color", System.Drawing.Color.Gray.ToArgb());
                int thickness = ReadInt(design, "thickness", 1);
                if (string.IsNullOrWhiteSpace(name)) continue;
                System.Drawing.Color color = System.Drawing.Color.FromArgb(colorValue);
                if (type == ParamDesignType.ForeColor.ToString())
                    _guiSettings.SetForeColorParameter(name, color);
                else if (type == ParamDesignType.BorderUnder.ToString())
                    _guiSettings.SetBorderUnderParameter(name, color, thickness);
                else if (type == ParamDesignType.SelectionColor.ToString())
                    _guiSettings.SetSelectionColorParameter(name, color);
            }
        }

        private void ReadCustomTabNames(JsonElement result)
        {
            if (!result.TryGetProperty("custom_tabs", out JsonElement tabs)
                || tabs.ValueKind != JsonValueKind.Array)
                return;

            foreach (JsonElement tab in tabs.EnumerateArray())
            {
                if (tab.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(tab.GetString()))
                    _customTabNames.Add(tab.GetString());
            }
        }

        private void CreateParameterTabs(List<IIStrategyParameter> parameters)
        {
            List<IIStrategyParameter> ungrouped = parameters.Where(p => string.IsNullOrWhiteSpace(p.TabName)).ToList();
            if (ungrouped.Count > 0)
                AddTab(ungrouped, string.IsNullOrWhiteSpace(_guiSettings.FirstTabLabel)
                    ? OsLocalization.Entity.TitleParametersUi : _guiSettings.FirstTabLabel);

            foreach (IGrouping<string, IIStrategyParameter> group in parameters
                .Where(p => !string.IsNullOrWhiteSpace(p.TabName)).GroupBy(p => p.TabName))
                AddTab(group.ToList(), group.Key);
        }

        private void AddTab(List<IIStrategyParameter> parameters, string label)
        {
            ParamTabPainter painter = new ParamTabPainter(parameters, label, TabControlSettings, _guiSettings);
            painter.ErrorEvent += Painter_ErrorEvent;
            _tabs.Add(new ParameterTab { Painter = painter, Parameters = parameters });
        }

        private void CreateCustomTabPlaceholders()
        {
            foreach (string tabName in _customTabNames)
            {
                TabControlSettings.Items.Add(new TabItem
                {
                    Header = tabName,
                    Content = new TextBlock
                    {
                        Text = "Remote custom panel content is not available yet.",
                        Margin = new Thickness(14),
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }
        }

        private async void ClickRemoteButton(string name)
        {
            try
            {
                await _client.CallToolAsync("bot_click_param_button",
                    new { bot_id = _botId, param_name = name }).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Parameter action failed: " + ex.Message, "VPS");
            }
        }

        private void ButtonStrategyParameterPosts_Click(object sender, RoutedEventArgs e)
        {
            // Robot-specific instruction links are not currently returned by the VPS parameter API.
        }

        private void ButtonUpdate_Click(object sender, RoutedEventArgs e) => _ = SaveParametersAsync();

        private void ButtonSaveSettings_Click(object sender, RoutedEventArgs e)
        {
            if (!CollectParameterValues(out Dictionary<string, object> values)) return;
            SaveFileDialog dialog = new SaveFileDialog
            {
                InitialDirectory = System.Windows.Forms.Application.StartupPath,
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                RestoreDirectory = true
            };
            if (dialog.ShowDialog() == true)
                File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(values, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void ButtonLoadSettings_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog dialog = new OpenFileDialog
            {
                InitialDirectory = System.Windows.Forms.Application.StartupPath,
                Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*",
                RestoreDirectory = true
            };
            if (dialog.ShowDialog() != true) return;
            try
            {
                Dictionary<string, JsonElement> values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(dialog.FileName));
                if (values == null) return;
                foreach (ParameterTab tab in _tabs)
                {
                    foreach (IIStrategyParameter parameter in tab.Parameters)
                        if (values.TryGetValue(parameter.Name, out JsonElement value))
                            ApplyWireValue(parameter, value);
                    tab.Painter.PaintTable();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not load parameter file: " + ex.Message, "VPS");
            }
        }

        private async void ButtonAccept_Click(object sender, RoutedEventArgs e)
        {
            if (await SaveParametersAsync()) Close();
        }

        private async System.Threading.Tasks.Task<bool> SaveParametersAsync()
        {
            if (!CollectParameterValues(out Dictionary<string, object> values)) return false;
            try
            {
                await _client.CallToolAsync("bot_set_params", new { bot_id = _botId, parameters = values }).ConfigureAwait(true);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not save VPS robot parameters: " + ex.Message, "VPS");
                return false;
            }
        }

        private bool CollectParameterValues(out Dictionary<string, object> values)
        {
            values = new Dictionary<string, object>();
            foreach (ParameterTab tab in _tabs)
            {
                tab.Painter.Save();
                foreach (IIStrategyParameter parameter in tab.Parameters)
                {
                    try
                    {
                        object value = ToWireValue(parameter);
                        if (value != null) values[parameter.Name] = value;
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("'" + parameter.Name + "': " + ex.Message, "VPS");
                        return false;
                    }
                }
            }
            return true;
        }

        private static object ToWireValue(IIStrategyParameter parameter)
        {
            switch (parameter.Type)
            {
                case StrategyParameterType.Int: return ((StrategyParameterInt)parameter).ValueInt;
                case StrategyParameterType.Decimal: return ((StrategyParameterDecimal)parameter).ValueDecimal;
                case StrategyParameterType.String: return ((StrategyParameterString)parameter).ValueString;
                case StrategyParameterType.Bool: return ((StrategyParameterBool)parameter).ValueBool;
                case StrategyParameterType.TimeOfDay:
                    TimeOfDay time = ((StrategyParameterTimeOfDay)parameter).Value;
                    return string.Format(CultureInfo.InvariantCulture, "{0:D2}:{1:D2}:{2:D2}", time.Hour, time.Minute, time.Second);
                case StrategyParameterType.CheckBox:
                    return ((StrategyParameterCheckBox)parameter).CheckState == System.Windows.Forms.CheckState.Checked;
                case StrategyParameterType.DecimalCheckBox:
                    StrategyParameterDecimalCheckBox decimalCheck = (StrategyParameterDecimalCheckBox)parameter;
                    return new Dictionary<string, object>
                    {
                        ["value"] = decimalCheck.ValueDecimal,
                        ["checked"] = decimalCheck.CheckState == System.Windows.Forms.CheckState.Checked
                    };
                default: return null;
            }
        }

        private static void ApplyWireValue(IIStrategyParameter parameter, JsonElement value)
        {
            switch (parameter.Type)
            {
                case StrategyParameterType.Int:
                    ((StrategyParameterInt)parameter).ValueInt = value.GetInt32();
                    break;
                case StrategyParameterType.Decimal:
                    ((StrategyParameterDecimal)parameter).ValueDecimal = ReadDecimal(value);
                    break;
                case StrategyParameterType.String:
                    ((StrategyParameterString)parameter).ValueString = value.GetString();
                    break;
                case StrategyParameterType.Bool:
                    ((StrategyParameterBool)parameter).ValueBool = value.GetBoolean();
                    break;
                case StrategyParameterType.CheckBox:
                    ((StrategyParameterCheckBox)parameter).CheckState = value.GetBoolean()
                        ? System.Windows.Forms.CheckState.Checked : System.Windows.Forms.CheckState.Unchecked;
                    break;
                case StrategyParameterType.DecimalCheckBox:
                    StrategyParameterDecimalCheckBox decimalCheck = (StrategyParameterDecimalCheckBox)parameter;
                    if (value.TryGetProperty("value", out JsonElement number)) decimalCheck.ValueDecimal = ReadDecimal(number);
                    if (value.TryGetProperty("checked", out JsonElement check))
                        decimalCheck.CheckState = check.GetBoolean()
                            ? System.Windows.Forms.CheckState.Checked : System.Windows.Forms.CheckState.Unchecked;
                    break;
                case StrategyParameterType.TimeOfDay:
                    if (TimeSpan.TryParse(value.GetString(), CultureInfo.InvariantCulture, out TimeSpan time))
                        ((StrategyParameterTimeOfDay)parameter).Value = new TimeOfDay
                        {
                            Hour = time.Hours, Minute = time.Minutes, Second = time.Seconds, Millisecond = time.Milliseconds
                        };
                    break;
            }
        }

        private static JsonElement FindParametersArray(JsonElement result)
        {
            if (result.ValueKind == JsonValueKind.Array)
            {
                if (result.GetArrayLength() > 0 && result[0].ValueKind == JsonValueKind.Object
                    && result[0].TryGetProperty("name", out _) && result[0].TryGetProperty("type", out _))
                    return result;
                foreach (JsonElement item in result.EnumerateArray())
                {
                    JsonElement found = FindParametersArray(item);
                    if (found.ValueKind == JsonValueKind.Array) return found;
                }
                return default;
            }
            if (result.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using JsonDocument nested = JsonDocument.Parse(result.GetString());
                    return FindParametersArray(nested.RootElement).Clone();
                }
                catch { return default; }
            }
            if (result.ValueKind != JsonValueKind.Object) return default;
            if (result.TryGetProperty("parameters", out JsonElement parameters))
            {
                if (parameters.ValueKind == JsonValueKind.Array) return parameters;
                JsonElement found = FindParametersArray(parameters);
                if (found.ValueKind == JsonValueKind.Array) return found;
            }
            foreach (string wrapper in new[] { "result", "data", "structuredContent", "content", "text" })
                if (result.TryGetProperty(wrapper, out JsonElement nested))
                {
                    JsonElement found = FindParametersArray(nested);
                    if (found.ValueKind == JsonValueKind.Array) return found;
                }
            return default;
        }

        private void ShowLoadMessage(string message)
        {
            TabControlSettings.Items.Clear();
            TabControlSettings.Items.Add(new System.Windows.Controls.TabItem
            {
                Header = OsLocalization.Entity.TitleParametersUi,
                Content = new System.Windows.Controls.TextBlock
                {
                    Text = message, Margin = new Thickness(14), TextWrapping = TextWrapping.Wrap
                }
            });
            TabControlSettings.SelectedIndex = 0;
        }

        private static int ReadInt(JsonElement p, string name, int fallback) =>
            p.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int parsed) ? parsed : fallback;

        private static decimal ReadDecimal(JsonElement p, string name, decimal fallback) =>
            p.TryGetProperty(name, out JsonElement value) ? ReadDecimal(value, fallback) : fallback;

        private static decimal ReadDecimal(JsonElement value, decimal fallback = 0m)
        {
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out decimal number)) return number;
            if (value.ValueKind == JsonValueKind.String
                && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) return number;
            return fallback;
        }

        private static bool ReadBool(JsonElement p, string name) =>
            p.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static string ReadString(JsonElement p, string name, string fallback) =>
            p.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback : fallback;

        private void Painter_ErrorEvent(string error) => MessageBox.Show(error, "VPS parameters");

        private void ParametersWindow_Closed(object sender, EventArgs e)
        {
            foreach (ParameterTab tab in _tabs)
            {
                tab.Painter.ErrorEvent -= Painter_ErrorEvent;
                tab.Painter.Dispose();
            }
            _tabs.Clear();
        }

        private sealed class ParameterTab
        {
            public ParamTabPainter Painter;
            public List<IIStrategyParameter> Parameters;
        }
    }
}
