using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace RunPad.Languages.DotnetCommon;

/// <summary>ILSpy's analyzer queries (the explorer's "Analyze"), by what is being analyzed.</summary>
public enum AnalysisKind
{
    /// <summary>Method/constructor/property/event: the code that calls it. Type: the code that refers to it.</summary>
    UsedBy,

    /// <summary>Method/constructor: the methods and fields its body refers to.</summary>
    Uses,

    /// <summary>Field: the code that reads it (or takes its address).</summary>
    ReadBy,

    /// <summary>Field: the code that assigns it.</summary>
    AssignedBy,

    /// <summary>Type: the code that creates instances of it (<c>newobj</c>).</summary>
    InstantiatedBy,

    /// <summary>Virtual/abstract member: the members that directly override it.</summary>
    OverriddenBy,

    /// <summary>Interface member: the members that implement it. Interface: the types that implement it.</summary>
    ImplementedBy,

    /// <summary>Type: the members whose signature (type, return type, parameters) mentions it.</summary>
    ExposedBy,
}

public static partial class CSharpDecompilerService
{
    /// <summary>
    /// The analyses ILSpy offers for a tree row of <paramref name="kind"/>, most useful first —
    /// Overridden By only for an overridable <paramref name="member"/>, Implemented By only for an
    /// interface's member (or an interface).
    /// </summary>
    public static IReadOnlyList<AnalysisKind> AnalysesFor(AssemblyNodeKind kind, AssemblyMemberEntry? member = null)
    {
        List<AnalysisKind> analyses = kind switch
        {
            AssemblyNodeKind.Method or AssemblyNodeKind.Constructor => [AnalysisKind.UsedBy, AnalysisKind.Uses],
            AssemblyNodeKind.Property or AssemblyNodeKind.Event => [AnalysisKind.UsedBy],
            AssemblyNodeKind.Field => [AnalysisKind.ReadBy, AnalysisKind.AssignedBy],
            AssemblyNodeKind.Class or AssemblyNodeKind.Struct =>
                [AnalysisKind.UsedBy, AnalysisKind.InstantiatedBy, AnalysisKind.ExposedBy],
            AssemblyNodeKind.Interface => [AnalysisKind.UsedBy, AnalysisKind.ImplementedBy, AnalysisKind.ExposedBy],
            AssemblyNodeKind.Enum or AssemblyNodeKind.Delegate => [AnalysisKind.UsedBy, AnalysisKind.ExposedBy],
            _ => [],
        };
        if (member is { IsOverridable: true }) analyses.Add(AnalysisKind.OverriddenBy);
        if (member is { IsInterfaceMember: true }) analyses.Add(AnalysisKind.ImplementedBy);
        return analyses;
    }

    /// <summary>
    /// Runs <paramref name="kind"/> for the type or member <paramref name="targetToken"/> of
    /// <paramref name="targetAssemblyPath"/>. <see cref="AnalysisKind.Uses"/> reads only the target's own
    /// body; every other kind reads the IL of every method of every assembly in
    /// <paramref name="assemblyPaths"/> (the explorer's loaded assemblies). Code the compiler generated
    /// for a member — lambdas, local functions, async/iterator state machines — counts as that member,
    /// like ILSpy. Entities are matched by assembly name + documentation ID rather than metadata token,
    /// so a reference assembly and its implementation (different tokens) still match. Results reuse
    /// <see cref="AssemblySearchEntry"/>, ordered by location then name, without duplicates.
    /// </summary>
    public static IReadOnlyList<AssemblySearchEntry> Analyze(
        AnalysisKind kind, string targetAssemblyPath, int targetToken, IReadOnlyList<string> assemblyPaths,
        CancellationToken cancellationToken = default)
    {
        var target = WithDecompiler(targetAssemblyPath, (decompiler, _) =>
            AnalysisTarget.Create(decompiler.TypeSystem.MainModule, targetToken));
        if (target is null) return [];

        var results = new Dictionary<string, AssemblySearchEntry>();
        if (kind == AnalysisKind.Uses)
        {
            WithDecompiler(targetAssemblyPath, (decompiler, _) =>
            {
                CollectUses(decompiler.TypeSystem.MainModule, targetToken, results, cancellationToken);
                return 0;
            });
        }
        else
        {
            foreach (var path in assemblyPaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                WithDecompiler(path, (decompiler, _) =>
                {
                    var module = decompiler.TypeSystem.MainModule;
                    if (kind is AnalysisKind.OverriddenBy or AnalysisKind.ImplementedBy or AnalysisKind.ExposedBy)
                        CollectByStructure(module, kind, target, path, results, cancellationToken);
                    else
                        CollectUsers(module, kind, target, path, results, cancellationToken);
                    return 0;
                });
            }
        }

        return results.Values
            .OrderBy(r => r.Location, StringComparer.Ordinal)
            .ThenBy(r => r.Text, StringComparer.Ordinal)
            .ToList();
    }

