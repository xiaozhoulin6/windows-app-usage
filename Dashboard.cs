using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;

namespace AppUsage
{
    internal static class Palette
    {
        internal static readonly Brush Page = Make(0xF6F5F0);
        internal static readonly Brush Surface = Make(0xFFFEFA);
        internal static readonly Brush Ink = Make(0x172C26);
        internal static readonly Brush Muted = Make(0x53665D);
        internal static readonly Brush Accent = Make(0x18594B);
        internal static readonly Brush AccentSoft = Make(0xE1EAE1);
        internal static readonly Brush Line = Make(0xDCE3D8);
        internal static readonly Brush Warning = Make(0x8C462E);
        internal static readonly Brush WarningSoft = Make(0xF3E6D9);

        private static Brush Make(int rgb)
        {
            var brush = new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
            brush.Freeze();
            return brush;
        }
    }

    internal sealed class Dashboard : Window
    {
        private readonly UsageLog log;
        private readonly ApplicationIcons icons;
        private readonly TextBlock dateText;
        private readonly TextBlock totalText;
        private readonly TextBlock countText;
        private readonly TextBlock periodUnit;
        private readonly TextBlock statusText;
        private readonly Border statusPill;
        private readonly TextBlock note;
        private readonly TrendChart chart = new TrendChart();
        private readonly StackPanel appRows = new StackPanel();
        private readonly Button[] tabs = new Button[3];
        private DateTime lastRender = DateTime.MinValue;
        private int selectedRange;
        private string previousStatus;
        private string previousError;

        internal int SelectedRange { get { return selectedRange; } }

        internal Dashboard(UsageLog log, StartupSetting startup, ApplicationIcons icons, int initialRange)
        {
            this.log = log;
            this.icons = icons;
            selectedRange = initialRange;
            Title = "应用使用情况";
            Width = Math.Min(1000, SystemParameters.WorkArea.Width * 0.94);
            Height = Math.Min(820, SystemParameters.WorkArea.Height * 0.94);
            MinWidth = 575;
            MinHeight = 470;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Palette.Page;
            Foreground = Palette.Ink;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

            var root = new DockPanel { LastChildFill = true };
            Content = root;

            note = Text("关闭窗口后将在通知区域继续记录", 12, Palette.Muted);
            note.TextWrapping = TextWrapping.Wrap;
            var footer = new Border
            {
                BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(32, 10, 32, 10), Child = note
            };
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var scroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(32, 25, 32, 30),
                Focusable = false
            };
            root.Children.Add(scroll);

            var body = new StackPanel { MaxWidth = 1030 };
            scroll.Content = body;
            var header = TwoColumns();
            body.Children.Add(header);

            var title = Text("应用使用情况", 34, Palette.Ink, FontWeights.SemiBold);
            header.Children.Add(title);

            var headerActions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
            Grid.SetColumn(headerActions, 1);
            header.Children.Add(headerActions);
            statusText = Text("正在记录", 12, Palette.Accent, FontWeights.SemiBold);
            statusPill = new Border
            {
                Background = Palette.AccentSoft, CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11, 4, 11, 4),
                HorizontalAlignment = HorizontalAlignment.Right, Child = statusText
            };
            headerActions.Children.Add(statusPill);

