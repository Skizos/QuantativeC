using System.Reflection;
using System.Text.RegularExpressions;
using QuantAnalyst.Desktop.Core.ViewModels;

namespace QuantAnalyst.Desktop.Tests;

/// <summary>
/// The window can't be opened in the Linux CI, and two XAML mistakes compile but fail only when it opens: a
/// <c>{StaticResource}</c> whose key doesn't exist (or is used above its definition inside a dictionary), and a
/// <c>{Binding}</c> whose property doesn't exist (the field then stays empty without an error). Both are checked here
/// on the source files (docs/plans/11-app-redesign.md).
/// </summary>
public sealed partial class XamlResourceTests
{
    /// <summary>The application dictionaries, in the order they are merged (App.xaml merges the theme first).</summary>
    private static readonly string[] Dictionaries = ["Themes/Theme.xaml", "App.xaml"];

    /// <summary>Paths bound on WPF elements or collections rather than on the app's own objects (e.g. a list's Count).</summary>
    private static readonly string[] WpfPaths = ["ActualWidth", "ActualHeight", "IsDropDownOpen", "IsChecked", "Text", "Count"];

    private static string DesktopDir => Path.Combine(TempWorkspace.RepoRoot(), "src", "QuantAnalyst.Desktop");

    [Fact]
    public void EveryStaticResource_IsDefined_AndDictionariesDefineItBeforeUse()
    {
        (HashSet<string> appKeys, List<string> problems) = ApplicationKeys();
        foreach (string file in ViewFiles())
        {
            problems.AddRange(MissingKeys(Path.GetRelativePath(DesktopDir, file), File.ReadAllText(file), appKeys));
        }

        Assert.True(appKeys.Count > 40, $"only {appKeys.Count} application keys found");
        Assert.Empty(problems);
    }

    [Fact]
    public void EveryBindingPath_NamesAPublicPropertyOfTheAppsTypes()
    {
        HashSet<string> properties = AppProperties();
        var problems = new List<string>();
        int checkedPaths = 0;
        foreach (string file in ViewFiles().Concat(Dictionaries.Select(d => Path.Combine(DesktopDir, d))))
        {
            foreach (string path in BindingPaths(File.ReadAllText(file)))
            {
                checkedPaths++;
                if (!properties.Contains(path))
                {
                    problems.Add($"{Path.GetRelativePath(DesktopDir, file)}: {{Binding {path}}} names no public property of the app's types");
                }
            }
        }

        Assert.True(checkedPaths > 50, $"only {checkedPaths} binding paths found");
        Assert.Empty(problems);
    }

    // ---- positive controls: the checks catch what they must ------------------------------------------------

    [Fact]
    public void TheResourceCheck_CatchesAnUnknownKey_AndAUseBeforeTheDefinition()
    {
        Assert.Single(MissingKeys("sample", """<Border Style="{StaticResource NoSuchStyle}" />""", ["Card"]));
        Assert.Empty(MissingKeys("sample", """<Border Style="{StaticResource Card}" />""", ["Card"]));
        Assert.Empty(MissingKeys("sample", """<Style x:Key="Local" /><Border Style="{StaticResource Local}" />""", []));
        Assert.Empty(MissingKeys("sample", """<Style BasedOn="{StaticResource {x:Type Button}}" />""", []));

        (_, List<string> ordered) = KeysInOrder("dict", """<Style x:Key="A"><Setter Value="{StaticResource B}" /></Style><SolidColorBrush x:Key="B" />""", []);
        Assert.Single(ordered);
    }

    [Theory]
    [InlineData("""<TextBlock Text="{Binding Titel}" />""", "Titel")]
    [InlineData("""<Button Command="{Binding DataContext.RemoveCommandd, RelativeSource={RelativeSource AncestorType=UserControl}}" />""", "RemoveCommandd")]
    [InlineData("""<TextBlock Text="{Binding Path=Selected.Summari}" />""", "Summari")]
    public void TheBindingCheck_CatchesAMisspelledProperty(string xaml, string wrong)
    {
        HashSet<string> properties = AppProperties();
        Assert.Contains(wrong, BindingPaths(xaml));
        Assert.DoesNotContain(wrong, properties);
    }

    [Fact]
    public void TheBindingCheck_SkipsBindingsOnWpfElements()
    {
        Assert.Empty(BindingPaths("""<Border MinWidth="{Binding ActualWidth, RelativeSource={RelativeSource TemplatedParent}}" />"""));
        Assert.Empty(BindingPaths("""<TextBlock Text="{Binding}" />"""));
        Assert.Equal(["Title", "Kind"], BindingPaths("""<TextBlock Text="{Binding Title}" /><Icon Data="{Binding Kind, Converter={StaticResource PageIcon}}" />"""));
    }

