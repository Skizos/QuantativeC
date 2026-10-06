using System.Text.RegularExpressions;
using QuantAnalyst.Desktop.Core.Presentation;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The sizing rules of the adaptive panels (docs/plans/14-adaptive-pages.md), and a guard over the page XAML: no fixed
/// columns or fixed-column tile grids that can't give way when a page is shown at half width beside another.
/// </summary>
public sealed partial class AdaptiveTests
{
    // The owner's screenshot: a 1 300 px window, the rail open, Charts beside Instruments. Each pane is about 500 px
    // wide, so after the page margins a page has about 460 px.
    private const double HalfPane = 460;

    private static string DesktopDir => Path.Combine(TempWorkspace.RepoRoot(), "src", "QuantAnalyst.Desktop");

    [Fact]
    public void ASplit_IsSideBySideWhenWide_AndStackedInHalfAPane()
    {
        var instruments = new SplitSpec(Breakpoint: 760, FirstWidth: 320);
        Assert.Equal(new SplitPlan(false, 320, 1040 - 16 - 320), Adaptive.Split(1040, instruments)); // the page alone
        Assert.Equal(new SplitPlan(true, HalfPane, HalfPane), Adaptive.Split(HalfPane, instruments)); // beside another page

        var overview = new SplitSpec(Breakpoint: 820, FirstShare: 0.6);
        Assert.Equal(new SplitPlan(false, 600, 1000 - 16 - 600), Adaptive.Split(1000, overview));

        // Side by side, neither part is squeezed below 200 px.
        Assert.Equal(new SplitPlan(false, 200, 484), Adaptive.Split(700, new SplitSpec(Breakpoint: 500, FirstShare: 0.1)));
        Assert.Equal(new SplitPlan(false, 484, 200), Adaptive.Split(700, new SplitSpec(Breakpoint: 500, FirstWidth: 650)));
        Assert.False(Adaptive.Split(double.PositiveInfinity, instruments).Stacked);
    }

    [Fact]
    public void Stacked_TheListGetsItsRowsUpToItsCap_AndTheDetailsTheRest()
    {
        var instruments = new SplitSpec(StackedFirstMaxHeight: 210, StackedSecondMinHeight: 260);
        Assert.Equal((150d, 700 - 16 - 150d), Adaptive.Stack(700, instruments, 150, 900)); // two names: their height
        Assert.Equal((210d, 700 - 16 - 210d), Adaptive.Stack(700, instruments, 800, 900)); // many: capped, it scrolls
        Assert.Equal((124d, 260d), Adaptive.Stack(400, instruments, 800, 900)); // a low pane: the chart keeps 260 px
        Assert.Equal((0d, 0d), Adaptive.Stack(10, instruments, 800, 900));

        // In a scrolling page (Overview, Trading) each part is as high as it wants.
        Assert.Equal((500d, 300d), Adaptive.Stack(double.PositiveInfinity, new SplitSpec(), 500, 300));
    }

    [Fact]
    public void ARow_KeepsTheButtonsBesideTheText_UntilTheTextWouldGetTooLittle()
    {
        // The page header: the title fills, the tools (Beside, New window, Refresh) are about 450 px.
        RowPlan wide = Adaptive.Row(1040, [120, 450], fill: 0, fillMin: 170, spacing: 16);
        Assert.False(wide.Stacked);
        Assert.Equal([1040 - 450 - 16, 450d], wide.Widths);

        RowPlan half = Adaptive.Row(HalfPane, [120, 450], fill: 0, fillMin: 170, spacing: 16);
        Assert.True(half.Stacked); // the title keeps its line; the tools go under it
        Assert.Equal([HalfPane, HalfPane], half.Widths);

        Assert.False(Adaptive.Row(636, [120, 450], 0, 170, 16).Stacked); // exactly enough
        Assert.True(Adaptive.Row(635, [120, 450], 0, 170, 16).Stacked);
        Assert.Equal([100d, 250d, 50d], Adaptive.Row(424, [100, 999, 50], fill: 1, fillMin: 100, spacing: 12).Widths); // the middle one fills
        Assert.Equal([100d, 50d], Adaptive.Row(double.PositiveInfinity, [100, 50], 0, 500, 12).Widths);
        Assert.Empty(Adaptive.Row(300, [], 0, 100, 12).Widths);
    }

