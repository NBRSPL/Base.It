using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Base.It.Core.Sync.TableAlter;

namespace Base.It.App.Views;

/// <summary>
/// Modal review window the Sync screen pops up when it's about to ALTER
/// an existing table. Two roles:
///
/// <list type="bullet">
///   <item>Show the user what's about to change — safe steps in one
///         list, destructive steps in another with a per-step Apply
///         checkbox so the user can opt-in selectively. The SQL panel
///         at the bottom regenerates as boxes tick, so what's previewed
///         is exactly what will run.</item>
///   <item>Hand back the chosen subset via <see cref="ShowAsync"/> —
///         returns the approved destructive list on Apply, <c>null</c>
///         on Cancel / close. Safe steps are implied (always applied).</item>
/// </list>
///
/// Why not a generic confirm dialog with a multi-line message? Because
/// destructive ALTERs need a per-step choice and a live SQL view — a
/// single Yes/No prompt erases the nuance and pushes users toward the
/// least-safe option just to get past the dialog.
/// </summary>
public partial class AlterPreviewWindow : Window
{
    /// <summary>Selected destructive steps, or null if user cancelled.</summary>
    private IReadOnlyList<AlterStep>? _result;
    private AlterPlan? _plan;

    /// <summary>
    /// Per-destructive row state: the underlying AlterStep + the
    /// CheckBox that toggles its inclusion. Held so OnApply can read
    /// each box and rebuild the approved list, and so OnDestructiveToggled
    /// can refresh the SQL preview live.
    /// </summary>
    private readonly List<(AlterStep Step, CheckBox Box)> _destRows = new();

    public AlterPreviewWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Opened += (_, _) => Services.WindowSizing.ClampToWorkingArea(this);
    }

    /// <summary>
    /// Show the preview modally over the application's main window.
    /// Returns the destructive subset the user approved, or <c>null</c>
    /// if they cancelled (close button, Cancel button, Escape). Finds
    /// its own owner via the desktop lifetime so VMs can call this
    /// without plumbing a <see cref="Window"/> reference down — matches
    /// how <c>ConfirmDialog</c> / <c>PromptDialog</c> work elsewhere.
    /// </summary>
    public static async Task<IReadOnlyList<AlterStep>?> ShowAsync(
        AlterPlan plan, string targetLabel)
    {
        var lifetime = Avalonia.Application.Current?.ApplicationLifetime
            as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
        var owner = lifetime?.MainWindow;
        if (owner is null) return null;

        var win = new AlterPreviewWindow();
        win.Bind(plan, targetLabel);
        await win.ShowDialog(owner);
        return win._result;
    }

    private void Bind(AlterPlan plan, string targetLabel)
    {
        _plan = plan;
        var title = this.FindControl<TextBlock>("TitleBlock");
        var subtitle = this.FindControl<TextBlock>("SubtitleBlock");
        var status = this.FindControl<TextBlock>("StatusBlock");
        if (title is not null) title.Text = $"ALTER [{plan.Table.Schema}].[{plan.Table.Name}]";
        if (subtitle is not null)
            subtitle.Text = $"Target: {targetLabel}    ·    {plan.OneLineSummary()}";
        if (status is not null) status.Text = "Backup of the target's current state is already on disk.";

        // ─── Safe list — bullet rows, no checkboxes (always applied). ──
        var safeStack = this.FindControl<StackPanel>("SafeStack");
        if (safeStack is not null)
        {
            safeStack.Children.Clear();
            if (plan.SafeSteps.Count == 0)
                safeStack.Children.Add(new TextBlock { Text = "(no safe changes detected)", Opacity = 0.6, FontSize = 11 });
            else
                foreach (var s in plan.SafeSteps)
                    safeStack.Children.Add(BuildSafeRow(s));
        }

        // ─── Destructive list — each row a CheckBox + reason ───────────
        var destStack = this.FindControl<StackPanel>("DestructiveStack");
        var destHdr   = this.FindControl<TextBlock>("DestructiveHeader");
        var destBdr   = this.FindControl<Border>("DestructiveBorder");
        if (!plan.HasDestructive)
        {
            if (destHdr is not null) destHdr.IsVisible = false;
            if (destBdr is not null) destBdr.IsVisible = false;
        }
        else if (destStack is not null)
        {
            destStack.Children.Clear();
            foreach (var d in plan.DestructiveSteps)
            {
                var box = new CheckBox
                {
                    IsChecked = false,
                    Content = BuildDestructiveContent(d),
                    Margin = new Avalonia.Thickness(0, 0, 0, 4),
                };
                box.IsCheckedChanged += (_, _) => RebuildSqlPreview();
                _destRows.Add((d, box));
                destStack.Children.Add(box);
            }
        }

        RebuildSqlPreview();
    }

    private static Control BuildSafeRow(AlterStep s)
    {
        // "• <summary>"  — single line, fixed bullet, ellipsis if it overflows.
        return new TextBlock
        {
            Text = "• " + s.Summary,
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Avalonia.Thickness(0, 1, 0, 1),
        };
    }

    private static Control BuildDestructiveContent(AlterStep d)
    {
        // CheckBox content is allowed to be a layout panel; here we
        // stack the summary on top of the (smaller, dimmer) reason
        // so the danger explanation is RIGHT next to the toggle.
        var panel = new StackPanel { Spacing = 1 };
        panel.Children.Add(new TextBlock
        {
            Text = d.Summary,
            FontSize = 11, FontWeight = FontWeight.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        if (!string.IsNullOrWhiteSpace(d.DestructiveReason))
            panel.Children.Add(new TextBlock
            {
                Text = d.DestructiveReason,
                FontSize = 10, Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        return panel;
    }

    /// <summary>
    /// Recompute the SQL preview from the currently-ticked destructive
    /// rows plus all safe rows. Runs on every checkbox toggle so what
    /// the user sees is what will execute.
    /// </summary>
    private void RebuildSqlPreview()
    {
        if (_plan is null) return;
        var sqlBlock = this.FindControl<SelectableTextBlock>("SqlBlock");
        if (sqlBlock is null) return;

        var ticked = _destRows.Where(r => r.Box.IsChecked == true).Select(r => r.Step);
        var steps  = _plan.SafeSteps.Concat(ticked).ToList();
        if (steps.Count == 0)
        {
            sqlBlock.Text = "-- Nothing to apply with the current selection.";
            return;
        }
        sqlBlock.Text = AlterScriptBuilder.Build(_plan.Table, steps);
    }

    private void OnCancel(object? sender, RoutedEventArgs e)
    {
        _result = null;
        Close();
    }

    private void OnApply(object? sender, RoutedEventArgs e)
    {
        _result = _destRows
            .Where(r => r.Box.IsChecked == true)
            .Select(r => r.Step)
            .ToList();
        Close();
    }
}
