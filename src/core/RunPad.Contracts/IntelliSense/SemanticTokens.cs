namespace RunPad.Contracts.IntelliSense;

public enum SemanticTokenKind
{
    None,
    ClassName, StructName, InterfaceName, EnumName, DelegateName,
    TypeParameterName, MethodName, ExtensionMethodName,
    PropertyName, FieldName, EventName,
    NamespaceName, LocalName, ParameterName,

    // Not produced by background semantic-token analysis (keywords are highlighted by the editor's
    // TextMate grammar there instead) — only used when building DisplayTextSegment signatures for
    // completion/signature-help/quick-info tooltips, which render outside the TextMate pipeline.
    Keyword,
}

public interface ISemanticToken
{
    int StartOffset { get; }
    int Length { get; }
    SemanticTokenKind Kind { get; }
}

// A single classified run of text used to render a member/method signature with syntax highlighting
// in completion/signature-help/quick-info tooltips. Emphasis marks the
// currently-active parameter in a signature-help header.
public readonly record struct DisplayTextSegment(string Text, SemanticTokenKind Kind, bool Emphasis = false);
