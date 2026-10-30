using System.Text;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>
/// How the text of the editor becomes the text of the analysis document, and back: the <c>global using</c> preamble, the wrapper
/// that mirrors the kind of the script (<see cref="MdRunKind.Program"/> opens a class around the code), the one space appended at
/// the end, and the offsets between the two texts. It holds no workspace: the service that owns the workspace asks it for the
/// text and the offsets. What it mirrors must agree with what the compiler emits, or the editor flags things that compile and the
/// other way round.
/// </summary>
internal sealed class ScriptDocumentLayout
{
    // Default usings the document is built with so completion and diagnostics work without the user typing them: exactly the implicit
    // usings of the compiler (ImplicitUsings.Default), or IntelliSense would show no error for something that will not compile, and
    // Invoklet.Runtime (the dump and LINQPad compatibility API), which the compiled project gets from its profile.
    private static readonly string[] BaseUsings = [.. ImplicitUsings.Default, "Invoklet.Runtime"];

    // With nothing after the user's code, a query position at its very end coincides with the true end of the whole document, where
    // Roslyn's SemanticModel.LookupSymbols silently stops finding block-scoped locals and parameters (the position is the end-of-file
    // token, whose parent is the bare compilation unit). One space appended moves every legitimate query position strictly before the
    // end. It is whitespace, so it cannot introduce diagnostics or tokens; the code fix engine, which measures from the end of the
    // document, is told of it so that it does not leak into a fix.
    internal const string TrailingPad = " ";

    private readonly object _gate = new();
    private string[] _extraUsings = [];
    private MdRunKind _kind = MdRunKind.Statements;
    private CSharpParseOptions _parseOptions = CSharpScriptCompiler.ParseOptions(Environment.Version.Major, Environment.Version.Minor);
    private KindShape? _shape;

    public ScriptDocumentLayout() => Preamble = BuildPreamble([]);

    /// <summary>The <c>global using</c> lines that start the document, and a blank line.</summary>
    public string Preamble { get; private set; }

    public MdRunKind Kind
    {
        get
        {
            lock (_gate) return _kind;
        }
    }

    /// <summary>The options the build parses with (its target's language version and symbols); the wrapper is parsed with them as the build does.</summary>
    public CSharpParseOptions ParseOptions
    {
        get
        {
            lock (_gate) return _parseOptions;
        }
        set
        {
            lock (_gate) _parseOptions = value;
        }
    }

    /// <summary>Adds the usings of the script's own front matter to the base ones; false when that changes nothing.</summary>
    public bool SetExtraUsings(IReadOnlyList<string> usings)
    {
        var extra = usings
            .Where(u => !BaseUsings.Contains(u, StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        lock (_gate)
        {
            if (extra.SequenceEqual(_extraUsings, StringComparer.Ordinal)) return false;
            _extraUsings = extra;
            Preamble = BuildPreamble(extra);
            return true;
        }
    }

    /// <summary>False when the script already had this kind.</summary>
    public bool SetKind(MdRunKind kind)
    {
        lock (_gate)
        {
            if (_kind == kind) return false;
            _kind = kind;
            return true;
        }
    }

    /// <summary>The text of the analysis document for this code.</summary>
    public string FullText(string code)
    {
        var shape = ShapeFor(code);
        return ScriptDocumentProjection.Create(Preamble, shape.Prefix, shape.Projection.Text, shape.Suffix, TrailingPad).FullText;
    }

    /// <summary>
    /// The prefix the kind transform inserts before the code: for a program, the call of its Main in a wrapper class, mirrored so that
    /// Roslyn does not flag the user's Main as an unused local function. Depends on the code (whether Main is static, whether it
    /// returns a task). Expression is not mirrored: its transform also wraps the end of the code, which would desync the mapping.
    /// </summary>
    public string Prefix(string code) => ShapeFor(code).Prefix;

    /// <summary>The suffix that closes the wrapper class the prefix opens; without it the document would have an unclosed class.</summary>
    public string Suffix(string code) => ShapeFor(code).Suffix;

    /// <summary>The user's code as it stands in the document, with the map of what the program kind moved.</summary>
    public ProgramBodyProjection Projection(string code) => ShapeFor(code).Projection;

    /// <summary>The offset in the document of the start of the user's code: the preamble and, for a program, the wrapper's prefix.</summary>
    public int HeaderLength(string code) => Preamble.Length + Prefix(code).Length;

    /// <summary>The offset in the document of an offset in the user's code.</summary>
    public int ToDocumentOffset(string code, int offset) => HeaderLength(code) + Projection(code).ToProjected(offset);

    /// <summary>The offset in the user's code of an offset in the document; -1 when it is outside the code.</summary>
    public int ToUserOffset(string code, int offset)
    {
        var result = Projection(code).ToOriginal(offset - HeaderLength(code));
        return result < 0 || result > code.Length ? -1 : result;
    }

    /// <summary>The span in the user's code of a span in the document; null when it starts in the preamble or the prefix.</summary>
    public TextSpan? ToUserSpan(string code, TextSpan span)
    {
        var relativeStart = span.Start - HeaderLength(code);
        return relativeStart < 0 ? null : Projection(code).ToOriginal(new TextSpan(relativeStart, span.Length));
    }

    // The prefix, the projection and the suffix each parse the whole script, and the offset mapping runs once per classified span,
    // diagnostic or token: computed once per text, kind and parse options, not on every mapped offset.
    private KindShape ShapeFor(string code)
    {
        lock (_gate)
        {
            if (_shape is { } shape && shape.Code == code && shape.Options == _parseOptions && shape.Kind == _kind) return shape;
            return _shape = _kind == MdRunKind.Program
                ? new KindShape(code, _parseOptions, _kind,
                    MdRunKindTransform.ProgramPrefixFor(code, _parseOptions),
                    ProgramBodyProjection.Create(code, _parseOptions),
                    MdRunKindTransform.ProgramSuffixFor(code, _parseOptions))
                : new KindShape(code, _parseOptions, _kind, "", ProgramBodyProjection.Identity(code), "");
        }
    }

    private static string BuildPreamble(IEnumerable<string> extra)
    {
        var text = new StringBuilder();
        foreach (var u in BaseUsings) text.Append("global using ").Append(u).Append(";\n");
        foreach (var u in extra) text.Append("global using ").Append(u).Append(";\n");
        return text.Append('\n').ToString();
    }

    private sealed record KindShape(string Code, CSharpParseOptions Options, MdRunKind Kind,
        string Prefix, ProgramBodyProjection Projection, string Suffix);
}