    [Theory]
    [InlineData(1040, 4)] // the Overview alone: four tiles in a row
    [InlineData(HalfPane, 2)] // beside another page: two by two
    [InlineData(380, 1)] // the narrowest pane: one per row
    [InlineData(5000, 4)] // never more columns than tiles
    public void Tiles_TakeAsManyColumnsAsFit(double width, int columns) =>
        Assert.Equal(columns, Adaptive.Columns(width, minItemWidth: 210, count: 4, spacing: 12));

    [Fact]
    public void Tiles_WithoutAWidth_OrWithoutTiles_StillHaveAColumn()
    {
        Assert.Equal(4, Adaptive.Columns(double.PositiveInfinity, 210, 4, 12));
        Assert.Equal(1, Adaptive.Columns(1000, 210, 0, 12));
    }

    // ---- the XAML guard -----------------------------------------------------------------------------------

    [Fact]
    public void NoPage_HasAFixedWideColumn_AFixedTileGrid_OrAChartTooTallForHalfAPane()
    {
        var problems = new List<string>();
        foreach (string file in Directory.EnumerateFiles(DesktopDir, "*.xaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                                 && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)))
        {
            string name = Path.GetRelativePath(DesktopDir, file);
            problems.AddRange(Problems(name, File.ReadAllText(file)));
        }

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData("""<ColumnDefinition Width="340" />""")]
    [InlineData("""<ColumnDefinition Width="1200" />""")]
    [InlineData("""<UniformGrid Columns="4">""")]
    [InlineData("""<c:CandleChart Data="{Binding Chart}" MinHeight="420" />""")]
    [InlineData("""<c:SeriesChart MinHeight="300" />""")]
    public void TheGuard_CatchesWhatWouldNotGiveWay(string xaml) => Assert.Single(Problems("sample", xaml));

    [Theory]
    [InlineData("""<ColumnDefinition Width="16" />""")]
    [InlineData("""<ColumnDefinition Width="*" MinWidth="460" />""")]
    [InlineData("""<ColumnDefinition Width="Auto" />""")]
    [InlineData("""<c:CandleChart MinHeight="220" />""")]
    [InlineData("""<c:SeriesChart Height="42" Compact="True" />""")]
    public void TheGuard_LeavesWhatGivesWay(string xaml) => Assert.Empty(Problems("sample", xaml));

    private static IEnumerable<string> Problems(string file, string xaml)
    {
        foreach (Match m in FixedColumn().Matches(xaml))
        {
            yield return $"{file}: a fixed {m.Groups["w"].Value} px column; use an AdaptiveSplit or a star width";
        }

        foreach (Match _ in FixedTileGrid().Matches(xaml))
        {
            yield return $"{file}: a UniformGrid with fixed columns; use an AdaptiveGrid";
        }

        foreach (Match m in TallChart().Matches(xaml))
        {
            yield return $"{file}: a chart with MinHeight {m.Groups["h"].Value}; charts shrink instead (at most 260)";
        }
    }

    [GeneratedRegex("""<ColumnDefinition\b[^>]*\bWidth="(?<w>[2-9]\d\d|\d{4,})(\.\d+)?"[^>]*>""")]
    private static partial Regex FixedColumn();

    [GeneratedRegex("""<UniformGrid\b[^>]*\bColumns="\d+"[^>]*>""")]
    private static partial Regex FixedTileGrid();

    [GeneratedRegex("""<c:(Candle|Series)Chart\b[^>]*\bMinHeight="(?<h>2[7-9]\d|[3-9]\d\d|\d{4,})"[^>]*>""")]
    private static partial Regex TallChart();
}
