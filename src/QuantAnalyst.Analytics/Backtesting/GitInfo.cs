namespace QuantAnalyst.Analytics.Backtesting;

/// <summary>Reads the current commit from .git without running git (for the TrialLedger's reproducibility field).</summary>
public static class GitInfo
{
    /// <summary>The HEAD commit of the repository containing <paramref name="start"/>, or null when not found.</summary>
    public static string? TryGetCommit(string start)
    {
        try
        {
            for (DirectoryInfo? d = new(Path.GetFullPath(start)); d is not null; d = d.Parent)
            {
                string git = Path.Combine(d.FullName, ".git");
                if (File.Exists(git))
                {
                    // Worktree: ".git" is a file "gitdir: <path>".
                    string target = File.ReadAllText(git).Trim();
                    if (target.StartsWith("gitdir:", StringComparison.Ordinal))
                    {
                        git = Path.GetFullPath(Path.Combine(d.FullName, target["gitdir:".Length..].Trim()));
                    }
                }

                if (!Directory.Exists(git))
                {
                    continue;
                }

                string head = File.ReadAllText(Path.Combine(git, "HEAD")).Trim();
                if (!head.StartsWith("ref:", StringComparison.Ordinal))
                {
                    return IsSha(head) ? head : null;
                }

                string reference = head["ref:".Length..].Trim();
                string loose = Path.Combine(git, reference.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(loose))
                {
                    string sha = File.ReadAllText(loose).Trim();
                    return IsSha(sha) ? sha : null;
                }

                string packed = Path.Combine(git, "packed-refs");
                if (File.Exists(packed))
                {
                    foreach (string line in File.ReadLines(packed))
                    {
                        string[] parts = line.Split(' ');
                        if (parts.Length == 2 && parts[1] == reference && IsSha(parts[0]))
                        {
                            return parts[0];
                        }
                    }
                }

                return null;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static bool IsSha(string s) => s.Length == 40 && s.All(Uri.IsHexDigit);
}
