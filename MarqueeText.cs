using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace SpectrumWidget;

/// <summary>
/// 单行文字，放不下时自动循环滚动（先停一会儿再滚，首尾相接），两侧渐隐。
/// 字体用 TextElement.FontSize / FontWeight 等继承属性设置。
/// </summary>
sealed class MarqueeText : Grid
{
    const double Gap = 48;          // 首尾之间的空白
    const double Speed = 35;        // 像素 / 秒
    const double PauseSeconds = 2.5;

    readonly TextBlock _sizer = new() { Text = "Ag字", Visibility = Visibility.Hidden };
    readonly TextBlock _a = new();
    readonly TextBlock _b = new();
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };
    readonly TranslateTransform _shift = new();
    double _lastWidth = -1, _lastText = -1;

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(MarqueeText),
        new PropertyMetadata("", (d, _) => ((MarqueeText)d).OnTextChanged()));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public MarqueeText()
    {
        ClipToBounds = true;
        _row.Children.Add(_a);
        _row.Children.Add(new Border { Width = Gap });
        _row.Children.Add(_b);
        _row.RenderTransform = _shift;
        var canvas = new Canvas();
        canvas.Children.Add(_row);
        Children.Add(_sizer);   // 只用来撑出行高
        Children.Add(canvas);
        SizeChanged += (_, _) => Refresh(force: false);
    }

    void OnTextChanged()
    {
        _a.Text = _b.Text = Text ?? "";
        Refresh(force: true);
    }

    void Refresh(bool force)
    {
        if (ActualWidth <= 0) return;
        _a.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = _a.DesiredSize.Width, avail = ActualWidth;
        if (!force && Math.Abs(avail - _lastWidth) < 0.5 && Math.Abs(textW - _lastText) < 0.5) return;
        _lastWidth = avail; _lastText = textW;

        _shift.BeginAnimation(TranslateTransform.XProperty, null);
        _shift.X = 0;

        if (textW <= avail + 0.5)
        {
            _b.Visibility = Visibility.Collapsed;
            OpacityMask = null;
            return;
        }

        _b.Visibility = Visibility.Visible;
        double dist = textW + Gap;
        double scroll = dist / Speed;
        var (mask, leftStop) = EdgeFade(avail);
        OpacityMask = mask;

        // 左侧渐隐只在滚动时出现，停顿时第一个字完整显示
        double total = PauseSeconds + scroll, ramp = Math.Min(0.35, scroll / 4);
        var fade = new ColorAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        fade.KeyFrames.Add(new DiscreteColorKeyFrame(Colors.Black, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearColorKeyFrame(Colors.Black, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds))));
        fade.KeyFrames.Add(new LinearColorKeyFrame(Colors.Transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds + ramp))));
        fade.KeyFrames.Add(new LinearColorKeyFrame(Colors.Transparent, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(total - ramp))));
        fade.KeyFrames.Add(new LinearColorKeyFrame(Colors.Black, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(total))));
        leftStop.BeginAnimation(GradientStop.ColorProperty, fade);
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-dist, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(PauseSeconds + scroll))));
        _shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    static (Brush mask, GradientStop leftStop) EdgeFade(double width)
    {
        double left = Math.Min(0.12, 14 / Math.Max(width, 1));
        double right = Math.Min(0.18, 22 / Math.Max(width, 1));
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        var leftStop = new GradientStop(Colors.Black, 0);
        g.GradientStops.Add(leftStop);
        g.GradientStops.Add(new GradientStop(Colors.Black, left));
        g.GradientStops.Add(new GradientStop(Colors.Black, 1 - right));
        g.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        return (g, leftStop);
    }
}