    // Who refers to the target: every method body of `module`, each hit reported as the user member
    // that owns the method (so a lambda inside Caller() reports Caller()).
    private static void CollectUsers(
        MetadataModule module, AnalysisKind kind, AnalysisTarget target, string assemblyPath,
        Dictionary<string, AssemblySearchEntry> results, CancellationToken cancellationToken)
    {
        foreach (var type in module.TypeDefinitions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var method in type.Methods)
            {
                if (!ReadsTarget(module, method, kind, target)) continue;
                if (ToResult(OwningMember(method), assemblyPath) is not { } result) continue;
                // a type's own code isn't "used by" for that type — only other code is
                if (kind == AnalysisKind.UsedBy && target.Type is { } targetType
                    && result.TypeFullName == targetType.ReflectionName) continue;
                results.TryAdd(ResultKey(result), result);
            }
        }
    }

    // Overridden By / Implemented By / Exposed By read the type system's structure, not IL: every
    // listed type of `module` and its declared members.
    private static void CollectByStructure(
        MetadataModule module, AnalysisKind kind, AnalysisTarget target, string assemblyPath,
        Dictionary<string, AssemblySearchEntry> results, CancellationToken cancellationToken)
    {
        foreach (var type in module.TypeDefinitions.Where(IsListed))
        {
            cancellationToken.ThrowIfCancellationRequested();
            IEnumerable<IEntity> hits = kind switch
            {
                AnalysisKind.OverriddenBy => type.Members.Where(m =>
                    m.IsOverride && InheritanceHelper.GetBaseMember(m) is { } overridden && target.MatchesSelf(overridden)),
                AnalysisKind.ImplementedBy when target.Type is not null =>
                    type.Kind != TypeKind.Interface
                    && type.GetAllBaseTypeDefinitions().Any(b => !b.Equals(type) && target.MatchesType(b))
                        ? [type]
                        : [],
                AnalysisKind.ImplementedBy => ImplementationsIn(type, target),
                AnalysisKind.ExposedBy => ListedMembers(type).Where(m => SignatureTypes(m).Any(target.MatchesTypeInside)),
                _ => [],
            };
            foreach (var hit in hits)
            {
                if (ToResult(hit, assemblyPath) is { } result)
                    results.TryAdd(ResultKey(result), result);
            }
        }
    }

    // The members of `type` that implement the target interface member: explicit implementations,
    // and the public member with the same name and parameters (after substituting the interface's
    // type arguments as `type` implements it). Implementations inherited from a base class aren't
    // looked for here — the base class reports them itself.
    private static IEnumerable<IMember> ImplementationsIn(ITypeDefinition type, AnalysisTarget target)
    {
        if (type.Kind == TypeKind.Interface || target.Member is not IMember) yield break;
        var implementedInterface = type.GetAllBaseTypes().FirstOrDefault(target.MatchesDeclaringType);
        if (implementedInterface is null) yield break;

        var specialized = implementedInterface
            .GetMembers(m => target.MatchesSelf(m.MemberDefinition), GetMemberOptions.IgnoreInheritedMembers)
            .FirstOrDefault();
        foreach (var member in type.Members)
        {
            if (member.ExplicitlyImplementedInterfaceMembers.Any(i => target.MatchesSelf(i.MemberDefinition)))
            {
                yield return member;
            }
            else if (specialized is not null && !member.IsExplicitInterfaceImplementation && !member.IsStatic
                     && member.Accessibility == Accessibility.Public && member.SymbolKind == specialized.SymbolKind
                     && member.Name == specialized.Name
                     && (member is not IParameterizedMember candidate || specialized is not IParameterizedMember wanted
                         || ParameterListComparer.Instance.Equals(candidate.Parameters, wanted.Parameters)))
            {
                yield return member;
            }
        }
    }

    // Every type a member's signature mentions at the top level: a field's/property's/event's type,
    // a method's return type, and every parameter type.
    private static IEnumerable<IType> SignatureTypes(IMember member)
    {
        if (member is not IMethod { IsConstructor: true }) yield return member.ReturnType;
        if (member is IParameterizedMember parameterized)
        {
            foreach (var parameter in parameterized.Parameters)
                yield return parameter.Type;
        }
    }

    private static bool ReadsTarget(MetadataModule module, IMethod method, AnalysisKind kind, AnalysisTarget target)
    {
        var found = false;
        ForEachOperand(module, method, (opCode, entity) =>
        {
            found |= kind switch
            {
                AnalysisKind.UsedBy when target.Type is null => IsCall(opCode) && target.Matches(entity),
                AnalysisKind.UsedBy => target.MatchesTypeOrMember(entity),
                AnalysisKind.InstantiatedBy => opCode == ILOpCode.Newobj && entity is IMethod ctor
                                               && target.MatchesType(ctor.DeclaringType),
                AnalysisKind.ReadBy => opCode is ILOpCode.Ldfld or ILOpCode.Ldsfld or ILOpCode.Ldflda or ILOpCode.Ldsflda
                                       && target.Matches(entity),
                AnalysisKind.AssignedBy => opCode is ILOpCode.Stfld or ILOpCode.Stsfld && target.Matches(entity),
                _ => false,
            };
            return !found; // stop at the first hit
        });
        return found;
    }

    private static bool IsCall(ILOpCode opCode) => opCode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj
        or ILOpCode.Ldftn or ILOpCode.Ldvirtftn or ILOpCode.Jmp;

    // What the target refers to: the target method's own body plus every compiler-generated method
    // that belongs to it (its lambdas, local functions, state machine). The target is resolved in THIS
    // module's type system: entities of two decompiler instances never compare equal.
    private static void CollectUses(
        MetadataModule module, int targetToken, Dictionary<string, AssemblySearchEntry> results,
        CancellationToken cancellationToken)
    {
        if (module.ResolveEntity(MetadataTokens.EntityHandle(targetToken)) is not IMethod targetMethod
            || targetMethod.DeclaringTypeDefinition is not { } owner) return;

        var bodies = new List<IMethod> { targetMethod };
        foreach (var nested in AllNestedTypes(owner).Prepend(owner))
        {
            bodies.AddRange(nested.Methods.Where(m =>
                !m.Equals(targetMethod) && IsCompilerGenerated(m) && OwningMember(m) is { } o && o.Equals(targetMethod)));
        }

        foreach (var body in bodies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ForEachOperand(module, body, (_, entity) =>
            {
                var used = entity switch
                {
                    IMethod m => OwningMember(m.MemberDefinition as IMethod ?? m),
                    IField f => f.MemberDefinition as IEntity ?? f,
                    _ => null,
                };
                if (used is not null && HasMetadata(used) && !used.Equals(targetMethod)
                    && used.ParentModule?.MetadataFile?.FileName is { } path
                    && ToResult(used, path) is { } result)
                {
                    results.TryAdd(ResultKey(result), result);
                }
                return true;
            });
        }
    }

    private static IEnumerable<ITypeDefinition> AllNestedTypes(ITypeDefinition type) =>
        type.NestedTypes.SelectMany(n => AllNestedTypes(n).Prepend(n));

    // Calls `visit` with every member/type operand of `method`'s IL, resolved (operands that can't be
    // resolved — an assembly that isn't on disk — are skipped). `visit` returns false to stop.
    private static void ForEachOperand(MetadataModule module, IMethod method, Func<ILOpCode, IEntity, bool> visit)
    {
        if (method.MetadataToken.IsNil || !method.HasBody) return;
        var metadataFile = module.MetadataFile;
        var definition = metadataFile.Metadata.GetMethodDefinition((MethodDefinitionHandle)method.MetadataToken);
        if (definition.RelativeVirtualAddress == 0) return;

        MethodBodyBlock body;
        try
        {
            body = metadataFile.GetMethodBody(definition.RelativeVirtualAddress);
        }
        catch (BadImageFormatException)
        {
            return;
        }

        var context = new GenericContext(method.DeclaringTypeDefinition?.TypeParameters ?? [], method.TypeParameters);
        var reader = body.GetILReader();
        while (reader.RemainingBytes > 0)
        {
            var opCode = ILParser.DecodeOpCode(ref reader);
            var operandType = opCode.GetOperandType();
            if (operandType is not (OperandType.Method or OperandType.Field or OperandType.Type or OperandType.Tok))
            {
                ILParser.SkipOperand(ref reader, opCode);
                continue;
            }

            var handle = MetadataTokens.EntityHandle(reader.ReadInt32());
            IEntity? entity;
            try
            {
                entity = handle.Kind switch
                {
                    HandleKind.TypeDefinition or HandleKind.TypeReference or HandleKind.TypeSpecification =>
                        module.ResolveType(handle, context).GetDefinition(),
                    _ => module.ResolveEntity(handle, context),
                };
            }
            catch (Exception ex) when (ex is BadImageFormatException or InvalidOperationException or NotSupportedException)
            {
                entity = null;
            }

            if (entity is not null && !visit(opCode, entity)) return;
        }
    }

    private static bool IsCompilerGenerated(IEntity entity) =>
        entity.Name.StartsWith('<') || (entity.DeclaringTypeDefinition is { } t && !IsListed(t));

    // The member the user wrote that `method` belongs to: an accessor → its property/event; a lambda,
    // local function or state machine method ("<Caller>b__0_0", "<Later>d__3.MoveNext") → the member
    // named between '<' and '>' in the nearest declaring type the user wrote. Null when nothing matches.
    private static IEntity? OwningMember(IMethod method)
    {
        if (method.AccessorOwner is { } accessorOwner) return accessorOwner;
        if (!IsCompilerGenerated(method)) return method;

        var name = GeneratedOwnerName(method.Name);
        ITypeDefinition? owner = method.DeclaringTypeDefinition;
        while (owner is not null && !IsListed(owner))
        {
            name ??= GeneratedOwnerName(owner.Name);
            owner = owner.DeclaringTypeDefinition;
        }
        if (owner is null) return null;
        if (name is null) return owner;

        var member = owner.Members.FirstOrDefault(m => m.Name == name);
        return member is IMethod { AccessorOwner: { } owningProperty } ? owningProperty : (IEntity?)member ?? owner;

        // "<Caller>b__0_0" → "Caller"; "<>c" / "<>c__DisplayClass0_0" → null (no name to go by)
        static string? GeneratedOwnerName(string generated) =>
            generated.StartsWith('<') && generated.IndexOf('>') is > 1 and var end ? generated[1..end] : null;
    }

    private static AssemblySearchEntry? ToResult(IEntity? entity, string assemblyPath)
    {
        switch (entity)
        {
            case ITypeDefinition type when IsListed(type):
                return new AssemblySearchEntry(type.Name, TypeLocation(type), type.FullTypeName.ReflectionName,
                    ToEntry(type, browsedAssembly: null), Member: null, assemblyPath);
            case IMember { DeclaringTypeDefinition: { } declaring } member when IsListed(declaring):
                var memberEntry = ToMemberEntry(declaring, member);
                return new AssemblySearchEntry(
                    member is IMethod { IsConstructor: true } ? declaring.Name : member.Name,
                    FullTypeText(declaring), declaring.FullTypeName.ReflectionName,
                    ToEntry(declaring, browsedAssembly: null), memberEntry, assemblyPath);
            default:
                return null;
        }

        static string TypeLocation(ITypeDefinition type) =>
            type.DeclaringTypeDefinition is { } outer ? FullTypeText(outer) : type.Namespace;
    }

    private static string ResultKey(AssemblySearchEntry result) =>
        $"{result.AssemblyPath}|{result.TypeFullName}|{result.Member?.MetadataToken}";

    // The analyzed entity, keyed by assembly name + documentation ID so it matches across assemblies.
    private sealed class AnalysisTarget
    {
        private readonly HashSet<string> _memberKeys = [];
        private string? _typeKey;
        private string? _selfKey; // the member itself (a property, not its accessors)
        private string? _declaringTypeKey;

        public IEntity? Member { get; private init; }
        public ITypeDefinition? Type { get; private init; }

        public static AnalysisTarget? Create(MetadataModule module, int token)
        {
            var entity = module.ResolveEntity(MetadataTokens.EntityHandle(token));
            if (entity is ITypeDefinition type)
                return new AnalysisTarget { Type = type, _typeKey = Key(type) };
            if (entity is not IMember member) return null;

            var target = new AnalysisTarget
            {
                Member = member,
                _selfKey = Key(member),
                _declaringTypeKey = member.DeclaringTypeDefinition is { } declaring ? Key(declaring) : null,
            };
            switch (member)
            {
                case IProperty property: // calls of its accessors
                    target.AddKeys(property.Getter, property.Setter);
                    break;
                case IEvent @event:
                    target.AddKeys(@event.AddAccessor, @event.RemoveAccessor, @event.InvokeAccessor);
                    break;
                default:
                    target.AddKeys(member);
                    break;
            }
            return target;
        }

        // the target member itself (Overridden By / Implemented By compare members, not accessors)
        public bool MatchesSelf(IEntity entity) =>
            _selfKey is not null && entity is IMember member && Key(member.MemberDefinition) == _selfKey;

        // a (possibly constructed) type whose definition declares the target member
        public bool MatchesDeclaringType(IType type) =>
            _declaringTypeKey is not null && type.GetDefinition() is { } definition && Key(definition) == _declaringTypeKey;

        // the target type anywhere in a signature type: Animal, Animal[], List<Animal>, ref Animal, ...
        public bool MatchesTypeInside(IType type) => type switch
        {
            ParameterizedType parameterized =>
                MatchesTypeInside(parameterized.GenericType) || parameterized.TypeArguments.Any(MatchesTypeInside),
            ICSharpCode.Decompiler.TypeSystem.Implementation.TypeWithElementType withElement =>
                MatchesTypeInside(withElement.ElementType),
            _ => MatchesType(type),
        };

        private void AddKeys(params IEntity?[] entities)
        {
            foreach (var entity in entities)
            {
                if (entity is not null && Key(entity) is { } key) _memberKeys.Add(key);
            }
        }

        public bool Matches(IEntity entity) =>
            entity is IMember member && Key(member.MemberDefinition) is { } key && _memberKeys.Contains(key);

        public bool MatchesType(IType? type) =>
            type?.GetDefinition() is { } definition && Key(definition) is { } key && key == _typeKey;

        // a reference to the type itself or to any of its members
        public bool MatchesTypeOrMember(IEntity entity) => entity switch
        {
            ITypeDefinition type => Key(type) is { } key && key == _typeKey,
            IMember member => MatchesType(member.DeclaringType),
            _ => false,
        };

        // null for entities with no metadata behind them (an array's Get/Set/Address, a stand-in for
        // an unresolvable reference): they have no documentation ID and can't be the target anyway
        private static string? Key(IEntity entity) => HasMetadata(entity)
            ? (entity.ParentModule?.AssemblyName ?? "") + "|" + IdStringProvider.GetIdString(entity)
            : null;
    }

    // IdStringProvider.GetIdString's own precondition: a module backed by a file (not a stand-in for
    // an assembly the resolver couldn't find) and a real metadata token
    private static bool HasMetadata(IEntity entity) =>
        entity.ParentModule?.MetadataFile is not null && !entity.MetadataToken.IsNil;
}
