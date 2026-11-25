using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AgentFlow.App.Controls;

/// <summary>
/// Read-only, selectable Markdown. While text streams in, rendering is throttled so a long
/// answer re-parses at most about eight times a second. Mouse-wheel scrolling passes through
/// to the chat list instead of being swallowed by the inner text box.
/// </summary>
public sealed class MarkdownView : RichTextBox
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(
        nameof(Markdown), typeof(string), typeof(MarkdownView),
        new PropertyMetadata("", (d, _) => ((MarkdownView)d).ScheduleRender()));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private readonly DispatcherTimer _timer;
    private bool _renderedOnce;

    public MarkdownView()
    {
        IsReadOnly = true;
        IsDocumentEnabled = true; // hyperlinks clickable
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        FocusVisualStyle = null;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Document = new System.Windows.Documents.FlowDocument();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(120), DispatcherPriority.Background, (_, _) => RenderNow(), Dispatcher);
    }

    private void ScheduleRender()
    {
        if (!_renderedOnce)
        {
            RenderNow();
            return;
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void RenderNow()
    {
        _timer.Stop();
        _renderedOnce = true;
        var doc = MarkdownRenderer.Render(Markdown);
        doc.FontFamily = FontFamily;
        doc.FontSize = FontSize;
        doc.SetResourceReference(System.Windows.Documents.FlowDocument.ForegroundProperty, "TextFillColorPrimaryBrush");
        Document = doc;
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        // Bubble the wheel to the parent ScrollViewer.
        e.Handled = true;
        var forwarded = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = MouseWheelEvent, Source = this };
        (Parent as UIElement ?? VisualTreeHelper.GetParent(this) as UIElement)?.RaiseEvent(forwarded);
    }
}
