using System.Text;
using RunPad.Format.RPad;

namespace RunPad.Contracts.Connections;

/// <summary>
/// Replaces <c>${secret:…}</c> and <c>${env:…}</c> in a connection's params with their values, for
/// RunPad's own use of a connection (the Test button). The resolved values stay in memory: they are
/// never written back to a script or to RunPad's connection list.
/// </summary>
public static class ConnectionPlaceholderResolver
{
    public sealed record Result(IReadOnlyDictionary<string, RpadValue> Params, IReadOnlyList<string> Errors);

    /// <param name="secret">The Secret Store value for a name, or null when there is none.</param>
    /// <param name="environment">An environment variable's value, or null when it isn't set.</param>
    public static Result Resolve(IReadOnlyDictionary<string, RpadValue> parameters,
        Func<string, string?> secret, Func<string, string?> environment)
    {
        var errors = new List<string>();
        var resolved = parameters.ToDictionary(p => p.Key, p => Resolve(p.Value), StringComparer.Ordinal);
        return new Result(resolved, errors.Distinct().ToList());

        RpadValue Resolve(RpadValue value) => value.Text is { } text ? RpadValue.Of(ResolveText(text))
            : value.Items is { } items ? RpadValue.List(items.Select(Resolve))
            : RpadValue.Map((value.Fields ?? []).Select(f => KeyValuePair.Create(f.Key, Resolve(f.Value))));

        string ResolveText(string text)
        {
            var parsed = RpadPlaceholders.Parse(text);
            if (parsed.Error is { } error)
            {
                errors.Add(error);
                return text;
            }
            var sb = new StringBuilder(text.Length);
            foreach (var part in parsed.Parts)
            {
                var value = part.Kind switch
                {
                    RpadPlaceholders.Kind.Secret => secret(part.Value)
                        ?? Missing($"The Secret Store has no secret named '{part.Value}'. Add it in Tools → Secret Store."),
                    RpadPlaceholders.Kind.Environment => environment(part.Value)
                        ?? Missing($"The environment variable '{part.Value}' is not set."),
                    _ => part.Value,
                };
                sb.Append(value);
            }
            return sb.ToString();
        }

        string Missing(string message)
        {
            errors.Add(message);
            return "";
        }
    }
}
