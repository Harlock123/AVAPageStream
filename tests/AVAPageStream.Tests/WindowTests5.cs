using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using AVAPageStream.Core;

namespace AVAPageStream.Tests;

/// <summary>Milestone 2: facing pages, spreads and masters' own guides.</summary>
public partial class WindowTests
{
    [AvaloniaFact]
    public void A_facing_document_shows_its_pages_in_spreads()
    {
        var w = Open();
        w.AddPage(after: true);
        w.AddPage(after: true);
        w.Panel.SelectedIndex = 3; // Document
        Pump();
        w.Panel.FacingBox.IsChecked = true;
        Pump();
        Assert.True(w.Editor.Doc.Setup.Facing);
        Assert.Equal("Inside", w.Panel.MarginLeftLabel.Text);

        // Page 1 is alone, on the right of its spread; pages 2 and 3 face each other.
        w.ShowSheet(SheetRef.PageAt(0));
        Assert.Equal([(SheetRef.PageAt(0), 612.0)], w.View.Spread());
        w.ShowSheet(SheetRef.PageAt(1));
        Assert.Equal([(SheetRef.PageAt(1), 0.0), (SheetRef.PageAt(2), 612.0)], w.View.Spread());

        // Clicking the right-hand page works on it: a rectangle drawn there lands on page 3.
        w.View.FitPage();
        Pump();
        Tool(w, AVAPageStream.Tool.Rectangle);
        var view = w.View;
        Avalonia.Point OnSpread(double x, double y) => view.TranslatePoint(new Avalonia.Point(view.Origin.X + x * view.Scale, view.Origin.Y + y * view.Scale), w)!.Value;
        var a = OnSpread(612 + 100, 100);
        var b = OnSpread(612 + 200, 200);
        w.MouseMove(a);
        w.MouseDown(a, MouseButton.Left);
        w.MouseMove(b, RawInputModifiers.LeftMouseButton);
        w.MouseUp(b, MouseButton.Left);
        Pump();
        Assert.Equal("Page 3 of 3", w.PageInfo.Text);
        var made = Assert.Single(w.Editor.Doc.Pages[2].Objects);
        Assert.Equal(100, made.X, 0);
        Assert.Empty(w.Editor.Doc.Pages[1].Objects);
        Shot(w, "spread");
        Done(w);
    }

    [AvaloniaFact]
    public void A_masters_left_side_is_edited_beside_its_right()
    {
        var w = Open();
        w.AddPage(after: true);
        w.Editor.SetFacing(true);
        w.ShowSheet(SheetRef.PageAt(1)); // a left page
        w.Panel.SelectedIndex = 2;
        Pump();
        // Edit this page's master: its left side.
        w.EditMaster();
        Pump();
        Assert.Equal(SheetRef.MasterAt(0, left: true), w.View.Sheet);
        Assert.Equal("A-Master (left)", w.PageInfo.Text);
        Assert.Equal(2, w.View.Spread().Count);

        // Its own margins, from the Pages tab.
        w.Panel.OwnGuidesBox.IsChecked = true;
        Pump();
        w.Panel.MasterOutsideBox.Text = "1in";
        w.Panel.MasterColumnsBox.Value = 2;
        w.Panel.ApplyMasterGuides();
        Pump();
        var m = w.Editor.Doc.Masters[0];
        Assert.Equal(72, m.Margins!.Right);
        Assert.Equal(2, m.Columns);
        Assert.Equal(72, w.Editor.Doc.GuidesFor(w.Editor.Doc.Pages[1]).Live.X); // the left page's outside is on its left
        Done(w);
    }
}