    // ---- the checks ---------------------------------------------------------------------------------------

    private static IEnumerable<string> ViewFiles() =>
        Directory.EnumerateFiles(DesktopDir, "*.xaml", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !Dictionaries.Any(d => Path.GetFullPath(Path.Combine(DesktopDir, d)) == Path.GetFullPath(f)))
            .Order(StringComparer.Ordinal);

    /// <summary>Every key of the application dictionaries, and any key a dictionary uses above its definition.</summary>
    private static (HashSet<string> Keys, List<string> Problems) ApplicationKeys()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (string dictionary in Dictionaries)
        {
            (keys, List<string> found) = KeysInOrder(dictionary, File.ReadAllText(Path.Combine(DesktopDir, dictionary)), keys);
            problems.AddRange(found);
        }

        return (keys, problems);
    }

    /// <summary>Walks a dictionary in document order: a reference must come after its key (or be defined before the file).</summary>
    private static (HashSet<string> Keys, List<string> Problems) KeysInOrder(string name, string xaml, HashSet<string> before)
    {
        var keys = new HashSet<string>(before, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (Match m in KeyOrReference().Matches(xaml))
        {
            if (m.Groups["key"].Success)
            {
                keys.Add(m.Groups["key"].Value);
            }
            else if (!keys.Contains(m.Groups["ref"].Value))
            {
                problems.Add($"{name}: '{m.Groups["ref"].Value}' is used before it is defined (or never defined)");
            }
        }

        return (keys, problems);
    }

    /// <summary>A view may use the application's keys and its own (anywhere in the file).</summary>
    private static List<string> MissingKeys(string name, string xaml, HashSet<string> appKeys)
    {
        var local = KeyOrReference().Matches(xaml).Where(m => m.Groups["key"].Success).Select(m => m.Groups["key"].Value).ToHashSet(StringComparer.Ordinal);
        return
        [
            .. KeyOrReference().Matches(xaml)
                .Where(m => m.Groups["ref"].Success && !appKeys.Contains(m.Groups["ref"].Value) && !local.Contains(m.Groups["ref"].Value))
                .Select(m => $"{name}: no resource '{m.Groups["ref"].Value}'"),
        ];
    }

    /// <summary>
    /// The property each binding names first (after <c>DataContext.</c> when it reaches a parent's view model). Bindings on
    /// WPF elements (<c>RelativeSource</c> or <c>ElementName</c> without <c>DataContext.</c>) and <c>{Binding}</c> are skipped.
    /// </summary>
    private static List<string> BindingPaths(string xaml)
    {
        var paths = new List<string>();
        foreach (Match m in Binding().Matches(xaml))
        {
            string args = m.Groups["args"].Value.Trim();
            string first = args.Split(',')[0].Trim();
            string path = first.StartsWith("Path=", StringComparison.Ordinal) ? first[5..]
                : first.Contains('=', StringComparison.Ordinal) ? string.Empty
                : first;
            if (path.Length == 0 || path == ".")
            {
                continue;
            }

            bool onElement = args.Contains("RelativeSource", StringComparison.Ordinal) || args.Contains("ElementName", StringComparison.Ordinal);
            if (path.StartsWith("DataContext.", StringComparison.Ordinal))
            {
                path = path["DataContext.".Length..];
            }
            else if (onElement)
            {
                continue;
            }

            foreach (string segment in path.Split('.'))
            {
                string name = segment.Split('[')[0];
                if (name.Length > 0 && !WpfPaths.Contains(name))
                {
                    paths.Add(name);
                }
            }
        }

        return paths;
    }

    /// <summary>Every public property name of the app's public types (view models, rows, the engine) and of the pages' base.</summary>
    private static HashSet<string> AppProperties() =>
        typeof(ShellViewModel).Assembly.GetExportedTypes()
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex("""x:Key="(?<key>[^"]+)"|\{(?:StaticResource|DynamicResource)\s+(?<ref>[A-Za-z_][\w.]*)\s*\}""")]
    private static partial Regex KeyOrReference();

    [GeneratedRegex(@"\{Binding(?<args>[^{}]*(?:\{[^{}]*\}[^{}]*)*)\}")]
    private static partial Regex Binding();
}