            var startupToggle = new CheckBox
            {
                Content = "登录 Windows 后自动记录", Foreground = Palette.Muted, FontSize = 12,
                Cursor = System.Windows.Input.Cursors.Hand, Margin = new Thickness(0, 8, 0, 0),
                VerticalContentAlignment = VerticalAlignment.Center
            };
            bool enabled = false;
            try { enabled = startup.IsEnabled(); }
            catch (System.Security.SecurityException) { startupToggle.IsEnabled = false; }
            catch (UnauthorizedAccessException) { startupToggle.IsEnabled = false; }
            startupToggle.IsEnabled = startupToggle.IsEnabled && startup.Editable;
            startupToggle.IsChecked = enabled;
            startupToggle.ToolTip = "登录后在通知区域自动开始记录应用用时";
            startupToggle.Click += delegate
            {
                bool requested = startupToggle.IsChecked == true;
                try { startup.SetEnabled(requested); }
                catch (Exception e)
                {
                    if (!(e is System.Security.SecurityException) && !(e is UnauthorizedAccessException) &&
                        !(e is System.IO.IOException))
                        throw;

                    startupToggle.IsChecked = !requested;
                    MessageBox.Show(this, "Windows 未能更新自动记录设置。", Title,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            headerActions.Children.Add(startupToggle);

            var headingLine = TwoColumns();
            headingLine.Margin = new Thickness(0, 23, 0, 0);
            body.Children.Add(headingLine);
            var tabPanel = new StackPanel { Orientation = Orientation.Horizontal };
            headingLine.Children.Add(tabPanel);
            string[] periods = { "今天", "本周", "本月" };
            var tabStyle = (Style)XamlReader.Parse(
                "<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='Button'>" +
                "<Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Button'>" +
                "<Border x:Name='Frame' Padding='{TemplateBinding Padding}' BorderThickness='1' BorderBrush='{TemplateBinding BorderBrush}' " +
                "Background='{TemplateBinding Background}' CornerRadius='8'>" +
                "<ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Frame' " +
                "Property='Opacity' Value='0.84'/></Trigger></ControlTemplate.Triggers>" +
                "</ControlTemplate></Setter.Value></Setter></Style>");
            for (int i = 0; i < periods.Length; i++)
            {
                int range = i;
                var tab = new Button
                {
                    Style = tabStyle, Content = periods[i], MinWidth = 66, FontSize = 14,
                    Padding = new Thickness(11, 6, 11, 6), Margin = new Thickness(0, 0, 7, 0),
                    Cursor = System.Windows.Input.Cursors.Hand
                };
                tab.Click += delegate
                {
                    selectedRange = range;
                    Refresh();
                };
                tabs[i] = tab;
                tabPanel.Children.Add(tab);
            }

            dateText = Text("", 13, Palette.Muted);
            dateText.VerticalAlignment = VerticalAlignment.Center;
            dateText.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(dateText, 1);
            headingLine.Children.Add(dateText);

            var overview = TwoColumns(2, 1);
            overview.Margin = new Thickness(0, 14, 0, 0);
            body.Children.Add(overview);

            var timeMetric = new StackPanel();
            timeMetric.Children.Add(Text("总使用时长", 13, Palette.Accent, FontWeights.Medium));
            totalText = Text("0 分钟", 37, Palette.Ink, FontWeights.SemiBold);
            totalText.Margin = new Thickness(0, 4, 0, 0);
            timeMetric.Children.Add(totalText);

            var countMetric = new StackPanel();
            countMetric.Children.Add(Text("使用过的应用", 13, Palette.Muted, FontWeights.Medium));
            countText = Text("0 个", 37, Palette.Ink, FontWeights.SemiBold);
            countText.Margin = new Thickness(0, 4, 0, 0);
            countMetric.Children.Add(countText);

            Border timeMetricPanel = Panel(timeMetric, Palette.AccentSoft, new Thickness(19, 12, 19, 13));
            timeMetricPanel.Margin = new Thickness(0, 0, 10, 0);
            overview.Children.Add(timeMetricPanel);
            Border countMetricPanel = Panel(countMetric, Palette.Surface, new Thickness(19, 12, 19, 13));
            Grid.SetColumn(countMetricPanel, 1);
            overview.Children.Add(countMetricPanel);

            var trendPanel = new StackPanel();
            var trendHeading = TwoColumns();
            trendPanel.Children.Add(trendHeading);
            trendHeading.Children.Add(Text("使用趋势", 17, Palette.Ink, FontWeights.SemiBold));
            periodUnit = Text("", 12, Palette.Muted);
            periodUnit.VerticalAlignment = VerticalAlignment.Center;
            periodUnit.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(periodUnit, 1);
            trendHeading.Children.Add(periodUnit);
            chart.Height = 125;
            chart.Margin = new Thickness(0, 8, 0, 0);
            trendPanel.Children.Add(chart);
            Border trendCard = Panel(trendPanel, Palette.Surface, new Thickness(19, 13, 19, 8));
            trendCard.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(trendCard);

            var list = new StackPanel();
            list.Children.Add(Text("应用排行", 17, Palette.Ink, FontWeights.SemiBold));
            appRows.Margin = new Thickness(0, 6, 0, 0);
            list.Children.Add(appRows);
            Border listCard = Panel(list, Palette.Surface, new Thickness(19, 13, 19, 9));
            listCard.Margin = new Thickness(0, 12, 0, 0);
            body.Children.Add(listCard);

            Refresh();
        }

        internal void UpdateStatus(string status, string error)
        {
            if (status != previousStatus || error != previousError)
            {
                bool attention = error != null || status != "正在记录";
                statusText.Text = error != null ? "记录异常" : status ?? "正在记录";
                statusText.Foreground = attention ? Palette.Warning : Palette.Accent;
                statusPill.Background = attention ? Palette.WarningSoft : Palette.AccentSoft;
                note.Foreground = error != null ? Palette.Warning : Palette.Muted;
                note.Text = error ?? "关闭窗口后将在通知区域继续记录";
                previousStatus = status;
                previousError = error;
            }

            DateTime now = DateTime.Now;
            if ((now - lastRender).Duration().TotalSeconds >= 5 || now.Date != lastRender.Date)
                Refresh();
        }

        internal void Refresh()
        {
            lastRender = DateTime.Now;
            UsageReport report = log.GetReport(lastRender, selectedRange);
            const string shortDate = "M月d日";
            const string fullDate = "yyyy年M月d日";
            var dateCulture = CultureInfo.InvariantCulture;
            var lastDate = report.End.AddDays(-1);
            var rangeFormat = report.Start.Year == lastDate.Year ? shortDate : fullDate;
            dateText.Text = selectedRange == 0
                ? report.Start.ToString(fullDate, dateCulture)
                : report.Start.ToString(rangeFormat, dateCulture) + " – " + lastDate.ToString(rangeFormat, dateCulture);
            totalText.Text = FormatTime(report.Seconds);
            countText.Text = report.Apps.Length.ToString("N0") + " 个";
            periodUnit.Text = selectedRange == 0 ? "按小时" : "按天";
            chart.SetData(report.Trend, selectedRange);

            for (int i = 0; i < tabs.Length; i++)
            {
                bool selected = selectedRange == i;
                tabs[i].Background = selected ? Palette.Accent : Palette.Surface;
                tabs[i].BorderBrush = selected ? Palette.Accent : Palette.Line;
                tabs[i].Foreground = selected ? Brushes.White : Palette.Muted;
                tabs[i].FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
                AutomationProperties.SetHelpText(tabs[i], selected ? "当前选定的日期范围" : "切换日期范围");
            }

            appRows.Children.Clear();
            if (report.Apps.Length == 0)
            {
                var empty = Text("这段时间还没有使用记录", 15, Palette.Ink, FontWeights.Medium);
                empty.Margin = new Thickness(0, 7, 0, 3);
                appRows.Children.Add(empty);
                var detail = Text("软件从首次启动开始记录。切换到其他应用后，使用时长会陆续显示。", 13, Palette.Muted);
                detail.TextWrapping = TextWrapping.Wrap;
                detail.Margin = new Thickness(0, 0, 0, 6);
                appRows.Children.Add(detail);
                return;
            }

            foreach (var app in report.Apps)
            {
                double share = app.Seconds / report.Seconds;
                var row = new Grid();
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(77) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
                var identification = new DockPanel { Margin = new Thickness(0, 0, 12, 0) };
                var appIcon = AppIcon(icons.Get(app.Id));
                appIcon.Margin = new Thickness(0, 0, 9, 0);
                DockPanel.SetDock(appIcon, Dock.Left);
                identification.Children.Add(appIcon);
                var name = Text(app.Name, 14, Palette.Ink, FontWeights.Medium);
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                name.VerticalAlignment = VerticalAlignment.Center;
                name.ToolTip = app.Name;
                identification.Children.Add(name);
                row.Children.Add(identification);

                var proportion = new Grid { Height = 5, VerticalAlignment = VerticalAlignment.Center };
                proportion.Children.Add(new Border { Background = Palette.AccentSoft, CornerRadius = new CornerRadius(2.5) });
                proportion.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(share, GridUnitType.Star) });
                proportion.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - share, GridUnitType.Star) });
                Grid.SetColumnSpan(proportion.Children[0], 2);
                proportion.Children.Add(new Border { Background = Palette.Accent, CornerRadius = new CornerRadius(2.5) });
                Grid.SetColumn(proportion, 1);
                row.Children.Add(proportion);

                var percent = Text(share.ToString("0.#%"), 12, Palette.Muted);
                percent.HorizontalAlignment = HorizontalAlignment.Right;
                percent.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(percent, 2);
                row.Children.Add(percent);

                var duration = Text(FormatTime(app.Seconds), 13, Palette.Ink, FontWeights.SemiBold);
                duration.HorizontalAlignment = HorizontalAlignment.Right;
                duration.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(duration, 3);
                row.Children.Add(duration);

                appRows.Children.Add(new Border
                {
                    BorderBrush = Palette.Line, BorderThickness = new Thickness(0, 1, 0, 0),
                    Padding = new Thickness(0, 9, 0, 9), Child = row
                });
            }
        }

        private static FrameworkElement AppIcon(ImageSource source)
        {
            if (source != null)
            {
                var image = new Image
                {
                    Source = source, Width = 22, Height = 22,
                    Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                return image;
            }

            var glyph = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M 0,0 H 11 V 9 H 0 Z M 0,3 H 11"),
                Stroke = Palette.Muted, StrokeThickness = 1, StrokeLineJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            return new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(5),
                Background = Palette.AccentSoft, Child = glyph, IsHitTestVisible = false
            };
        }

        internal static string FormatTime(double seconds)
        {
            if (seconds <= 0)
                return "0 分钟";

            var elapsed = TimeSpan.FromSeconds(Math.Floor(seconds));
            if (elapsed.TotalMinutes < 1)
                return Math.Max(1, elapsed.Seconds) + " 秒";

            int hours = (int)elapsed.TotalHours;
            if (hours == 0)
                return elapsed.Minutes + " 分钟";

            return hours + " 小时" + (elapsed.Minutes == 0 ? "" : " " + elapsed.Minutes + " 分钟");
        }

        private static Grid TwoColumns(double left = 1, double right = 0)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = right == 0 ?
                GridLength.Auto : new GridLength(right, GridUnitType.Star) });
            return grid;
        }

        private static Border Panel(UIElement child, Brush background, Thickness padding)
        {
            return new Border
            {
                Background = background, BorderBrush = Palette.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Child = child, Padding = padding
            };
        }

        private static TextBlock Text(string text, double fontSize, Brush foreground, FontWeight? weight = null)
        {
            return new TextBlock
            {
                Text = text, FontSize = fontSize, Foreground = foreground,
                FontWeight = weight ?? FontWeights.Normal
            };
        }
    }

    internal sealed class TrendChart : FrameworkElement
    {
        private static readonly Typeface LabelTypeface = new Typeface("Segoe UI, Microsoft YaHei UI");
        private static readonly Pen GridLine = new Pen(Palette.Line, 1);
        private static readonly double[] ScalePresets = { 60, 120, 240, 600, 1200, 1800, 3600 };
        private double[] data = new double[0];
        private int range;
        private int previousTooltipIndex = -1;

        internal TrendChart()
        {
            var detail = new ToolTip();
            ToolTip = detail;
            MouseMove += delegate(object sender, System.Windows.Input.MouseEventArgs args)
            {
                if (data.Length == 0)
                    return;
                double x = args.GetPosition(this).X;
                int index = (int)Math.Floor((x - 59) * data.Length / (ActualWidth - 60));
                if (index >= 0 && index < data.Length && index != previousTooltipIndex)
                {
                    previousTooltipIndex = index;
                    detail.Content = FullLabel(index) + "  " + Dashboard.FormatTime(data[index]);
                }
            };
        }

        internal void SetData(double[] trend, int selectedRange)
        {
            data = trend;
            range = selectedRange;
            previousTooltipIndex = -1;
            AutomationProperties.SetName(this, "使用趋势");
            AutomationProperties.SetHelpText(this, string.Join("，", trend.Select((seconds, i) =>
                FullLabel(i) + " " + Dashboard.FormatTime(seconds))));
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (data.Length == 0 || ActualWidth <= 61 || ActualHeight <= 28)
                return;

            const double left = 59;
            const double top = 9;
            double baseLine = ActualHeight - 22;
            double height = baseLine - top;
            double plotWidth = ActualWidth - left - 1;
            double slot = plotWidth / data.Length;
            double peak = data.Max();
            double maximum = NiceCeiling(peak);
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
            for (int row = 0; row <= 2; row++)
            {
                double y = top + height * row / 2;
                dc.DrawLine(GridLine, new Point(left, y), new Point(ActualWidth - 1, y));
                var label = Label(row == 2 ? "0" : Dashboard.FormatTime(maximum * (2 - row) / 2));
                dc.DrawText(label, new Point(left - label.Width - 7, y - label.Height / 2));
            }

            for (int i = 0; i < data.Length; i++)
            {
                double center = left + (i + 0.5) * slot;
                double barWidth = Math.Min(37, slot * 0.64);
                if (data[i] > 0)
                {
                    double barHeight = Math.Max(2, height * data[i] / maximum);
                    dc.DrawRoundedRectangle(Palette.Accent, null,
                        new Rect(center - barWidth / 2, baseLine - barHeight, barWidth, barHeight), 3, 3);
                }

                string text = AxisLabel(i);
                if (text.Length > 0)
                {
                    var label = Label(text);
                    dc.DrawText(label, new Point(Math.Min(ActualWidth - label.Width, center - label.Width / 2), baseLine + 6));
                }
            }
        }

        private static double NiceCeiling(double peak)
        {
            foreach (double preset in ScalePresets)
                if (peak <= preset)
                    return preset;
            return Math.Ceiling(peak / 7200) * 7200;
        }

        private FormattedText Label(string text)
        {
            return new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                LabelTypeface, 11, Palette.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        }

        private string AxisLabel(int index)
        {
            if (range == 0)
                return index % 4 == 0 ? index.ToString("00") + ":00" : "";
            if (range == 1)
                return "一二三四五六日"[index].ToString();

            int day = index + 1;
            return day == 1 || day == 5 || day == 10 || day == 15 || day == 20 || day == 25 ||
                day == data.Length ? day.ToString() : "";
        }

        private string FullLabel(int index)
        {
            if (range == 0)
                return index.ToString("00") + ":00";
            if (range == 1)
                return "周" + "一二三四五六日"[index];
            return (index + 1) + " 日";
        }
    }
}
