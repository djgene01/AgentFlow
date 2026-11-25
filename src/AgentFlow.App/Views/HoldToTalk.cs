using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AgentFlow.App.ViewModels;

namespace AgentFlow.App.Views;

/// <summary>Makes a button a hold-to-talk test for the Voice page: press to start, release to finish.</summary>
public static class HoldToTalk
{
    public static readonly DependencyProperty VoiceProperty = DependencyProperty.RegisterAttached(
        "Voice", typeof(VoiceViewModel), typeof(HoldToTalk), new PropertyMetadata(null, OnVoiceChanged));

    public static VoiceViewModel? GetVoice(DependencyObject o) => (VoiceViewModel?)o.GetValue(VoiceProperty);
    public static void SetVoice(DependencyObject o, VoiceViewModel? value) => o.SetValue(VoiceProperty, value);

    private static void OnVoiceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Button b) return;
        b.PreviewMouseLeftButtonDown -= Down;
        b.PreviewMouseLeftButtonUp -= Up;
        b.LostMouseCapture -= Lost;
        if (e.NewValue is null) return;
        b.PreviewMouseLeftButtonDown += Down;
        b.PreviewMouseLeftButtonUp += Up;
        b.LostMouseCapture += Lost;
    }

    private static void Down(object sender, MouseButtonEventArgs e)
    {
        var b = (Button)sender;
        e.Handled = true;
        b.CaptureMouse();
        GetVoice(b)?.BeginTest();
    }

    private static void Up(object sender, MouseButtonEventArgs e)
    {
        var b = (Button)sender;
        e.Handled = true;
        if (b.IsMouseCaptured) b.ReleaseMouseCapture(); // Lost then finishes
        else GetVoice(b)?.EndTest();
    }

    private static void Lost(object sender, MouseEventArgs e) => GetVoice((Button)sender)?.EndTest();
}
