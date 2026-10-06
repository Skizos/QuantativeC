using QuantAnalyst.Desktop.Core.Engine;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// Several pages at once (docs/plans/13-pages-side-by-side.md): a page beside the selected one, pages in windows of
/// their own, what is refreshed, and the folding rail.
/// </summary>
public sealed class PagesTests : IDisposable
{
    private readonly TempWorkspace _ws = new();

    public void Dispose() => _ws.Dispose();

    private ShellViewModel Shell() => new(_ws.Workspace, new QaEngine(new ImmediateDispatcher()), _ws.Time, null, new FakeEnvironment());

    private static PageChoice Choice(ShellViewModel shell, PageKind? kind) => shell.BesideChoices.Single(c => c.Kind == kind);

    [Fact]
    public void Beside_ShowsASecondPage_ChartsBecomesAChartOfItsOwn_AndAPageMovesLeftWhenPickedInTheRail()
    {
        ShellViewModel shell = Shell();
        Assert.Equal(["Nothing", "Overview", "Trading", "Charts", "Accounts", "Instruments", "Strategy", "Reports"], shell.BesideChoices.Select(c => c.Title));
        Assert.False(shell.HasBeside);
        Assert.Null(shell.BesidePage);
        Assert.False(shell.CloseBesideCommand.CanExecute(null));

        shell.Navigate(PageKind.Accounts);
        shell.SelectedBeside = Choice(shell, PageKind.Charts);
        Assert.True(shell.HasBeside);
        ChartsViewModel besideChart = Assert.IsType<ChartsViewModel>(shell.BesidePage);
        Assert.NotSame(shell.Charts, besideChart); // a chart of its own
        Assert.True(shell.CloseBesideCommand.CanExecute(null));

        shell.SelectedBeside = Choice(shell, PageKind.Strategy);
        Assert.Same(shell.Strategy, shell.BesidePage);
        Assert.True(besideChart.IsDisposed); // replaced: it let go of the session

        // The page already on the left can't also be on the right: the choice stays.
        shell.SelectedBeside = Choice(shell, PageKind.Accounts);
        Assert.Equal(PageKind.Strategy, shell.SelectedBeside.Kind);
        Assert.Same(shell.Strategy, shell.BesidePage);

        // Picking the right-hand page in the rail moves it to the left.
        shell.Navigate(PageKind.Strategy);
        Assert.False(shell.HasBeside);
        Assert.Null(shell.SelectedBeside.Kind);

        // Charts beside Charts: a second chart.
        shell.Navigate(PageKind.Charts);
        shell.SelectedBeside = Choice(shell, PageKind.Charts);
        ChartsViewModel second = Assert.IsType<ChartsViewModel>(shell.BesidePage);
        Assert.NotSame(shell.Charts, second);

        shell.CloseBesideCommand.Execute(null);
        Assert.False(shell.HasBeside);
        Assert.Null(shell.SelectedBeside.Kind);
        Assert.True(second.IsDisposed);
        Assert.False(shell.Charts.IsDisposed); // the app's own Charts page lives on
    }

    [Fact]
    public void NewWindow_ShowsTheSamePage_ButAChartOfItsOwn_AndForgetsItWhenTheWindowCloses()
    {
        ShellViewModel shell = Shell();
        var opened = new List<PageViewModel>();
        shell.PageWindowRequested += opened.Add;

        shell.OpenWindowCommand.Execute(shell.Instruments);
        shell.OpenWindowCommand.Execute(shell.Instruments);
        Assert.Equal([shell.Instruments, shell.Instruments], opened); // the same page: the same numbers and buttons
        Assert.Equal(2, shell.WindowPages.Count(p => ReferenceEquals(p, shell.Instruments)));

        shell.OpenWindowCommand.Execute(shell.Charts);
        ChartsViewModel chart = Assert.IsType<ChartsViewModel>(opened[^1]);
        Assert.NotSame(shell.Charts, chart);

        shell.OpenWindowCommand.Execute("not a page");
        Assert.Equal(3, opened.Count);

        shell.WindowClosed(shell.Instruments);
        Assert.Single(shell.WindowPages, p => ReferenceEquals(p, shell.Instruments)); // the other window is still open
        shell.WindowClosed(chart);
        Assert.DoesNotContain(chart, shell.WindowPages);
        Assert.True(chart.IsDisposed);
        shell.WindowClosed(shell.Instruments);
        Assert.Empty(shell.WindowPages);
        Assert.False(shell.Charts.IsDisposed);
    }

    [Fact]
    public async Task EveryPageOnScreen_IsRefreshed_AndOnlyThose()
    {
        ShellViewModel shell = Shell();
        shell.SelectedBeside = Choice(shell, PageKind.Accounts);
        shell.OpenWindowCommand.Execute(shell.Instruments);
        _ws.AllowEricB();
        Assert.Empty(shell.Instruments.Rows);

        await shell.RefreshVisibleAsync();
        Assert.NotEmpty(shell.Status.Lines); // on the left
        Assert.Contains(shell.Accounts.Accounts, a => a.IsPaper); // on the right
        Assert.Equal(["ERIC B"], shell.Instruments.Rows.Select(r => r.Ticker)); // in a window
        Assert.Empty(shell.Reports.GateDots); // not on screen: not read
    }

    [Fact]
    public void TheRail_FoldsToIcons_AndBack()
    {
        ShellViewModel shell = Shell();
        Assert.False(shell.NavCollapsed);
        shell.ToggleNavCommand.Execute(null);
        Assert.True(shell.NavCollapsed);
        shell.ToggleNavCommand.Execute(null);
        Assert.False(shell.NavCollapsed);
    }
}
