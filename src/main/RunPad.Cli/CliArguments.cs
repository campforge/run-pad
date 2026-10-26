using RunPad.Format.RPad;

namespace RunPad.Cli;

/// <summary>
/// The parsed command line of <c>runpad [run] [options] &lt;script&gt; [options] [--] [args...]</c>.
/// Options follow the two runners people already script against: LINQPad's <c>lprun</c>
/// (<c>-lang=Program</c>, <c>-compileonly</c>; anything after the script is passed to it) and NetPad's
/// <c>npad</c> (an optional <c>run</c> command, <c>--kind program</c>, <c>-s 9</c>).
/// </summary>
internal sealed record CliArguments
{
    public string? ScriptPath { get; init; }

    /// <summary>The script's <c>args</c>; null when nothing follows the script, so the front matter's
    /// declarations and selected preset apply. A bare <c>--</c> gives an empty list: no arguments at all.</summary>
    public IReadOnlyList<string>? ScriptArguments { get; init; }

    /// <summary><c>--args-set NAME</c>: run with the script's argument set of that name. Arguments given
    /// after the script still replace it.</summary>
    public string? ArgumentSet { get; init; }

    public RpadKind? Kind { get; init; }

    /// <summary>Set by the <c>VB…</c> values of <c>-lang</c>: the script must be Visual Basic.</summary>
    public RpadLanguage? Language { get; init; }

    public TargetSdk? TargetSdk { get; init; }
    public bool CompileOnly { get; init; }

    /// <summary>Read every connection's description (a gRPC server's services) again before the build,
    /// not only those never read or that failed.</summary>
    public bool RefreshConnections { get; init; }
    public bool ShowHelp { get; init; }
    public bool ShowVersion { get; init; }

    /// <summary>Why the command line can't be used, or null.</summary>
    public string? Error { get; init; }

