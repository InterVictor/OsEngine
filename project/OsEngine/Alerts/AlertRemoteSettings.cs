/* Your rights to use code governed by https://github.com/AlexWan/OsEngine/blob/master/LICENSE */
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Reflection;
using System.Text.Json;
namespace OsEngine.Alerts
{
    public static class AlertRemoteSettings
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { IncludeFields = true };
        public static Dictionary<string, object> Export(IIAlert alert)
        {
            Dictionary<string, object> result = new Dictionary<string, object> { ["IsOn"] = alert.IsOn };
            foreach (FieldInfo field in alert.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                object value = field.GetValue(alert);
                result[field.Name] = value is Color color ? color.ToArgb() : value is Enum ? value.ToString() :
                    value is ChartAlertLine[] ? JsonSerializer.SerializeToElement(value, typeof(ChartAlertLine[]), Options) : value;
            }
            return result;
        }
        public static IIAlert Create(string type, JsonElement settings)
        {
            IIAlert alert = type == "PriceAlert" ? new AlertToPrice("VpsDraft_" + Guid.NewGuid().ToString("N")) :
                type == "ChartAlert" ? new AlertToChart(null) : throw new ArgumentException("Unknown alert type");
            alert.IsOn = settings.GetProperty("IsOn").GetBoolean();
            foreach (FieldInfo field in alert.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!settings.TryGetProperty(field.Name, out JsonElement value)) continue;
                Type target = field.FieldType;
                object parsed;
                if (target == typeof(Color)) parsed = Color.FromArgb(value.GetInt32());
                else if (target.IsEnum)
                {
                    parsed = Enum.Parse(target, value.GetString());
                    if (!Enum.IsDefined(target, parsed)) throw new ArgumentException("Invalid " + field.Name);
                }
                else parsed = JsonSerializer.Deserialize(value.GetRawText(), target, Options);
                field.SetValue(alert, parsed);
            }
            if (alert is AlertToPrice price && (price.PriceActivation <= 0 || price.VolumeReaction < 0 || price.Slippage < 0))
                throw new ArgumentException("Invalid price alert values");
            if (alert is AlertToChart chart && (chart.Lines == null || chart.Lines.Length == 0 || chart.Lines.Length > 100 || chart.BorderWidth < 1 || chart.BorderWidth > 20))
                throw new ArgumentException("Invalid chart alert lines or width");
            return alert;
        }
    }
}
