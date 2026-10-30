using Avalonia.Media;
using Invoklet.Languages.DotnetCommon;

namespace Invoklet.Windows.Models;

/// <summary>
/// One row of <c>AssemblyExplorerWindow</c>'s ILSpy-style tree: a namespace, a type (top-level, nested,
/// base or derived), a member, or a "Base Types"/"Derived Types" group. <see cref="Type"/>/<see cref="Member"/>
/// say what selecting the row decompiles; <see cref="AssemblyPath"/> is the assembly to decompile it from.
/// </summary>
public sealed class AssemblyTreeNode(
    AssemblyNodeKind kind, string text, AssemblyAccess access, AssemblyTreeNodeIcons icons,
    bool isStatic = false, bool isAbstract = false,
    string? assemblyPath = null, AssemblyTypeEntry? type = null, AssemblyMemberEntry? member = null,
    string? location = null, AssemblyReferenceEntry? reference = null, bool isUnavailable = false,
    AssemblyResourceEntry? resource = null)
{
    /// <summary>A Resources row: the manifest resource it shows.</summary>
    public AssemblyResourceEntry? Resource { get; } = resource;

    /// <summary>A References row: the referenced assembly (activating it loads it into the tree).</summary>
    public AssemblyReferenceEntry? Reference { get; } = reference;

    /// <summary>Greyed like non-public API: a reference whose file can't be found.</summary>
    public bool IsUnavailable { get; } = isUnavailable;

    /// <summary>Search results only: where the hit is declared (namespace or declaring type).</summary>
    public string? Location { get; } = location;

    public AssemblyNodeKind Kind { get; } = kind;
    public string Text { get; } = text;
    public AssemblyAccess Access { get; } = access;
    public bool IsStatic { get; } = isStatic;
    public bool IsAbstract { get; } = isAbstract;
    public string? AssemblyPath { get; } = assemblyPath;
    public AssemblyTypeEntry? Type { get; } = type;
    public AssemblyMemberEntry? Member { get; } = member;

    public Geometry? Icon { get; } = icons.Icon;
    public IBrush IconBrush { get; } = icons.IconBrush;

    /// <summary>ILSpy's bottom-right access overlay: none for public, a lock, a key or a house otherwise.</summary>
    public Geometry? AccessOverlay { get; } = icons.AccessOverlay;

    /// <summary>ILSpy's top-left "static" overlay.</summary>
    public Geometry? StaticOverlay { get; } = icons.StaticOverlay;

    public bool IsPublic => Access is AssemblyAccess.Public or AssemblyAccess.Protected or AssemblyAccess.ProtectedInternal;

    /// <summary>ILSpy greys private/internal API.</summary>
    public double Opacity => IsPublic && !IsUnavailable ? 1.0 : 0.55;

    /// <summary>Abstract types/members are italic, like the abstract members of a class diagram.</summary>
    public FontStyle FontStyle => IsAbstract ? FontStyle.Italic : FontStyle.Normal;

    public override string ToString() => Text;
}

/// <summary>The resolved glyphs of a node — looked up from the window's resources.</summary>
public sealed record AssemblyTreeNodeIcons(
    Geometry? Icon, IBrush IconBrush, Geometry? AccessOverlay, Geometry? StaticOverlay);
