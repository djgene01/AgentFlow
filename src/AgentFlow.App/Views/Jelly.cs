using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace AgentFlow.App.Views;

/// <summary>
/// A soft, jelly-like bounce for sidebar items: a small swell on hover, a squash on press and a wobbly spring
/// back on release. The motion is short and small so it feels alive without pulling the eye. It is off when
/// Windows' "Show animations" setting is off.
/// </summary>
public static class Jelly
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(Jelly), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject o) => (bool)o.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject o, bool value) => o.SetValue(EnabledProperty, value);

    /// <summary>How far it swells on hover (1.0 = not at all).</summary>
    public static readonly DependencyProperty HoverScaleProperty = DependencyProperty.RegisterAttached(
        "HoverScale", typeof(double), typeof(Jelly), new PropertyMetadata(1.025));

    public static double GetHoverScale(DependencyObject o) => (double)o.GetValue(HoverScaleProperty);
    public static void SetHoverScale(DependencyObject o, double value) => o.SetValue(HoverScaleProperty, value);

    /// <summary>Marks the sidebar entry for the page that is open (styles use it for the highlight).</summary>
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.RegisterAttached(
        "IsActive", typeof(bool), typeof(Jelly), new FrameworkPropertyMetadata(false, OnIsActiveChanged));

    public static bool GetIsActive(DependencyObject o) => (bool)o.GetValue(IsActiveProperty);
    public static void SetIsActive(DependencyObject o, bool value) => o.SetValue(IsActiveProperty, value);

    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement el) return;
        if (e.NewValue is true)
        {
            el.RenderTransformOrigin = new Point(0.5, 0.5);
            el.RenderTransform = new ScaleTransform(1, 1);
            el.MouseEnter += OnEnter;
            el.MouseLeave += OnLeave;
            el.PreviewMouseLeftButtonDown += OnDown;
            el.PreviewMouseLeftButtonUp += OnUp;
        }
        else
        {
            el.MouseEnter -= OnEnter;
            el.MouseLeave -= OnLeave;
            el.PreviewMouseLeftButtonDown -= OnDown;
            el.PreviewMouseLeftButtonUp -= OnUp;
        }
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // A little "boing" when an entry becomes the open page, even if it was picked by keyboard.
        // A click already squashes and springs it, so only keyboard and code changes get this one.
        if (e.NewValue is true && d is FrameworkElement el && GetEnabled(el) && el.IsLoaded && Mouse.LeftButton != MouseButtonState.Pressed)
            Wobble(el, 1.06, 0.94);
    }

    private static void OnEnter(object sender, MouseEventArgs e)
    {
        var el = (FrameworkElement)sender;
        var s = GetHoverScale(el);
        To(el, s, s, Spring(oscillations: 1, springiness: 6), 420);
    }

    private static void OnLeave(object sender, MouseEventArgs e) =>
        To((FrameworkElement)sender, 1, 1, Spring(oscillations: 1, springiness: 6), 420);

    private static void OnDown(object sender, MouseButtonEventArgs e) =>
        // Squash: wider and shorter, like pressing on jelly.
        To((FrameworkElement)sender, 1.04, 0.90, new QuadraticEase { EasingMode = EasingMode.EaseOut }, 90);

    private static void OnUp(object sender, MouseButtonEventArgs e)
    {
        var el = (FrameworkElement)sender;
        var s = el.IsMouseOver ? GetHoverScale(el) : 1;
        To(el, s, s, Spring(oscillations: 3, springiness: 3), 650);
    }

    /// <summary>Stretches the element and lets it wobble back to rest.</summary>
    public static void Wobble(FrameworkElement el, double x, double y)
    {
        if (!Animate || el.RenderTransform is not ScaleTransform st) return;
        var rest = el.IsMouseOver ? GetHoverScale(el) : 1;
        To(el, rest, rest, Spring(oscillations: 3, springiness: 3), 700, x, y);
    }

    private static ElasticEase Spring(int oscillations, double springiness) =>
        new() { EasingMode = EasingMode.EaseOut, Oscillations = oscillations, Springiness = springiness };

    private static void To(FrameworkElement el, double x, double y, IEasingFunction ease, int ms, double? fromX = null, double? fromY = null)
    {
        if (el.RenderTransform is not ScaleTransform st) return;
        if (!Animate)
        {
            st.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            return;
        }
        var duration = TimeSpan.FromMilliseconds(ms);
        // Height springs a touch behind width, which is what makes it read as jelly rather than a zoom.
        st.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(x, duration) { From = fromX, EasingFunction = ease });
        st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(y, duration + TimeSpan.FromMilliseconds(60))
        {
            From = fromY,
            EasingFunction = ease,
            BeginTime = TimeSpan.FromMilliseconds(fromY is null ? 25 : 0),
        });
    }
}
