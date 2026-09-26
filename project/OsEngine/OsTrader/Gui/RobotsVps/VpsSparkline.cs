// Small line chart for the VPS monitoring panel: 0–100 % scale, last N samples, dashed alarm threshold.
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace OsEngine.OsTrader.Gui.RobotsVps
{
    public sealed class VpsSparkline : Canvas
    {
        // 360 samples x 10 s = the last hour
        public int Capacity { get; set; } = 360;

        public double Threshold { get; set; } = 90;

        private readonly Queue<double> _values = new Queue<double>();
        private readonly Polyline _line = new Polyline { StrokeThickness = 1.5 };
        private readonly Line _threshold = new Line { StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 3, 3 } };

        public VpsSparkline()
        {
            ClipToBounds = true;
            Children.Add(_threshold);
            Children.Add(_line);
            SizeChanged += (s, e) => Redraw();
            Loaded += (s, e) =>
            {
                // theme colors: the same brushes the rest of the window uses
                Background = TryFindResource("PanelAltBrush") as Brush ?? Brushes.Transparent;
                _line.Stroke = TryFindResource("ChartEquityBrush") as Brush ?? Brushes.DeepSkyBlue;
                _threshold.Stroke = Brushes.IndianRed;
                Redraw();
            };
        }

        public void Add(double percent)
        {
            if (double.IsNaN(percent)) return;

            _values.Enqueue(System.Math.Max(0, System.Math.Min(100, percent)));
            while (_values.Count > Capacity) _values.Dequeue();
            Redraw();
        }

        public void Clear()
        {
            _values.Clear();
            Redraw();
        }

        private void Redraw()
        {
            double width = ActualWidth;
            double height = ActualHeight;

            if (width <= 0 || height <= 0)
            {
                return;
            }

            double thresholdY = height - height * Threshold / 100.0;
            _threshold.X1 = 0;
            _threshold.X2 = width;
            _threshold.Y1 = thresholdY;
            _threshold.Y2 = thresholdY;

            PointCollection points = new PointCollection();
            double step = Capacity > 1 ? width / (Capacity - 1) : width;
            double x = width - step * (_values.Count - 1);

            foreach (double value in _values)
            {
                points.Add(new Point(x, height - height * value / 100.0));
                x += step;
            }

            _line.Points = points;
        }
    }
}
