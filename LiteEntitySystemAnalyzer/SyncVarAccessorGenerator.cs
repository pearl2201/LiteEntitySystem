using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace LiteEntitySystemAnalyzer
{
    /// <summary>
    /// Generates typed field accessors for every type that declares synchronization fields
    /// (<c>SyncVar&lt;T&gt;</c> and <c>SyncableField</c>).
    /// </summary>
    /// <remarks>
    /// This replaces the old IL/reflection based <c>RefMagic</c> field access. Runtime field offsets and
    /// managed-reference-to-pointer conversions are not available in IL2CPP/WebGL builds, so accessors are
    /// emitted at compile time instead.
    /// </remarks>
    [Generator]
    public sealed class SyncVarAccessorGenerator : ISourceGenerator
    {
        private const string InternalBaseClassMetadataName = "LiteEntitySystem.Internal.InternalBaseClass";
        private const string SyncVarMetadataName = "LiteEntitySystem.SyncVar`1";
        private const string SyncableFieldMetadataName = "LiteEntitySystem.SyncableField";

        private static readonly DiagnosticDescriptor TypeMustBePartial = new DiagnosticDescriptor(
            "LES0002",
            "Type with synchronization fields must be partial",
            "Type '{0}' declares synchronization fields and must be declared as 'partial' (including all containing " +
            "types) so that LiteEntitySystem can generate field accessors for it",
            "Rules",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly DiagnosticDescriptor SyncVarCannotBeReadonly = new DiagnosticDescriptor(
            "LES0003",
            "SyncVar field cannot be readonly",
            "SyncVar field '{0}.{1}' cannot be readonly. Remove the 'readonly' modifier so that the synchronized value " +
            "can be updated",
            "Rules",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);

        private static readonly SymbolDisplayFormat FullyQualified = SymbolDisplayFormat.FullyQualifiedFormat;

        public void Initialize(GeneratorInitializationContext context) =>
            context.RegisterForSyntaxNotifications(() => new ClassReceiver());

        public void Execute(GeneratorExecutionContext context)
        {
            if (!(context.SyntaxReceiver is ClassReceiver receiver))
                return;

            var compilation = context.Compilation;
            var internalBaseClass = compilation.GetTypeByMetadataName(InternalBaseClassMetadataName);
            var syncVarType = compilation.GetTypeByMetadataName(SyncVarMetadataName);
            var syncableFieldType = compilation.GetTypeByMetadataName(SyncableFieldMetadataName);
            if (internalBaseClass == null || syncVarType == null || syncableFieldType == null)
                return;

            var processedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

            foreach (var declaration in receiver.Declarations)
            {
                var semanticModel = compilation.GetSemanticModel(declaration.SyntaxTree);
                if (!(semanticModel.GetDeclaredSymbol(declaration) is INamedTypeSymbol type))
                    continue;
                if (type.TypeKind != TypeKind.Class || type.IsStatic)
                    continue;
                if (!processedTypes.Add(type))
                    continue;
                if (!IsDerivedFrom(type, internalBaseClass))
                    continue;

                var fields = CollectFields(type, syncVarType, syncableFieldType);
                if (fields.Count == 0)
                    continue;

                //A hand written override wins: the type does not have to be partial in that case.
                if (DeclaresAccessorOverride(type))
                    continue;

                if (!ValidateFields(context, type, fields))
                    continue;

                if (!IsPartialWithContainingTypes(type))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        TypeMustBePartial, declaration.Identifier.GetLocation(), type.Name));
                    continue;
                }

                //'protected internal' collapses to 'protected' outside the assembly that declares the base member,
                //and an override must match the accessibility as seen from its own assembly.
                var overrideModifiers = SymbolEqualityComparer.Default.Equals(
                    type.ContainingAssembly, internalBaseClass.ContainingAssembly)
                    ? "protected internal"
                    : "protected";

                context.AddSource(GetHintName(type), SourceText.From(Emit(type, fields, overrideModifiers), Encoding.UTF8));
            }
        }

        /// <summary>
        /// True when the type already declares (or inherits an override of) <c>RegisterSyncVarAccessors</c> itself,
        /// which means the user wants to provide the accessors by hand and the generator must stay out of the way.
        /// </summary>
        private static bool DeclaresAccessorOverride(INamedTypeSymbol type)
        {
            foreach (var member in type.GetMembers("RegisterSyncVarAccessors"))
            {
                if (member is IMethodSymbol method && method.DeclaringSyntaxReferences.Length > 0)
                    return true;
            }
            return false;
        }

        private static bool ValidateFields(GeneratorExecutionContext context, INamedTypeSymbol type, List<FieldInfo> fields)
        {
            var valid = true;
            foreach (var field in fields)
            {
                if (field.ValueTypeName == null || !field.IsReadOnly)
                    continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    SyncVarCannotBeReadonly, field.Location, type.Name, field.Name));
                valid = false;
            }
            return valid;
        }

        private static bool IsDerivedFrom(INamedTypeSymbol type, INamedTypeSymbol baseType)
        {
            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, baseType))
                    return true;
            }
            return false;
        }

        private static bool IsPartialWithContainingTypes(INamedTypeSymbol type)
        {
            if (!IsPartial(type))
                return false;
            for (var containing = type.ContainingType; containing != null; containing = containing.ContainingType)
            {
                if (!IsPartial(containing))
                    return false;
            }
            return true;
        }

        private static bool IsPartial(INamedTypeSymbol type)
        {
            foreach (var reference in type.DeclaringSyntaxReferences)
            {
                if (!(reference.GetSyntax() is ClassDeclarationSyntax declaration))
                    return false;
                var isPartial = false;
                foreach (var modifier in declaration.Modifiers)
                {
                    if (modifier.IsKind(SyntaxKind.PartialKeyword))
                    {
                        isPartial = true;
                        break;
                    }
                }
                if (!isPartial)
                    return false;
            }
            return true;
        }

        private static List<FieldInfo> CollectFields(
            INamedTypeSymbol type,
            INamedTypeSymbol syncVarType,
            INamedTypeSymbol syncableFieldType)
        {
            var result = new List<FieldInfo>();
            foreach (var member in type.GetMembers())
            {
                if (!(member is IFieldSymbol field) || field.IsStatic || field.IsConst)
                    continue;
                if (!(field.Type is INamedTypeSymbol fieldType))
                    continue;

                if (SymbolEqualityComparer.Default.Equals(fieldType.OriginalDefinition, syncVarType) &&
                    fieldType.TypeArguments.Length == 1)
                {
                    result.Add(FieldInfo.ForSyncVar(field.Name, fieldType.TypeArguments[0], field.IsReadOnly, field.Locations.Length > 0 ? field.Locations[0] : Location.None));
                    continue;
                }

                if (SymbolEqualityComparer.Default.Equals(fieldType, syncableFieldType) ||
                    IsDerivedFrom(fieldType, syncableFieldType))
                {
                    result.Add(FieldInfo.ForSyncableField(field.Name));
                }
            }
            return result;
        }

        private static string GetHintName(INamedTypeSymbol type)
        {
            var name = type.ToDisplayString(FullyQualified).Replace("global::", string.Empty);
            var sb = new StringBuilder(name.Length + 16);
            foreach (var c in name)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.Append(".SyncVarAccessors.g.cs").ToString();
        }

        private static string Emit(INamedTypeSymbol type, List<FieldInfo> fields, string overrideModifiers)
        {
            var sb = new StringBuilder();
            sb.AppendLine("// <auto-generated/>");
            sb.AppendLine("// Generated by the LiteEntitySystem source generator. Do not modify.");
            sb.AppendLine("#nullable disable");
            sb.AppendLine("#pragma warning disable");

            var containingNamespace = type.ContainingNamespace;
            var hasNamespace = containingNamespace != null && !containingNamespace.IsGlobalNamespace;
            if (hasNamespace)
            {
                sb.Append("namespace ").Append(containingNamespace.ToDisplayString()).AppendLine();
                sb.AppendLine("{");
            }

            var indent = hasNamespace ? "    " : string.Empty;
            var depth = AppendTypeHeaders(sb, type, ref indent);
            AppendAccessorMethod(sb, type, fields, indent, overrideModifiers);
            for (var i = 0; i < depth; i++)
            {
                indent = indent.Substring(0, indent.Length - 4);
                sb.Append(indent).AppendLine("}");
            }

            if (hasNamespace)
                sb.AppendLine("}");
            return sb.ToString();
        }

        /// <summary>
        /// Writes the namespace-less chain of partial type declarations and returns how many braces were opened.
        /// </summary>
        private static int AppendTypeHeaders(StringBuilder sb, INamedTypeSymbol type, ref string indent)
        {
            var chain = new List<INamedTypeSymbol>();
            for (var current = type; current != null; current = current.ContainingType)
                chain.Add(current);
            chain.Reverse();

            foreach (var current in chain)
            {
                sb.Append(indent).Append(GetAccessibility(current));
                if (current.IsStatic)
                    sb.Append(" static");
                sb.Append(" partial class ").Append(current.Name);
                AppendTypeParameters(sb, current);
                sb.AppendLine();
                AppendConstraints(sb, current, indent);
                sb.Append(indent).AppendLine("{");
                indent += "    ";
            }

            return chain.Count;
        }

        private static void AppendAccessorMethod(
            StringBuilder sb,
            INamedTypeSymbol type,
            List<FieldInfo> fields,
            string indent,
            string overrideModifiers)
        {
            var typeName = type.ToDisplayString(FullyQualified);

            sb.Append(indent)
              .Append(overrideModifiers)
              .AppendLine(" override void RegisterSyncVarAccessors(global::LiteEntitySystem.Internal.SyncVarAccessorMap map)");
            sb.Append(indent).AppendLine("{");
            var body = indent + "    ";
            sb.Append(body).AppendLine("base.RegisterSyncVarAccessors(map);");

            //Enum backed fields need their value processor registered, because enum types are not
            //registered by default (unlike primitive and user registered field types).
            foreach (var field in fields)
            {
                if (field.ValueTypeName == null || !field.IsEnumBacked)
                    continue;
                sb.Append(body)
                  .Append("global::LiteEntitySystem.EntityManager.EnsureFieldTypeRegistered<")
                  .Append(field.ValueTypeName)
                  .AppendLine(">();");
            }

            foreach (var field in fields)
            {
                sb.Append(body)
                  .Append("map.Add(typeof(").Append(typeName).Append("), \"")
                  .Append(field.Name).Append("\", new global::LiteEntitySystem.Internal.");

                if (field.ValueTypeName != null)
                {
                    sb.Append("SyncVarRefGetter<").Append(field.ValueTypeName).Append(">(__lesObj => ref ((")
                      .Append(typeName).Append(")__lesObj).").Append(field.Name).Append(")");
                }
                else
                {
                    sb.Append("ObjectFieldGetter<global::LiteEntitySystem.SyncableField>(__lesObj => ")
                      .Append("(global::LiteEntitySystem.SyncableField)((")
                      .Append(typeName).Append(")__lesObj).").Append(field.Name).Append(")");
                }

                sb.AppendLine(");");
            }

            sb.Append(indent).AppendLine("}");
        }

        private static void AppendTypeParameters(StringBuilder sb, INamedTypeSymbol type)
        {
            if (type.TypeParameters.Length == 0)
                return;
            sb.Append('<');
            for (var i = 0; i < type.TypeParameters.Length; i++)
            {
                if (i > 0)
                    sb.Append(", ");
                sb.Append(type.TypeParameters[i].Name);
            }
            sb.Append('>');
        }

        private static void AppendConstraints(StringBuilder sb, INamedTypeSymbol type, string indent)
        {
            foreach (var typeParameter in type.TypeParameters)
            {
                var constraints = new List<string>();
                if (typeParameter.HasUnmanagedTypeConstraint)
                    constraints.Add("unmanaged");
                else if (typeParameter.HasValueTypeConstraint)
                    constraints.Add("struct");
                else if (typeParameter.HasReferenceTypeConstraint)
                    constraints.Add("class");
                else if (typeParameter.HasNotNullConstraint)
                    constraints.Add("notnull");

                foreach (var constraintType in typeParameter.ConstraintTypes)
                    constraints.Add(constraintType.ToDisplayString(FullyQualified));

                if (typeParameter.HasConstructorConstraint &&
                    !typeParameter.HasValueTypeConstraint &&
                    !typeParameter.HasUnmanagedTypeConstraint)
                {
                    constraints.Add("new()");
                }

                if (constraints.Count == 0)
                    continue;
                sb.Append(indent).Append("    where ").Append(typeParameter.Name).Append(" : ")
                  .Append(string.Join(", ", constraints)).AppendLine();
            }
        }

        private static string GetAccessibility(INamedTypeSymbol type)
        {
            switch (type.DeclaredAccessibility)
            {
                case Accessibility.Public:
                    return "public";
                case Accessibility.Internal:
                    return "internal";
                case Accessibility.Private:
                    return "private";
                case Accessibility.Protected:
                    return "protected";
                case Accessibility.ProtectedOrInternal:
                    return "protected internal";
                case Accessibility.ProtectedAndInternal:
                    return "private protected";
                default:
                    return "internal";
            }
        }

        private sealed class ClassReceiver : ISyntaxReceiver
        {
            public readonly List<ClassDeclarationSyntax> Declarations = new List<ClassDeclarationSyntax>();

            public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
            {
                if (syntaxNode is ClassDeclarationSyntax declaration)
                    Declarations.Add(declaration);
            }
        }

        private sealed class FieldInfo
        {
            public string Name;
            public bool IsReadOnly;
            public Location Location;

            /// <summary>Fully qualified SyncVar value type, or null for SyncableField members.</summary>
            public string ValueTypeName;

            /// <summary>True when the value type is an enum and has to be registered at runtime.</summary>
            public bool IsEnumBacked;

            public static FieldInfo ForSyncVar(string name, ITypeSymbol valueType, bool isReadOnly, Location location)
            {
                var isEnum = valueType.TypeKind == TypeKind.Enum || IsEnumConstrainedTypeParameter(valueType);
                return new FieldInfo
                {
                    Name = name,
                    IsReadOnly = isReadOnly,
                    Location = location,
                    ValueTypeName = valueType.ToDisplayString(FullyQualified),
                    IsEnumBacked = isEnum
                };
            }

            public static FieldInfo ForSyncableField(string name) =>
                new FieldInfo { Name = name, IsReadOnly = false, Location = Location.None, ValueTypeName = null, IsEnumBacked = false };

            private static bool IsEnumConstrainedTypeParameter(ITypeSymbol type)
            {
                if (!(type is ITypeParameterSymbol typeParameter))
                    return false;
                foreach (var constraint in typeParameter.ConstraintTypes)
                {
                    if (constraint.SpecialType == SpecialType.System_Enum)
                        return true;
                }
                return false;
            }
        }
    }
}
