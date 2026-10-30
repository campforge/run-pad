using System.Reflection.Emit;
using Invoklet.Avalonia.CodeTextEditor;
using TextMateSharp.Grammars;
using TextMateSharp.Registry;
using Xunit;

namespace Invoklet.NonUI.Tests;

// The ILAsm grammar behind the Assembly Explorer's IL view and the editor's IL tab: tokens are checked
// by the TextMate scopes the grammar assigns, colours by what the real themes make of those scopes.
public class IlGrammarTests
{
    private sealed record Token(string Text, IReadOnlyList<string> Scopes)
    {
        public bool Has(string scopePrefix) => Scopes.Any(s => s.StartsWith(scopePrefix, StringComparison.Ordinal));
    }

    private static Registry CreateRegistry(ThemeName theme) =>
        new(new IlAwareRegistryOptions(new RegistryOptions(theme)));

    private static List<List<Token>> Tokenize(params string[] lines)
    {
        var grammar = CreateRegistry(ThemeName.DarkPlus).LoadGrammar(IlGrammar.ScopeName);
        Assert.NotNull(grammar);
        var result = new List<List<Token>>();
        IStateStack? state = null;
        foreach (var line in lines)
        {
            var tokenized = grammar.TokenizeLine(line, state, TimeSpan.MaxValue);
            result.Add(tokenized.Tokens
                .Select(t => new Token(line.Substring(t.StartIndex, Math.Min(t.EndIndex, line.Length) - t.StartIndex), t.Scopes))
                .ToList());
            state = tokenized.RuleStack;
        }
        return result;
    }

