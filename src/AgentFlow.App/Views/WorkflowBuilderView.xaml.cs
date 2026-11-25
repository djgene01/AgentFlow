using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentFlow.App.ViewModels.Builder;
using AgentFlow.Core.Workflows;

namespace AgentFlow.App.Views;

public partial class WorkflowBuilderView : UserControl
{
    private Point _dragStart;
    private StageEditorViewModel? _dragItem;

    public WorkflowBuilderView() => InitializeComponent();

    private WorkflowEditorViewModel? Editor => (DataContext as WorkflowBuilderViewModel)?.Editor;

    // ---- Add stage menu ----

    private void AddStageButton_Click(object sender, RoutedEventArgs e)
    {
        if (Editor is not { } editor) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var t in StageTemplates.All)
        {
            var item = new MenuItem { Header = t.Label, ToolTip = t.Description, Command = editor.AddStageCommand, CommandParameter = t };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---- Insert a variable at the caret ----

    private void InsertVariable_Click(object sender, RoutedEventArgs e)
    {
        if (Editor?.SelectedStage is not { } stage) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var v in stage.InsertableVariables)
        {
            var item = new MenuItem { Header = v };
            item.Click += (_, _) => Insert(v);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void Insert(string text)
    {
        var box = FindChild<TextBox>(this, "PromptBox");
        if (box is null) return;
        var at = box.CaretIndex;
        box.Text = box.Text.Insert(at, text);
        box.CaretIndex = at + text.Length;
        box.Focus();
    }

    // ---- Drag to reorder stages ----

    private void StageList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragItem = (e.OriginalSource as DependencyObject) is { } src ? ItemFrom(src) : null;
    }

    private void StageList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(null) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        DragDrop.DoDragDrop(StageList, new DataObject(typeof(StageEditorViewModel), item), DragDropEffects.Move);
    }

    private void StageList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(StageEditorViewModel)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void StageList_Drop(object sender, DragEventArgs e)
    {
        if (Editor is not { } editor || e.Data.GetData(typeof(StageEditorViewModel)) is not StageEditorViewModel dragged) return;
        var target = (e.OriginalSource as DependencyObject) is { } src ? ItemFrom(src) : null;
        var from = editor.Stages.IndexOf(dragged);
        var to = target is null ? editor.Stages.Count - 1 : editor.Stages.IndexOf(target);
        editor.MoveTo(from, to);
    }

    private static StageEditorViewModel? ItemFrom(DependencyObject source)
    {
        for (var d = source; d is not null; d = VisualTreeHelper.GetParent(d))
            if (d is ListBoxItem { DataContext: StageEditorViewModel vm }) return vm;
        return null;
    }

    private static T? FindChild<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T fe && fe.Name == name) return fe;
            if (FindChild<T>(child, name) is { } found) return found;
        }
        return null;
    }
}
