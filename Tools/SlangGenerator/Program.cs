using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: SlangGenerator <input.cs> <output.slang>");
    return 1;
}

var inputPath = Path.GetFullPath(args[0]);
var outputPath = Path.GetFullPath(args[1]);

if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Input file not found: {inputPath}");
    return 1;
}

Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

var source = File.ReadAllText(inputPath);
var slang = SlangGenerator.Generate(source, Path.GetFileName(inputPath));

File.WriteAllText(outputPath, slang);
Console.WriteLine($"Generated {Path.GetFileName(outputPath)} from {Path.GetFileName(inputPath)}.");
return 0;

internal static class SlangGenerator
{
    public static string Generate(string csharpSource, string sourceFileName)
    {
        var tree = CSharpSyntaxTree.ParseText(csharpSource);
        var root = tree.GetCompilationUnitRoot();

        var collector = new DeclarationCollector();
        collector.Visit(root);

        var guardName = Path.GetFileNameWithoutExtension(sourceFileName).ToUpperInvariant() + "_SLANG";

        var sb = new StringBuilder();
        sb.AppendLine($"// Auto-generated from {sourceFileName} - do not edit manually");
        sb.AppendLine();
        sb.AppendLine($"#ifndef {guardName}");
        sb.AppendLine($"#define {guardName}");
        sb.AppendLine();

        if (collector.Constants.Count > 0)
        {
            sb.AppendLine("// Constants");
            foreach (var constant in collector.Constants)
            {
                sb.AppendLine(constant);
            }
            sb.AppendLine();
        }

        foreach (var generatedStruct in collector.Structs)
        {
            sb.AppendLine($"struct {generatedStruct.Name}");
            sb.AppendLine("{");

            foreach (var field in generatedStruct.Fields)
            {
                if (field.IsUnsupported)
                {
                    sb.AppendLine($"    // Unsupported field skipped: {field.OriginalType} {field.Name};");
                    continue;
                }

                sb.AppendLine($"    {field.SlangType} {field.Name};");
            }

            sb.AppendLine("};");
            sb.AppendLine();
        }

        if (collector.Structs.Count == 0)
        {
            sb.AppendLine("// No structs found.");
        }

        sb.AppendLine();
        sb.AppendLine($"#endif // {guardName}");

        return sb.ToString();
    }

    private sealed class DeclarationCollector : CSharpSyntaxWalker
    {
        public List<string> Constants { get; } = new();
        public List<GeneratedStruct> Structs { get; } = new();

        public override void VisitFieldDeclaration(FieldDeclarationSyntax node)
        {
            if (node.Modifiers.Any(SyntaxKind.ConstKeyword))
            {
                var mappedType = TypeMapper.Map(node.Declaration.Type);

                foreach (var variable in node.Declaration.Variables)
                {
                    if (variable.Initializer is null)
                        continue;

                    if (mappedType is null)
                    {
                        Constants.Add($"// Unsupported const skipped: {node.Declaration.Type} {variable.Identifier.Text} = {variable.Initializer.Value};");
                        continue;
                    }

                    var valueText = LiteralMapper.MapConstantValue(variable.Initializer.Value, mappedType);
                    Constants.Add($"static const {mappedType} {variable.Identifier.Text} = {valueText};");
                }
            }

            base.VisitFieldDeclaration(node);
        }

        public override void VisitStructDeclaration(StructDeclarationSyntax node)
        {
            var fields = new List<GeneratedField>();

            foreach (var member in node.Members.OfType<FieldDeclarationSyntax>())
            {
                if (member.Modifiers.Any(SyntaxKind.ConstKeyword))
                    continue;

                var mappedType = TypeMapper.Map(member.Declaration.Type);

                foreach (var variable in member.Declaration.Variables)
                {
                    fields.Add(new GeneratedField(
                        Name: variable.Identifier.Text,
                        SlangType: mappedType,
                        OriginalType: member.Declaration.Type.ToString(),
                        IsUnsupported: mappedType is null));
                }
            }

            Structs.Add(new GeneratedStruct(node.Identifier.Text, fields));

            base.VisitStructDeclaration(node);
        }
    }

