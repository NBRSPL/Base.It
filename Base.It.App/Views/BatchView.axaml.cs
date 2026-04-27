using Avalonia.Controls;
using Base.It.App.ViewModels;

namespace Base.It.App.Views;

public partial class BatchView : UserControl
{
    public BatchView()
    {
        InitializeComponent();
        WireSourceFilter();
    }

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
