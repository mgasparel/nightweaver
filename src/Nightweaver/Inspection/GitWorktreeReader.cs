namespace Nightweaver.Inspection;

internal sealed class GitWorktreeReader(IProcessRunner processRunner)
{
    internal async Task<IReadOnlyList<GitWorktreeSnapshot>> ReadAsync(ValidatedTargetRepository repository, CancellationToken cancellationToken)
    {
        var context = $"git worktree list --porcelain -z in Target Repository '{repository.Path}'";
        try
        {
            var result = await processRunner.RunAsync(GitCommand.Create(["worktree", "list", "--porcelain", "-z"], repository.Path), cancellationToken);
            if (result.ExitCode != 0) throw new ToolExecutionException($"exited {result.ExitCode}", result.StandardError);
            return Parse(result.StandardOutput);
        }
        catch (ToolExecutionException exception) { throw new ToolExecutionException($"{context}: {exception.Message}", exception.StandardError); }
        catch (OperationCanceledException) { throw new ToolExecutionException($"{context}: cancelled"); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or FormatException or PathTooLongException)
        { throw new ToolExecutionException($"{context}: invalid porcelain-z output: {exception.Message}"); }
    }

    internal static IReadOnlyList<GitWorktreeSnapshot> Parse(string output)
    {
        if (!output.EndsWith("\0\0", StringComparison.Ordinal)) throw new FormatException("missing record terminator");
        var records = new List<GitWorktreeSnapshot>();
        var paths = new HashSet<string>(RuntimePaths.Comparer);
        var fields = new Dictionary<string, string?>(StringComparer.Ordinal);
        var tokens = output.Split('\0');
        foreach (var token in tokens.Take(tokens.Length - 1))
        {
            if (token.Length == 0)
            {
                if (!fields.TryGetValue("worktree", out var raw) || string.IsNullOrEmpty(raw) || !Path.IsPathFullyQualified(raw))
                    throw new FormatException("missing absolute worktree identity");
                var path = RuntimePaths.Normalize(raw);
                if (!paths.Add(path)) throw new FormatException("conflicting worktree registrations");
                var bare = fields.ContainsKey("bare");
                var detached = fields.ContainsKey("detached");
                if ((bare && (detached || fields.ContainsKey("branch") || fields.ContainsKey("HEAD"))) || (detached && fields.ContainsKey("branch")))
                    throw new FormatException("conflicting checkout state");
                if (!bare && (!fields.ContainsKey("HEAD") || (!detached && !fields.ContainsKey("branch"))))
                    throw new FormatException("incomplete checkout state");
                records.Add(new(raw, path, fields.GetValueOrDefault("HEAD"), fields.GetValueOrDefault("branch"), detached, bare,
                    fields.ContainsKey("locked"), fields.GetValueOrDefault("locked"), fields.ContainsKey("prunable"), fields.GetValueOrDefault("prunable"), records.Count == 0));
                fields.Clear();
                continue;
            }
            var space = token.IndexOf(' ');
            var key = space < 0 ? token : token[..space];
            var value = space < 0 ? null : token[(space + 1)..];
            if (fields.Count == 0 && key != "worktree") throw new FormatException("record must begin with worktree identity");
            if (key is not ("worktree" or "HEAD" or "branch" or "detached" or "bare" or "locked" or "prunable")) continue;
            if ((key is "worktree" or "HEAD" or "branch") && string.IsNullOrEmpty(value)) throw new FormatException($"missing {key} value");
            if ((key is "detached" or "bare") && value is not null) throw new FormatException($"unexpected {key} value");
            if (key == "HEAD" && (value!.Length is not (40 or 64) || !value.All(char.IsAsciiHexDigit))) throw new FormatException("invalid HEAD object ID");
            if (!fields.TryAdd(key, value)) throw new FormatException($"duplicate {key}");
        }
        return records.AsReadOnly();
    }
}