    private static class TypeMapper
    {
        private static readonly Dictionary<string, string> TypeMap = new(StringComparer.Ordinal)
        {
            ["byte"] = "uint8_t",
            ["sbyte"] = "int8_t",
            ["short"] = "int16_t",
            ["ushort"] = "uint16_t",
            ["int"] = "int",
            ["uint"] = "uint",
            ["long"] = "int64_t",
            ["ulong"] = "uint64_t",
            ["float"] = "float",
            ["double"] = "double",
            ["bool"] = "bool",

            ["Byte"] = "uint8_t",
            ["SByte"] = "int8_t",
            ["Int16"] = "int16_t",
            ["UInt16"] = "uint16_t",
            ["Int32"] = "int",
            ["UInt32"] = "uint",
            ["Int64"] = "int64_t",
            ["UInt64"] = "uint64_t",
            ["Single"] = "float",
            ["Double"] = "double",
            ["Boolean"] = "bool",

            ["Vector2"] = "float2",
            ["Vector3"] = "float3",
            ["Vector4"] = "float4",
            ["Matrix4x4"] = "float4x4",
            ["Quaternion"] = "float4",

            ["System.Byte"] = "uint8_t",
            ["System.SByte"] = "int8_t",
            ["System.Int16"] = "int16_t",
            ["System.UInt16"] = "uint16_t",
            ["System.Int32"] = "int",
            ["System.UInt32"] = "uint",
            ["System.Int64"] = "int64_t",
            ["System.UInt64"] = "uint64_t",
            ["System.Single"] = "float",
            ["System.Double"] = "double",
            ["System.Boolean"] = "bool",

            ["System.Numerics.Vector2"] = "float2",
            ["System.Numerics.Vector3"] = "float3",
            ["System.Numerics.Vector4"] = "float4",
            ["System.Numerics.Matrix4x4"] = "float4x4",
            ["System.Numerics.Quaternion"] = "float4",
        };

        public static string? Map(TypeSyntax typeSyntax)
        {
            switch (typeSyntax)
            {
                case IdentifierNameSyntax id:
                    return MapName(id.Identifier.Text);

                case PredefinedTypeSyntax predefined:
                    return MapName(predefined.Keyword.Text);

                case QualifiedNameSyntax qualified:
                    return MapName(qualified.ToString());

                case AliasQualifiedNameSyntax aliasQualified:
                    return MapName(aliasQualified.ToString());

                case GenericNameSyntax generic:
                    return MapGeneric(generic);

                case NullableTypeSyntax nullable:
                    return Map(nullable.ElementType);

                case ArrayTypeSyntax:
                    return null;

                case PointerTypeSyntax:
                    return null;

                default:
                    return MapName(typeSyntax.ToString());
            }
        }

        private static string? MapName(string name)
        {
            if (TypeMap.TryGetValue(name, out var mapped))
                return mapped;

            if (LooksLikeUserDefinedType(name))
                return ExtractSimpleName(name);

            return null;
        }

        private static string? MapGeneric(GenericNameSyntax generic)
        {
            return null;
        }

        private static bool LooksLikeUserDefinedType(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return false;

            if (name.Contains('<') || name.Contains('[') || name.Contains('*') || name.Contains('?'))
                return false;

            return char.IsLetter(name[0]) || name[0] == '_';
        }

        private static string ExtractSimpleName(string name)
        {
            var lastDot = name.LastIndexOf('.');
            return lastDot >= 0 ? name[(lastDot + 1)..] : name;
        }
    }

    private static class LiteralMapper
    {
        public static string MapConstantValue(ExpressionSyntax expression, string mappedType)
        {
            var raw = expression.ToString().Trim();

            if (!LooksNumeric(raw))
                return raw;

            return mappedType switch
            {
                "float" => NormalizeFloatLiteral(raw),
                "double" => NormalizeDoubleLiteral(raw),
                "uint" => NormalizeUnsignedLiteral(raw, "u"),
                "uint64_t" => NormalizeUnsignedLiteral(raw, "ul"),
                "int64_t" => NormalizeSignedLongLiteral(raw),
                "bool" => NormalizeBoolLiteral(raw),
                _ => raw
            };
        }

        private static bool LooksNumeric(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return false;

            var first = raw[0];
            return char.IsDigit(first) || first == '-' || first == '+' || first == '.';
        }

        private static string NormalizeFloatLiteral(string raw)
        {
            if (raw.EndsWith("f", StringComparison.OrdinalIgnoreCase))
                return raw;

            if (raw.Contains('.') || raw.Contains('e') || raw.Contains('E'))
                return raw + "f";

            return raw + ".0f";
        }

        private static string NormalizeDoubleLiteral(string raw)
        {
            if (raw.EndsWith("d", StringComparison.OrdinalIgnoreCase))
                return raw[..^1];

            return raw;
        }

        private static string NormalizeUnsignedLiteral(string raw, string suffix)
        {
            var cleaned = raw.TrimEnd('u', 'U', 'l', 'L');
            return cleaned + suffix;
        }

        private static string NormalizeSignedLongLiteral(string raw)
        {
            return raw.TrimEnd('l', 'L') + "l";
        }

        private static string NormalizeBoolLiteral(string raw)
        {
            return raw.Equals("true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
        }
    }

    private sealed record GeneratedStruct(string Name, List<GeneratedField> Fields);

    private sealed record GeneratedField(
        string Name,
        string? SlangType,
        string OriginalType,
        bool IsUnsupported);
}
