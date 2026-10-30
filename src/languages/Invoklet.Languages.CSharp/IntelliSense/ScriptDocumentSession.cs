using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>
/// The workspace of the analysis of one script: one project with the script's document, the .editorconfig that carries the editor's
/// indentation, and the document kept in step with the editor's text. It owns the lock that serialises everything that changes
/// the solution (completion, signature help, quick info and the background analysis each run on their own thread), the baseline the
/// sync diffs against and the count of changes to the project's context. What the document contains comes from the
/// <see cref="ScriptDocumentLayout"/>; what the project's references and options are is for whoever changes them under <see cref="SyncLock"/>.
/// </summary>
internal sealed class ScriptDocumentSession : IDisposable
{
    // Where the script document and its .editorconfig appear to be; nothing is stored there.
    private static readonly string ScriptFolder = Path.Combine(Path.GetTempPath(), "InvokletScript");

    private readonly ScriptDocumentLayout _layout;
    private readonly DocumentId _editorConfigId;

    // The full text last handed to the workspace and the SourceText it produced, kept beside the code they were made from so that a
    // sync hands Roslyn a minimal TextChange against the previous SourceText instead of a fresh string on every keystroke.
    // SourceText.WithChanges keeps the lineage, which lets Roslyn's incremental lexer and parser reuse the syntax outside the edited
    // span: about seven times faster parsing and two to three times faster completion on scripts of 200 to 1000 lines.
    private string _lastCode = "";
    private string _lastFullText = "";
    private SourceText? _lastSourceText;

    private int _contextVersion;
    private bool _useTabs;
    private int _indentationSize = 4;

