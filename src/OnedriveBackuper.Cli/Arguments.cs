namespace OnedriveBackuper.Cli;

/// <summary>Minimal parser: positional values, "--flag", and "--option value" (repeatable).</summary>
internal sealed class Arguments
{
    private readonly Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> flags = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Positional { get; } = [];

    public static Arguments Parse(IEnumerable<string> args, IReadOnlySet<string> knownFlags, IReadOnlySet<string> knownOptions)
    {
        var result = new Arguments();
        using var e = args.GetEnumerator();
        while (e.MoveNext())
        {
            var arg = e.Current;
            if (!arg.StartsWith("--", StringComparison.Ordinal) || arg == "--")
            {
                result.Positional.Add(arg);
                continue;
            }

            var name = arg[2..];
            string? inlineValue = null;
            var equals = name.IndexOf('=');
            if (equals >= 0)
            {
                inlineValue = name[(equals + 1)..];
                name = name[..equals];
            }

            if (knownFlags.Contains(name) && inlineValue == null)
            {
                result.flags.Add(name);
            }
            else if (knownOptions.Contains(name))
            {
                var value = inlineValue ?? (e.MoveNext() ? e.Current : throw new UsageException($"--{name} needs a value."));
                if (!result.values.TryGetValue(name, out var list))
                {
                    result.values[name] = list = [];
                }
                list.Add(value);
            }
            else
            {
                throw new UsageException($"Unknown option --{name}.");
            }
        }
        return result;
    }

    public bool Has(string flag) => flags.Contains(flag);

    public string? Value(string option) => values.TryGetValue(option, out var list) ? list[^1] : null;

    public IReadOnlyList<string> All(string option) => values.TryGetValue(option, out var list) ? list : [];
}

internal sealed class UsageException(string message) : Exception(message);
