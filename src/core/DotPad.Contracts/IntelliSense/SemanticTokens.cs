namespace DotPad.Contracts.IntelliSense;

public enum SemanticTokenKind
{
    None,
    ClassName, StructName, InterfaceName, EnumName, DelegateName,
    TypeParameterName, MethodName, ExtensionMethodName,
    PropertyName, FieldName, EventName,
    NamespaceName, LocalName, ParameterName,
}

public interface ISemanticToken
{
    int StartOffset { get; }
    int Length { get; }
    SemanticTokenKind Kind { get; }
}
