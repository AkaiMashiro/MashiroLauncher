using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MashiroLauncher.App.ViewModels;

namespace MashiroLauncher.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    public MainWindow(MainWindowViewModel viewModel) : this()
    {
        DataContext = viewModel;
    }

    /// <summary>
    /// Wired to the inline-name TextBox on each instance card. Watches for the
    /// IsVisible flip that happens when the user clicks the name (BeginRename)
    /// and auto-focuses + selects the text so they can type immediately.
    /// </summary>
    private void InlineNameBox_AttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // AttachedToVisualTree can fire repeatedly under ScrollViewer
        // virtualization, so use -=/+= for both handlers to avoid stacking
        // duplicates (slow memory growth + redundant focus/commit calls).
        tb.PropertyChanged -= InlineNameBox_PropertyChanged;
        tb.PropertyChanged += InlineNameBox_PropertyChanged;
        tb.LostFocus -= InlineNameBox_LostFocus;
        tb.LostFocus += InlineNameBox_LostFocus;
    }

    private static void InlineNameBox_PropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs ev)
    {
        if (sender is not TextBox tb) return;
        if (ev.Property != Visual.IsVisibleProperty || !tb.IsVisible) return;

        // Defer to the dispatcher so layout settles before we steal focus —
        // without this, clicking the name Button consumes its focus event
        // before our Focus() call lands.
        Dispatcher.UIThread.Post(() =>
        {
            tb.Focus();
            tb.SelectAll();
        });
    }

    private static void InlineNameBox_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.DataContext is not InstanceItemViewModel vm) return;
        if (!vm.IsRenaming) return;  // already committed via Enter/Esc
        vm.ConfirmRenameCommand.Execute(null);
    }

    /// <summary>
    /// Bubble-phase fallback for LostFocus: clicks on non-focusable elements
    /// (Border background, empty space inside a ScrollViewer) don't move
    /// keyboard focus, so the inline editor's LostFocus never fires. Walk up
    /// the visual tree from the click target; if no inline-name TextBox is in
    /// the path, the click happened outside any editor and we commit every
    /// open rename.
    /// </summary>
    private void Window_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;

        // Click landed inside the editor itself — leave it alone.
        for (var src = e.Source as Visual; src is not null; src = src.GetVisualParent())
        {
            if (src is TextBox tb && tb.Classes.Contains("inline-name"))
                return;
        }

        // Click outside any inline editor — commit all open renames.
        // ConfirmRename is idempotent so a double-commit from a near-simultaneous
        // LostFocus is harmless.
        foreach (var card in vm.Instances)
        {
            if (card.IsRenaming)
                card.ConfirmRenameCommand.Execute(null);
        }
    }
}
