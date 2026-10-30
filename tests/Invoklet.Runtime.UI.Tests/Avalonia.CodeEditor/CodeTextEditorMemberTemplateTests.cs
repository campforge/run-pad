using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Invoklet.Avalonia.CodeTextEditor;
using Invoklet.Languages.CSharp.IntelliSense.Completion;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.Avalonia.CodeEditor;
// C# no longer inserts '}' after '{'; in a new type body Enter offers the property template instead,
// which changes the text only when Tab accepts it. The position check is the real C# one.
public class CodeTextEditorMemberTemplateTests
{
    private const string Prop = "public ${1:int} ${2:Property} { get; set; }${0}";

    private static (Window Window, CodeTextEditor Editor) Show(string text)
    {
        var editor = new CodeTextEditor
        {
            Text = text,
            MemberTemplateSnippet = "prop",
            MemberTemplateTitle = "Insert property",
            MemberTemplatePositionProvider = (code, offset, _) =>
                Task.FromResult(CSharpMemberPosition.IsInEmptyTypeBody(code, offset)),
        };
        editor.Snippets["prop"] = Prop;
        editor.BracePairs['('] = ')';
        editor.BracePairs['['] = ']';
        editor.BracePairs['{'] = '}';
        editor.AutoCloseBraces.Add('[');
        var window = new Window { Width = 800, Height = 400, Content = editor };
        window.Show();
        editor.TextArea.Focus();
        editor.CaretOffset = text.Length;
        return (window, editor);
    }

    private static async Task EnterAsync(Window window, CodeTextEditor editor)
    {
        window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
        await editor.MemberTemplateOfferTask;
        Dispatcher.UIThread.RunJobs();
    }

    private static string Selected(CodeTextEditor editor) => editor.TextArea.Selection.GetText();

    [AvaloniaFact]
    public void OpeningBrace_Should_NotInsertAClosingOne_WhileSquareBracketsStillPair()
    {
        var (window, editor) = Show("");
        try
        {
            window.KeyTextInput("{");
            Assert.Equal("{", editor.Text);
            window.KeyTextInput("[");
            Assert.Equal("{[]", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Enter_InAnEmptyClassBody_Should_OfferTheTemplate_AndTabShouldInsertIt()
    {
        var (window, editor) = Show("class C\n{");
        try
        {
            await EnterAsync(window, editor);
            Assert.True(editor.IsMemberTemplateOfferOpen);
            Assert.True(editor.MemberTemplateOfferForTests!.IsOpen);
            Assert.Equal("Insert property (Tab)", editor.MemberTemplateOfferForTests.Hint.Text);
            Assert.Equal("class C\n{\n", editor.Text); // nothing inserted yet

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.False(editor.IsMemberTemplateOfferOpen);
            Assert.Equal("class C\n{\npublic int Property { get; set; }", editor.Text);
            Assert.Equal("int", Selected(editor));

            window.KeyTextInput("string");
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("Property", Selected(editor));
            window.KeyTextInput("Name");
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); // leaves the snippet
            Assert.Equal("class C\n{\npublic string Name { get; set; }", editor.Text);
            Assert.Equal(editor.Text.Length, editor.CaretOffset);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task AcceptedTemplate_Should_BeOneUndoStep()
    {
        var (window, editor) = Show("class C\n{");
        try
        {
            await EnterAsync(window, editor);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            editor.Undo();
            Assert.Equal("class C\n{\n", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Escape_Should_DismissTheOffer_AndTabShouldThenIndentAsBefore()
    {
        var (window, editor) = Show("class C\n{");
        try
        {
            await EnterAsync(window, editor);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.False(editor.IsMemberTemplateOfferOpen);
            Assert.Equal("class C\n{\n", editor.Text);

            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.DoesNotContain("Property", editor.Text);
            Assert.True(editor.Text.Length > "class C\n{\n".Length); // indentation, as without an offer
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Typing_Should_DismissTheOffer_AndTypeAsUsual()
    {
        var (window, editor) = Show("class C\n{");
        try
        {
            await EnterAsync(window, editor);
            window.KeyPressQwerty(PhysicalKey.V, RawInputModifiers.None);
            window.KeyTextInput("v");
            Assert.False(editor.IsMemberTemplateOfferOpen);
            Assert.Equal("class C\n{\nv", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task MovingTheCaret_Should_DismissTheOffer()
    {
        var (window, editor) = Show("class C\n{");
        try
        {
            await EnterAsync(window, editor);
            editor.CaretOffset = 0;
            Assert.False(editor.IsMemberTemplateOfferOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("class C\n{\n    void M()\n    {")] // a method body
    [InlineData("class C\n{\n    int X;")] // the type already has a member
    [InlineData("var p = new P\n{")] // an object initializer
    public async Task Enter_Elsewhere_Should_OfferNothing(string text)
    {
        var (window, editor) = Show(text);
        try
        {
            await EnterAsync(window, editor);
            Assert.False(editor.IsMemberTemplateOfferOpen);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.DoesNotContain("Property", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TypedAbbreviation_Should_ExpandWithFields()
    {
        var (window, editor) = Show("class C\n{\n    prop");
        try
        {
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("class C\n{\n    public int Property { get; set; }", editor.Text);
            Assert.Equal("int", Selected(editor));
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Assert.Equal("Property", Selected(editor));

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            editor.Undo();
            Assert.Equal("class C\n{\n    prop", editor.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void Preview_Should_ShowFieldsAsTheirDefaults()
    {
        Assert.Equal("public int Property { get; set; }", SnippetTemplate.Preview(Prop));
        Assert.Null(SnippetTemplate.TryCreate("for (int i = 0; i < ${0}; i++)"));
        Assert.NotNull(SnippetTemplate.TryCreate(Prop));
    }
}