    // Plain text between matches is one token, so an unscoped word may only be found inside it.
    private static Token Find(List<Token> line, string text) =>
        line.FirstOrDefault(t => t.Text == text) ?? line.First(t => t.Text.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void Grammar_Should_BeEmbeddedAndLoadable()
    {
        var raw = IlGrammar.Load();

        Assert.Equal(IlGrammar.ScopeName, raw.GetScopeName());
        Assert.NotNull(CreateRegistry(ThemeName.DarkPlus).LoadGrammar(IlGrammar.ScopeName));
    }

    [Fact]
    public void MethodHeader_Should_ScopeDirectiveModifiersAndTypes()
    {
        var line = Tokenize(".method public hidebysig instance void Run() cil managed")[0];

        Assert.True(Find(line, ".method").Has("keyword.control.directive"));
        Assert.True(Find(line, "public").Has("storage.modifier"));
        Assert.True(Find(line, "hidebysig").Has("storage.modifier"));
        Assert.True(Find(line, "instance").Has("storage.modifier"));
        Assert.True(Find(line, "void").Has("storage.type"));
        Assert.True(Find(line, "cil").Has("storage.modifier"));
        Assert.False(Find(line, "Run").Has("keyword"));
    }

    [Fact]
    public void Instruction_Should_ScopeLabelOpcodeReferenceAndConstructor()
    {
        var line = Tokenize("    IL_0001: call instance void [System.Runtime]System.Object::.ctor()")[0];

        Assert.True(Find(line, "IL_0001").Has("entity.name.label"));
        Assert.True(Find(line, "call").Has("keyword.control.opcode"));
        Assert.True(Find(line, "[System.Runtime]").Has("entity.name.namespace"));
        Assert.True(Find(line, ".ctor").Has("entity.name.function"));
        Assert.True(Find(line, "::").Has("punctuation"));
    }

    [Theory]
    [InlineData("ldarg.0")]
    [InlineData("ldc.i4.s")]
    [InlineData("ldc.i4")]
    [InlineData("brfalse.s")]
    [InlineData("callvirt")]
    [InlineData("ret")]
    public void DottedOpcodes_Should_BeOneTokenEach(string opcode)
    {
        var line = Tokenize($"IL_0000: {opcode}")[0];

        Assert.True(Find(line, opcode).Has("keyword.control.opcode"));
    }

    [Fact]
    public void EveryFrameworkOpcode_Should_BeRecognised()
    {
        var names = typeof(OpCodes).GetFields().Select(f => ((OpCode)f.GetValue(null)!).Name!).Distinct().ToList();

        var missed = Tokenize(names.Select(n => $"IL_0000: {n} ").ToArray())
            .Zip(names)
            .Where(pair => !pair.First.Any(t => t.Text == pair.Second && t.Has("keyword.control.opcode")))
            .Select(pair => pair.Second)
            .ToList();

        Assert.Empty(missed);
    }

    [Fact]
    public void Literals_Should_ScopeStringsNumbersAndGenericParameters()
    {
        var lines = Tokenize(
            "ldstr \"a\\n\\\"q\\\"\"",
            "ldc.i4 0x1F",
            "ldc.r8 -3.5e2",
            "ldc.i4.s 12",
            "call !!0 [mscorlib]X::Get<!0>()");

        Assert.Contains(lines[0], t => t.Has("string.quoted"));
        Assert.Contains(lines[0], t => t.Has("constant.character.escape"));
        Assert.True(Find(lines[1], "0x1F").Has("constant.numeric.hex"));
        Assert.True(Find(lines[2], "-3.5e2").Has("constant.numeric"));
        Assert.True(Find(lines[3], "12").Has("constant.numeric.decimal"));
        Assert.True(Find(lines[4], "!!0").Has("entity.name.type.parameter"));
        Assert.True(Find(lines[4], "!0").Has("entity.name.type.parameter"));
    }

    [Fact]
    public void Comments_Should_NotContainCodeTokens_AndBlockCommentsSpanLines()
    {
        var lines = Tokenize(
            "ret // call .method IL_0001: 0x1F \"x\"",
            "/* start .class",
            "   still ldarg.0 inside */ nop");

        Assert.True(Find(lines[0], "ret").Has("keyword.control.opcode"));
        Assert.All(lines[0].Where(t => t.Text != "ret" && t.Text.Trim().Length > 0), t => Assert.True(t.Has("comment.line")));
        Assert.All(lines[1], t => Assert.True(t.Has("comment.block")));
        Assert.True(lines[2].Where(t => t.Text.Contains("ldarg")).All(t => t.Has("comment.block")));
        Assert.True(Find(lines[2], "nop").Has("keyword.control.opcode"));
        Assert.False(Find(lines[2], "nop").Has("comment"));
    }

    [Fact]
    public void Identifiers_Should_NotBeMistakenForOpcodesOrKeywords()
    {
        var line = Tokenize("call void Foo::call_me() Public ret_value")[0];

        Assert.False(Find(line, "call_me").Has("keyword"));
        Assert.False(Find(line, "Public").Has("storage"));
        Assert.False(Find(line, "ret_value").Has("keyword"));
    }

    private static string? ColorOf(Registry registry, Token token)
    {
        var rule = registry.GetTheme().Match(token.Scopes.ToList());
        var foreground = rule.Select(r => r.foreground).FirstOrDefault(f => f > 0);
        return foreground > 0 ? registry.GetTheme().GetColor(foreground) : null;
    }

    [Fact]
    public void TokenColours_Should_FollowTheTheme_AndSeparateTokenKinds()
    {
        string?[] Colours(ThemeName theme)
        {
            var registry = CreateRegistry(theme);
            registry.SetTheme(new RegistryOptions(theme).LoadTheme(theme));
            var grammar = registry.LoadGrammar(IlGrammar.ScopeName);
            var tokens = grammar.TokenizeLine("IL_0000: ldstr \"s\" // note", null, TimeSpan.MaxValue).Tokens
                .Select(t => new Token("", t.Scopes)).ToList();
            return
            [
                ColorOf(registry, tokens.First(t => t.Has("keyword.control.opcode"))),
                ColorOf(registry, tokens.First(t => t.Has("string.quoted"))),
                ColorOf(registry, tokens.First(t => t.Has("comment.line"))),
            ];
        }

        var dark = Colours(ThemeName.DarkPlus);
        var light = Colours(ThemeName.LightPlus);

        Assert.All(dark, c => Assert.NotNull(c));
        Assert.Equal(3, dark.Distinct().Count());
        Assert.NotEqual(dark, light);
    }
}