    public ScriptDocumentSession(ScriptDocumentLayout layout, IReadOnlyList<MetadataReference> references)
    {
        _layout = layout;
        Workspace = new AdhocWorkspace(InvokletRoslynHost.HostServices);

        // The preamble, the wrapper and the pad are built with a literal '\n' on every OS, but Roslyn's FormattingOptions.NewLine
        // defaults to Environment.NewLine ("\r\n" on Windows) when a workspace does not set it. A code fix that reformats whitespace it
        // touches then emitted "\r\n" where the document only had "\n", which on Windows moved the start of its edit back across the
        // preamble and made the code fix engine discard the whole fix as unsafe: it vanished on one OS only. Forcing "\n" makes
        // Roslyn's formatting match what the document contains everywhere.
        Workspace.TryApplyChanges(Workspace.CurrentSolution.WithOptions(
            Workspace.CurrentSolution.Options.WithChangedOption(FormattingOptions.NewLine, LanguageNames.CSharp, "\n")));

        var projectId = ProjectId.CreateNewId("InvokletScript");
        DocumentId = DocumentId.CreateNewId(projectId, "Script.cs");
        Workspace.AddProject(ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            name: "InvokletScript",
            assemblyName: "InvokletScript",
            language: LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable),
            parseOptions: layout.ParseOptions,
            metadataReferences: references));
        Workspace.AddDocument(DocumentInfo.Create(
            DocumentId,
            "Script.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(layout.Preamble, Encoding.UTF8), VersionStamp.Create())),
            filePath: Path.Combine(ScriptFolder, "Script.cs")));

        // Code actions format what they write with the options an .editorconfig beside the document gives, not with the options passed
        // to Formatter: this one carries the editor's indentation (UpdateIndentation). It is never written to disk.
        _editorConfigId = DocumentId.CreateNewId(projectId, ".editorconfig");
        Workspace.TryApplyChanges(Workspace.CurrentSolution.AddAnalyzerConfigDocument(
            _editorConfigId, ".editorconfig", SourceText.From(EditorConfig(_useTabs, _indentationSize)),
            filePath: Path.Combine(ScriptFolder, ".editorconfig")));
    }

    public AdhocWorkspace Workspace { get; }

    public DocumentId DocumentId { get; }

    /// <summary>
    /// Held by whoever reads or changes the solution as a whole: it guards the current solution and the baseline against concurrent
    /// syncs (a completion request for the text at keystroke N could otherwise race a background pass for keystroke N-2).
    /// </summary>
    public object SyncLock { get; } = new();

    /// <summary>The text last synced, as a SourceText of the lineage Roslyn reuses; for tests that check the incremental path was taken.</summary>
    internal SourceText? LastSyncedSourceText => _lastSourceText;

    /// <summary>Counts the changes to the project's references, options and generated documents; changed under <see cref="SyncLock"/>.</summary>
    public int ContextVersion
    {
        get
        {
            lock (SyncLock) return _contextVersion;
        }
    }

    /// <summary>Whether a pass that began at <paramref name="version"/> still describes the solution (it publishes nothing otherwise).</summary>
    public bool IsCurrent(int version)
    {
        lock (SyncLock) return version == _contextVersion;
    }

    /// <summary>The script's document at its current text, whatever the sync made of it.</summary>
    public Project Project => Workspace.CurrentSolution.GetProject(DocumentId.ProjectId)!;

    /// <summary>The project's context changed (references, options, generated documents): the document is synced again next time and
    /// a pass that was running describes an obsolete solution. The text did not change, so the baseline is kept.</summary>
    public void ContextChanged()
    {
        lock (SyncLock)
        {
            _lastCode = "";
            _contextVersion++;
        }
    }

    /// <summary>
    /// What the document is made of changed (the preamble, the kind): the next sync builds the whole text again and does not diff
    /// against a baseline that no longer describes what the workspace is about to hold.
    /// </summary>
    public void InvalidateBaseline()
    {
        lock (SyncLock)
        {
            _lastCode = "";
            _lastFullText = "";
            _lastSourceText = null;
        }
    }

    /// <summary>Brings the document to the text of this code; the current document when it was there already.</summary>
    public Document Sync(string code)
    {
        lock (SyncLock)
        {
            if (_lastCode != code)
            {
                var newFullText = _layout.FullText(code);

                // A minimal TextChange against the previous SourceText; only the very first sync, and the first after a forced reset, falls
                // back to a full SourceText.From, as there is no earlier lineage to diff against.
                var text = _lastSourceText is null
                    ? SourceText.From(newFullText, Encoding.UTF8)
                    : _lastSourceText.WithChanges(ScriptDocumentProjection.ComputeMinimalChange(_lastFullText, newFullText));

                // Remembered only once the workspace reflects it: TryApplyChanges can fail (a solution-version conflict from a concurrent
                // update), and remembering the code regardless left the workspace on stale text while claiming to be in sync, which skipped
                // the sync for ever on every later call with that code.
                if (Workspace.TryApplyChanges(Workspace.CurrentSolution.WithDocumentText(DocumentId, text)))
                {
                    _lastCode = code;
                    _lastFullText = newFullText;
                    _lastSourceText = text;
                }
            }

            return Workspace.CurrentSolution.GetDocument(DocumentId)!;
        }
    }

    /// <summary>The solution's options with the editor's indentation: for code that formats outside the workspace.</summary>
    public OptionSet FormattingOptionsSnapshot() => Workspace.CurrentSolution.Options
        .WithChangedOption(FormattingOptions.UseTabs, LanguageNames.CSharp, _useTabs)
        .WithChangedOption(FormattingOptions.TabSize, LanguageNames.CSharp, _indentationSize)
        .WithChangedOption(FormattingOptions.IndentationSize, LanguageNames.CSharp, _indentationSize);

    public void UpdateIndentation(bool useTabs, int indentationSize)
    {
        lock (SyncLock)
        {
            // Applying solution options writes shared Roslyn services; keep each editor's policy local.
            _useTabs = useTabs;
            _indentationSize = Math.Clamp(indentationSize, 1, 16);
            Workspace.TryApplyChanges(Workspace.CurrentSolution.WithAnalyzerConfigDocumentText(
                _editorConfigId, SourceText.From(EditorConfig(_useTabs, _indentationSize))));
        }
    }

    public void Dispose() => Workspace.Dispose();

    private static string EditorConfig(bool useTabs, int indentationSize) =>
        $"root = true\n[*.cs]\nindent_style = {(useTabs ? "tab" : "space")}\nindent_size = {indentationSize}\ntab_width = {indentationSize}\n";
}