    // Accepted for compatibility, with nothing to do: the runner compiles every script afresh in a
    // temporary folder, so there is no build cache to skip or rebuild.
    private static readonly HashSet<string> NoEffectOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "nunuget", "no-cache", "rebuild", "b",
    };

    // Recognized, so they are reported instead of silently passed to the script, but not available yet.
    private static readonly HashSet<string> UnsupportedOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "f", "eval", "e", "cxname", "connection", "c", "optimize", "O", "warn", "aspnet",
        "minimal", "m", "verbose", "v", "sql",
    };

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        var result = new CliArguments();
        var index = 0;
        // NetPad's default command, written out.
        if (args.Count > 0 && args[0] == "run") index++;

        List<string>? scriptArguments = null;
        for (; index < args.Count; index++)
        {
            var arg = args[index];
            if (arg == "--")
            {
                // Everything after "--" belongs to the script, even what looks like an option.
                scriptArguments ??= [];
                scriptArguments.AddRange(args.Skip(index + 1));
                break;
            }

            if (TrySplitOption(arg, out var name, out var inlineValue))
            {
                var option = ApplyOption(result, name, inlineValue, args, ref index);
                if (option.Error is not null) return result with { Error = option.Error };
                if (option.Result is not null)
                {
                    result = option.Result;
                    continue;
                }
                // Not one of ours: before the script it's a mistake, after it the script's own argument.
                if (result.ScriptPath is null) return result with { Error = $"Unknown option '{arg}'." };
            }

            if (result.ScriptPath is null)
                result = result with { ScriptPath = arg };
            else
                (scriptArguments ??= []).Add(arg);
        }

        return result with { ScriptArguments = scriptArguments };
    }

    // "-lang=e" (lprun), "--kind program" / "-k program" (npad); "-" alone is a value, not an option.
    private static bool TrySplitOption(string arg, out string name, out string? inlineValue)
    {
        name = "";
        inlineValue = null;
        if (arg.Length < 2 || arg[0] != '-') return false;
        var body = arg.StartsWith("--", StringComparison.Ordinal) ? arg[2..] : arg[1..];
        if (body.Length == 0) return false;
        var equals = body.IndexOf('=');
        if (equals >= 0)
        {
            inlineValue = body[(equals + 1)..];
            body = body[..equals];
        }
        name = body;
        return true;
    }

    private static (CliArguments? Result, string? Error) ApplyOption(
        CliArguments current, string name, string? inlineValue, IReadOnlyList<string> args, ref int index)
    {
        switch (name.ToLowerInvariant())
        {
            case "h" or "help" or "?":
                return (current with { ShowHelp = true }, null);
            case "version":
                return (current with { ShowVersion = true }, null);
            case "compileonly" or "compile-only":
                return (current with { CompileOnly = true }, null);
            case "refresh-connections":
                return (current with { RefreshConnections = true }, null);
            case "args-set":
                {
                    if (!TryTakeValue(name, inlineValue, args, ref index, out var value, out var error))
                        return (null, error);
                    return (current with { ArgumentSet = value }, null);
                }
            case "lang" or "kind" or "k":
                {
                    if (!TryTakeValue(name, inlineValue, args, ref index, out var value, out var error))
                        return (null, error);
                    return ParseKind(value) is { } kind
                        ? (current with { Kind = kind.Kind, Language = kind.Language ?? current.Language }, null)
                        : (null, $"Unsupported script kind '{value}'. Use Statements, Program or Expression (or s, p, e; VBStatements, VBProgram, VBExpression).");
                }
            case "s" or "sdk":
                {
                    if (!TryTakeValue(name, inlineValue, args, ref index, out var value, out var error))
                        return (null, error);
                    return ParseSdk(value) is { } sdk
                        ? (current with { TargetSdk = sdk }, null)
                        : (null, $"Unsupported SDK '{value}'. Use a .NET version such as 9 or 9.0, a target framework such as net9.0, or latest.");
                }
        }

        // "-O" (npad) is case-sensitive where "-o" isn't an option at all; the rest compare ignoring case.
        if (NoEffectOptions.Contains(name)) return (current, null);
        if (name == "O" || (name != "o" && UnsupportedOptions.Contains(name)))
            return (null, $"The option '{name}' is not supported yet.");
        return (null, null);
    }

    private static bool TryTakeValue(
        string name, string? inlineValue, IReadOnlyList<string> args, ref int index, out string value, out string? error)
    {
        error = null;
        if (inlineValue is not null)
        {
            value = inlineValue;
        }
        // Another option (or "--") is never the value: "--sdk --compile-only a.rpad" lacks the SDK
        // rather than naming one called "--compile-only" and running the script.
        else if (index + 1 < args.Count && args[index + 1] != "--" && !TrySplitOption(args[index + 1], out _, out _))
        {
            value = args[++index];
        }
        else
        {
            value = "";
        }

        if (value.Length > 0) return true;
        error = $"The option '{name}' needs a value.";
        return false;
    }

    private static (RpadKind Kind, RpadLanguage? Language)? ParseKind(string value)
    {
        RpadLanguage? language = null;
        if (value.StartsWith("vb", StringComparison.OrdinalIgnoreCase) && value.Length > 2)
        {
            language = RpadLanguage.Vb;
            value = value[2..];
        }

        RpadKind? kind = value.ToLowerInvariant() switch
        {
            "s" or "statements" => RpadKind.Statements,
            "p" or "program" => RpadKind.Program,
            "e" or "expression" => RpadKind.Expression,
            _ => null,
        };
        return kind is null ? null : (kind.Value, language);
    }

    // npad takes a bare version ("9", "9.0"); targetSdk values ("net9.0", "latest") work as they are.
    // Anything else is null: an unreadable value would otherwise count as "latest" and run silently.
    // A well-formed version that isn't installed is fine here; the runner falls back with a warning.
    private static TargetSdk? ParseSdk(string value)
    {
        if (string.Equals(value, "latest", StringComparison.OrdinalIgnoreCase))
            return RunPad.Format.RPad.TargetSdk.Default;
        var version = value.StartsWith("net", StringComparison.OrdinalIgnoreCase) ? value[3..] : value;
        var parts = version.Split('.');
        if (parts.Length > 2 || !parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit))) return null;
        return new RunPad.Format.RPad.TargetSdk("net", parts.Length == 1 ? version + ".0" : version);
    }
}
