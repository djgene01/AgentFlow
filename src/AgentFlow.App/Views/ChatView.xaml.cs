using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AgentFlow.App.ViewModels;

namespace AgentFlow.App.Views;

public partial class ChatView : UserControl
{
    private ScrollViewer? _scroll;
    private bool _stickToBottom = true;
    private ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) =>
        {
            _scroll ??= FindScrollViewer(Timeline);
            if (_scroll is not null) _scroll.ScrollChanged += OnScrollChanged;
            ScrollToEnd();
            DraftBox.Focus();
            if (Window.GetWindow(this) is { } w)
            {
                w.Deactivated -= OnWindowDeactivated;
                w.Deactivated += OnWindowDeactivated;
            }
        };
        Unloaded += (_, _) => { if (_dictation is not null) _ = _vm?.Voice?.EndAsync(); };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewKeyUp += OnPreviewKeyUp;
    }

    // ---- push-to-talk ----

    private Dictation? _dictation;

    /// <summary>Puts dictated text at the caret: the text before and after it stays as it was.</summary>
    private sealed class Dictation(ChatView view, string before, string after, string? folder, bool fromKey) : AgentFlow.App.ViewModels.IDictationTarget
    {
        public bool FromKey { get; } = fromKey;
        public string? ProjectFolder => folder;
        private readonly string _before = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? before + " " : before;
        private readonly string _after = after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " + after : after;
        private readonly string _original = before + after;

        public void ShowLive(string text) => Put(text);

        public void Finish(string text)
        {
            if (text.Length == 0) view.SetDraft(_original, before.Length);
            else Put(text);
            view.OnDictationFinished(this, text);
        }

        private void Put(string text) => view.SetDraft(_before + text + _after, _before.Length + text.Length);
    }

    private void SetDraft(string text, int caret)
    {
        if (_vm is null) return;
        _vm.Draft = text;
        DraftBox.CaretIndex = Math.Min(caret, DraftBox.Text.Length);
        DraftBox.ScrollToEnd();
    }

    private async void StartDictation(bool fromKey)
    {
        if (_vm?.Voice is not { } voice || _dictation is not null || voice.IsBusy) return;
        var caret = Math.Min(DraftBox.CaretIndex, _vm.Draft.Length);
        var d = new Dictation(this, _vm.Draft[..caret], _vm.Draft[caret..], _vm.FolderPath, fromKey);
        _dictation = d;
        DraftBox.IsReadOnly = true;
        VoiceBar.Visibility = Visibility.Visible;
        if (!await voice.BeginAsync(d) && _dictation == d) EndUi();
    }

    private void FinishDictation()
    {
        if (_dictation is null || _vm?.Voice is not { } voice) return;
        _ = voice.EndAsync(); // Finish is called back with the text
    }

    private void CancelDictation()
    {
        if (_dictation is null || _vm?.Voice is not { } voice) return;
        _ = voice.CancelAsync();
    }

    private void OnDictationFinished(Dictation d, string text)
    {
        if (_dictation != d) return;
        EndUi();
        DraftBox.Focus();
        _vm?.Voice?.NoteDictated(_vm.ProjectId, text);
        if (text.Length > 0 && _vm?.Voice?.SendOnRelease == true && _vm.SendCommand.CanExecute(null))
        {
            _stickToBottom = true;
            _vm.SendCommand.Execute(null);
        }
    }

    private void EndUi()
    {
        _dictation = null;
        DraftBox.IsReadOnly = false;
        VoiceBar.Visibility = Visibility.Collapsed;
    }

    private bool IsPushToTalkDown(KeyEventArgs e) => _vm?.Voice?.Key switch
    {
        PushToTalkKey.RightCtrl => e.Key == Key.RightCtrl,
        PushToTalkKey.CtrlSpace => e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) != 0,
        PushToTalkKey.F8 => e.Key == Key.F8,
        _ => false,
    };

    private bool IsPushToTalkUp(KeyEventArgs e) => _vm?.Voice?.Key switch
    {
        PushToTalkKey.RightCtrl => e.Key == Key.RightCtrl,
        PushToTalkKey.CtrlSpace => e.Key == Key.Space,
        PushToTalkKey.F8 => e.Key == Key.F8,
        _ => false,
    };

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _vm?.Voice is { IsSpeaking: true } speaking)
        {
            speaking.StopSpeaking();
            e.Handled = true;
            return;
        }
        if (_vm?.Voice is not { IsEnabled: true }) return;
        if (_dictation is { FromKey: true })
        {
            if (e.IsRepeat || IsPushToTalkDown(e)) { e.Handled = true; return; }
            // Right Ctrl held with another key is a shortcut (Ctrl+L and so on), not talking.
            if (_vm.Voice.Key == PushToTalkKey.RightCtrl) CancelDictation();
            return;
        }
        if (!e.IsRepeat && IsPushToTalkDown(e))
        {
            if (_vm.Voice.Key != PushToTalkKey.RightCtrl) e.Handled = true;
            StartDictation(fromKey: true);
        }
    }

    private void OnPreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (_dictation is { FromKey: true } && IsPushToTalkUp(e))
        {
            e.Handled = true;
            FinishDictation();
        }
    }

    private void OnWindowDeactivated(object? sender, EventArgs e)
    {
        if (_dictation is not null) FinishDictation(); // the key-up would go to another window
    }

    private void MicButton_Down(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        MicButton.CaptureMouse();
        StartDictation(fromKey: false);
    }

    private void MicButton_Up(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (MicButton.IsMouseCaptured) MicButton.ReleaseMouseCapture(); // LostMouseCapture finishes
        else FinishDictation();
    }

    private void MicButton_LostCapture(object sender, MouseEventArgs e)
    {
        if (_dictation is { FromKey: false }) FinishDictation();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null)
        {
            _vm.ContentGrew -= OnContentGrew;
            _vm.ScrollToEntry -= OnScrollToEntry;
            _vm.FocusRequested -= OnFocusRequested;
        }
        _vm = DataContext as ChatViewModel;
        if (_vm is not null)
        {
            _vm.ContentGrew += OnContentGrew;
            _vm.ScrollToEntry += OnScrollToEntry;
            _vm.FocusRequested += OnFocusRequested;
        }
        _stickToBottom = true;
        ScrollToEnd();
    }

    private void OnScrollToEntry(EntryViewModel entry)
    {
        _stickToBottom = false; // reading an older match should not jump back down
        Timeline.ScrollIntoView(entry);
    }

    private void OnFocusRequested(bool search) =>
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (search) { SearchBox.Focus(); SearchBox.SelectAll(); }
            else DraftBox.Focus();
        });

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Escape) { _vm.CloseSearchCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Shift) { _vm.PreviousMatchCommand.Execute(null); e.Handled = true; }
        else if (e.Key == Key.Enter) { _vm.NextMatchCommand.Execute(null); e.Handled = true; }
    }

    private void OnContentGrew()
    {
        if (_stickToBottom) ScrollToEnd();
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Follow new output only while the user is at the bottom; scrolling up pauses it.
        if (e.ExtentHeightChange == 0)
            _stickToBottom = _scroll!.VerticalOffset >= _scroll.ScrollableHeight - 24;
        else if (_stickToBottom)
            _scroll!.ScrollToEnd();
    }

    private void ScrollToEnd() =>
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => (_scroll ?? FindScrollViewer(Timeline))?.ScrollToEnd());

    private void DraftBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            if (_vm?.SendCommand.CanExecute(null) == true)
            {
                _stickToBottom = true;
                _vm.SendCommand.Execute(null);
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        }
        return null;
    }
}
