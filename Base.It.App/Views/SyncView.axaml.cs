using Avalonia.Controls;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class SyncView : UserControl
{
    public SyncView()
    {
        InitializeComponent();
        WireSourceFilter();
    }

    /// <summary>
    /// AutoCompleteBox's default filter only matches against ToString() —
    /// when DisplayName is set the env/db pair becomes invisible to
    /// type-ahead. This filter makes the picker searchable by display
    /// name, environment, or database name simultaneously.
    /// </summary>
    private void WireSourceFilter()
    {
        var box = this.FindControl<AutoCompleteBox>("SourceBox");
        if (box is null) return;
        box.ItemFilter = (search, item) =>
        {
            if (item is not EndpointPick p) return false;
            if (string.IsNullOrEmpty(search)) return true;
            var s = search.Trim();
            return p.Label.Contains(s, System.StringComparison.OrdinalIgnoreCase)
                || p.Environment.Contains(s, System.StringComparison.OrdinalIgnoreCase)
                || p.Database.Contains(s, System.StringComparison.OrdinalIgnoreCase);
        };
    }
}
