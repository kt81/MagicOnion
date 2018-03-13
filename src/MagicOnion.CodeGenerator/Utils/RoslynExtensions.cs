using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using MagicOnion.Utils;

namespace MagicOnion
{
    // Utility and Extension methods for Roslyn
    internal static class RoslynExtensions
    {
        public static async Task<Compilation> GetCompilationFromProject(string csprojPath, params string[] preprocessorSymbols)
        {
            EnvironmentHelper.Setup();

            var workspace = MSBuildWorkspace.Create();
            workspace.WorkspaceFailed += Workspace_WorkspaceFailed;
            var requiredExternalReferences = DetermineExternalReferences(csprojPath);

            var project = await workspace.OpenProjectAsync(csprojPath).ConfigureAwait(false);
            if (requiredExternalReferences != null)
            {
                project = project.AddMetadataReferences(requiredExternalReferences); // workaround:)
            }

            project = project.WithParseOptions((project.ParseOptions as CSharpParseOptions).WithPreprocessorSymbols(preprocessorSymbols));

            var compilation = await project.GetCompilationAsync().ConfigureAwait(false);
            return compilation;
        }

        private static void Workspace_WorkspaceFailed(object sender, WorkspaceDiagnosticEventArgs e)
        {
            Console.WriteLine(e.Diagnostic.ToString());
            // throw new Exception(e.Diagnostic.ToString());
        }

        private static IEnumerable<PortableExecutableReference> DetermineExternalReferences(string csprojPath)
        {
            // fucking workaround of resolve reference... (for netfx)

            var xElem = XElement.Load(csprojPath);
            var ns = xElem.Name.Namespace;

            // Skip the .NET Standard and Core projects to prevent the issue: https://github.com/neuecc/MessagePack-CSharp/issues/188
            var targetFrameworks = PickProjectsTargetFramework(xElem, ns).ToArray();
            if (1 < targetFrameworks.Length)
            {
                throw new NotImplementedException("Portable project has not been supported yet.");
            }
            if (targetFrameworks.FirstOrDefault(s => s.StartsWith("netstandard") || s.StartsWith("netcoreapp")) != null)
            {
                return null;
            }

            var externalReferences = new List<PortableExecutableReference>();

            var locations = new List<string>();
            locations.Add(typeof(object).Assembly.Location); // mscorlib
            locations.Add(typeof(System.Linq.Enumerable).Assembly.Location); // core

            var csProjRoot = Path.GetDirectoryName(csprojPath);
            var framworkRoot = Path.GetDirectoryName(typeof(object).Assembly.Location);

            foreach (var item in xElem.Descendants(ns + "Reference"))
            {
                var hintPath = item.Element(ns + "HintPath")?.Value;
                if (hintPath == null)
                {
                    var path = Path.Combine(framworkRoot, item.Attribute("Include").Value + ".dll");
                    locations.Add(path);
                }
                else
                {
                    locations.Add(Path.Combine(csProjRoot, hintPath));
                }
            }

            foreach (var item in locations.Distinct())
            {
                if (File.Exists(item))
                {
                    externalReferences.Add(MetadataReference.CreateFromFile(item));
                }
            }

            return externalReferences;
        }

        private static IEnumerable<string> PickProjectsTargetFramework(XContainer csProjFile, XNamespace ns)
        {
            string[] targets;

            var multipleSpec = csProjFile.Descendants(ns + "TargetFrameworks").FirstOrDefault();
            if (multipleSpec != null)
            {
                targets = multipleSpec.Value.Split(';');
            }
            else
            {
                var s = csProjFile.Descendants(ns + "TargetFramework").FirstOrDefault()?.Value;
                if (s == null)
                {
                    throw new ArgumentException("The csproj file is broken. It has no valid TargetFramework element.", nameof(csProjFile));
                }
                targets = new[] { s };
            }

            return targets.Select(s => s.Trim());
        }


        public static IEnumerable<INamedTypeSymbol> GetNamedTypeSymbols(this Compilation compilation)
        {
            foreach (var syntaxTree in compilation.SyntaxTrees)
            {
                var semModel = compilation.GetSemanticModel(syntaxTree);

                foreach (var item in syntaxTree.GetRoot()
                    .DescendantNodes()
                    .Select(x => semModel.GetDeclaredSymbol(x))
                    .Where(x => x != null))
                {
                    var namedType = item as INamedTypeSymbol;
                    if (namedType != null)
                    {
                        yield return namedType;
                    }
                }
            }
        }

        public static IEnumerable<INamedTypeSymbol> EnumerateBaseType(this ITypeSymbol symbol)
        {
            var t = symbol.BaseType;
            while (t != null)
            {
                yield return t;
                t = t.BaseType;
            }
        }

        public static AttributeData FindAttribute(this IEnumerable<AttributeData> attributeDataList, string typeName)
        {
            return attributeDataList
                .Where(x => x.AttributeClass.ToDisplayString() == typeName)
                .FirstOrDefault();
        }

        public static AttributeData FindAttributeShortName(this IEnumerable<AttributeData> attributeDataList, string typeName)
        {
            return attributeDataList
                .Where(x => x.AttributeClass.Name == typeName)
                .FirstOrDefault();
        }

        public static AttributeData FindAttributeIncludeBasePropertyShortName(this IPropertySymbol property, string typeName)
        {
            do
            {
                var data = FindAttributeShortName(property.GetAttributes(), typeName);
                if (data != null) return data;
                property = property.OverriddenProperty;
            } while (property != null);

            return null;
        }

        public static AttributeSyntax FindAttribute(this BaseTypeDeclarationSyntax typeDeclaration, SemanticModel model, string typeName)
        {
            return typeDeclaration.AttributeLists
                .SelectMany(x => x.Attributes)
                .Where(x => model.GetTypeInfo(x).Type?.ToDisplayString() == typeName)
                .FirstOrDefault();
        }

        public static INamedTypeSymbol FindBaseTargetType(this ITypeSymbol symbol, string typeName)
        {
            return symbol.EnumerateBaseType()
                .Where(x => x.OriginalDefinition?.ToDisplayString() == typeName)
                .FirstOrDefault();
        }

        public static object GetSingleNamedArgumentValue(this AttributeData attribute, string key)
        {
            foreach (var item in attribute.NamedArguments)
            {
                if (item.Key == key)
                {
                    return item.Value.Value;
                }
            }

            return null;
        }

        public static bool IsNullable(this INamedTypeSymbol symbol)
        {
            if (symbol.IsGenericType)
            {
                if (symbol.ConstructUnboundGenericType().ToDisplayString() == "T?")
                {
                    return true;
                }
            }
            return false;
        }

        public static IEnumerable<ISymbol> GetAllMembers(this ITypeSymbol symbol)
        {
            var t = symbol;
            while (t != null)
            {
                foreach (var item in t.GetMembers())
                {
                    yield return item;
                }
                t = t.BaseType;
            }
        }

        public static IEnumerable<ISymbol> GetAllInterfaceMembers(this ITypeSymbol symbol)
        {
            return symbol.GetMembers()
                .Concat(symbol.AllInterfaces.SelectMany(x => x.GetMembers()));
        }
    }
}
