#:package Microsoft.CodeAnalysis.CSharp@4.14.0
#:property JsonSerializerIsReflectionEnabledByDefault=true

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

var root = Directory.GetCurrentDirectory();
var outDir = Path.Combine(root, "artifacts", "uml");
var includeVendor = false;
var includeTests = true;
var emitSvg = true;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--root": root = Path.GetFullPath(args[++i]); break;
        case "--out": outDir = Path.GetFullPath(args[++i]); break;
        case "--include-vendor": includeVendor = true; break;
        case "--no-tests": includeTests = false; break;
        case "--no-svg": emitSvg = false; break;
        case "--help":
            Console.WriteLine("usage: dotnet run tools/uml/NoksUml.cs -- [--root <dir>] [--out <dir>] [--include-vendor] [--no-tests] [--no-svg]");
            return 0;
    }
}

var slnx = Path.Combine(root, "Noks.slnx");
var projectPaths = new List<string>();
if (File.Exists(slnx))
{
    foreach (Match m in Regex.Matches(File.ReadAllText(slnx), "Project\\s+Path=\"([^\"]+)\""))
    {
        projectPaths.Add(Path.GetFullPath(Path.Combine(root, m.Groups[1].Value.Replace('\\', '/'))));
    }
}

var extrasDir = Path.Combine(root, "extras");
if (Directory.Exists(extrasDir))
{
    foreach (var extra in Directory.EnumerateFiles(extrasDir, "*.csproj", SearchOption.AllDirectories))
    {
        projectPaths.Add(Path.GetFullPath(extra));
    }
}

projectPaths = projectPaths.Where(File.Exists).Distinct().OrderBy(p => p).ToList();

var projects = new List<ProjectModel>();
foreach (var path in projectPaths)
{
    var name = Path.GetFileNameWithoutExtension(path);
    if (!includeTests && name.EndsWith(".Tests", StringComparison.Ordinal)) continue;
    if (!includeVendor && name.EndsWith(".Vendored", StringComparison.Ordinal)) continue;
    var dir = Path.GetDirectoryName(path)!;
    var text = File.ReadAllText(path);
    var refs = Regex.Matches(text, "ProjectReference\\s+Include=\"([^\"]+)\"")
        .Select(m => Path.GetFileNameWithoutExtension(m.Groups[1].Value.Replace('\\', '/')))
        .Distinct().OrderBy(s => s).ToList();
    var packages = Regex.Matches(text, "PackageReference\\s+Include=\"([^\"]+)\"")
        .Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s).ToList();
    var tfm = Regex.Match(text, "<TargetFrameworks?>([^<]+)<").Groups[1].Value;
    if (string.IsNullOrWhiteSpace(tfm)) tfm = "net10.0";
    var group = Path.GetRelativePath(root, dir).Split(Path.DirectorySeparatorChar)[0];
    var isTest = name.EndsWith(".Tests", StringComparison.Ordinal) || packages.Any(p => p.StartsWith("xunit", StringComparison.Ordinal));
    projects.Add(new ProjectModel(name, Path.GetRelativePath(root, path), dir, group, tfm, refs, packages, isTest));
}

var types = new List<TypeModel>();
var fileCount = 0;
var totalLines = 0;

foreach (var project in projects)
{
    foreach (var file in Directory.EnumerateFiles(project.Dir, "*.cs", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
        if (rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal)) continue;
        if (!includeVendor && (rel.Contains("/vendor/", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".g.cs", StringComparison.Ordinal))) continue;

        var source = File.ReadAllText(file);
        var tree = CSharpSyntaxTree.ParseText(SourceText.From(source), new CSharpParseOptions(LanguageVersion.Preview), rel);
        var declared = tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().ToList();
        var delegates = tree.GetRoot().DescendantNodes().OfType<DelegateDeclarationSyntax>().ToList();
        if (declared.Count == 0 && delegates.Count == 0) continue;

        fileCount++;
        totalLines += source.Count(c => c == '\n') + 1;

        foreach (var decl in declared)
        {
            types.Add(BuildType(decl, tree, project, rel));
        }

        foreach (var del in delegates)
        {
            var span = tree.GetLineSpan(del.Span);
            types.Add(new TypeModel(
                Id: $"{project.Name}:{Qualify(NamespaceOf(del), NameOf(del))}",
                Name: NameOf(del),
                FullName: Qualify(NamespaceOf(del), NameOf(del)),
                Kind: "delegate",
                Accessibility: AccessOf(del.Modifiers, "internal"),
                Modifiers: [],
                TypeParameters: del.TypeParameterList?.Parameters.Select(p => p.Identifier.Text).ToList() ?? [],
                Namespace: NamespaceOf(del),
                Project: project.Name,
                File: rel,
                Line: span.StartLinePosition.Line + 1,
                Loc: span.EndLinePosition.Line - span.StartLinePosition.Line + 1,
                Doc: DocOf(del),
                BaseNames: [],
                Members: [new MemberModel("method", "Invoke", $"Invoke({string.Join(", ", del.ParameterList.Parameters.Select(p => p.Type?.ToString() ?? "?"))}) : {del.ReturnType}", "public", false, false, span.StartLinePosition.Line + 1, "")],
                ReferencedNames: TypeNames(del.ReturnType).Concat(del.ParameterList.Parameters.SelectMany(p => TypeNames(p.Type))).Distinct().ToList()));
        }
    }
}

var byId = types.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
types = byId.Values.OrderBy(t => t.FullName, StringComparer.Ordinal).ToList();

var bySimple = new Dictionary<string, List<TypeModel>>(StringComparer.Ordinal);
foreach (var t in types)
{
    var simple = t.Name;
    if (!bySimple.TryGetValue(simple, out var list)) bySimple[simple] = list = [];
    list.Add(t);
}

string? Resolve(TypeModel from, string simple)
{
    if (!bySimple.TryGetValue(simple, out var candidates)) return null;
    var sameNamespace = candidates.Where(c => c.Namespace == from.Namespace).ToList();
    if (sameNamespace.Count == 1) return sameNamespace[0].Id;
    var sameProject = candidates.Where(c => c.Project == from.Project).ToList();
    if (sameProject.Count == 1) return sameProject[0].Id;
    var visible = candidates.Where(c => c.Project == from.Project || ProjectSees(from.Project, c.Project)).ToList();
    if (visible.Count == 1) return visible[0].Id;
    return candidates.Count == 1 ? candidates[0].Id : null;
}

var projectByName = projects.ToDictionary(p => p.Name, StringComparer.Ordinal);
bool ProjectSees(string from, string to)
{
    if (from == to) return true;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var stack = new Stack<string>();
    stack.Push(from);
    while (stack.Count > 0)
    {
        var current = stack.Pop();
        if (!seen.Add(current) || !projectByName.TryGetValue(current, out var p)) continue;
        foreach (var r in p.ProjectRefs)
        {
            if (r == to) return true;
            stack.Push(r);
        }
    }
    return false;
}

var inherits = new List<Edge>();
var implements = new List<Edge>();
var uses = new Dictionary<(string, string), int>();

foreach (var t in types)
{
    foreach (var baseName in t.BaseNames)
    {
        var id = Resolve(t, baseName);
        if (id is null || id == t.Id) continue;
        var target = byId.TryGetValue(id, out var tt) ? tt : null;
        if (target is null) continue;
        if (target.Kind == "interface") implements.Add(new Edge(t.Id, id));
        else inherits.Add(new Edge(t.Id, id));
    }

    foreach (var name in t.ReferencedNames)
    {
        var id = Resolve(t, name);
        if (id is null || id == t.Id) continue;
        uses[(t.Id, id)] = uses.TryGetValue((t.Id, id), out var c) ? c + 1 : 1;
    }
}

var structural = new HashSet<(string, string)>(inherits.Concat(implements).Select(e => (e.From, e.To)));
var useEdges = uses.Where(kv => !structural.Contains(kv.Key))
    .Select(kv => new UseEdge(kv.Key.Item1, kv.Key.Item2, kv.Value))
    .OrderByDescending(e => e.Weight).ToList();

var namespaces = types.GroupBy(t => (t.Namespace, t.Project))
    .Select(g => new NamespaceModel(
        Id: $"{g.Key.Project}:{g.Key.Namespace}",
        Name: g.Key.Namespace,
        Project: g.Key.Project,
        TypeCount: g.Count(),
        Loc: g.Sum(t => t.Loc)))
    .OrderBy(n => n.Name, StringComparer.Ordinal).ToList();

var nsEdges = new Dictionary<(string, string), int>();
foreach (var e in inherits.Concat(implements).Select(e => (e.From, e.To, 1)).Concat(useEdges.Select(e => (e.From, e.To, e.Weight))))
{
    var a = byId[e.Item1];
    var b = byId[e.Item2];
    var key = ($"{a.Project}:{a.Namespace}", $"{b.Project}:{b.Namespace}");
    if (key.Item1 == key.Item2) continue;
    nsEdges[key] = nsEdges.TryGetValue(key, out var c) ? c + e.Item3 : e.Item3;
}

var model = new
{
    generatedAtUtc = DateTime.UtcNow.ToString("O"),
    root,
    stats = new
    {
        projects = projects.Count,
        files = fileCount,
        lines = totalLines,
        types = types.Count,
        members = types.Sum(t => t.Members.Count),
    },
    projects = projects.Select(p => new
    {
        id = p.Name,
        name = p.Name,
        path = p.Path,
        group = p.Group,
        targetFrameworks = p.TargetFrameworks,
        isTest = p.IsTest,
        projectRefs = p.ProjectRefs,
        packageRefs = p.PackageRefs,
        typeCount = types.Count(t => t.Project == p.Name),
        loc = types.Where(t => t.Project == p.Name).Sum(t => t.Loc),
    }),
    namespaces,
    types = types.Select(t => new
    {
        t.Id, t.Name, t.FullName, t.Kind, t.Accessibility, t.Modifiers, t.TypeParameters,
        t.Namespace, t.Project, t.File, t.Line, t.Loc, t.Doc,
        baseTypes = t.BaseNames,
        members = t.Members,
        memberCount = t.Members.Count,
    }),
    edges = new
    {
        inherits = inherits.Distinct().ToList(),
        implements = implements.Distinct().ToList(),
        uses = useEdges,
        namespaces = nsEdges.Select(kv => new UseEdge(kv.Key.Item1, kv.Key.Item2, kv.Value)).OrderByDescending(e => e.Weight).ToList(),
    },
};

Directory.CreateDirectory(outDir);
var json = JsonSerializer.Serialize(model, new JsonSerializerOptions { WriteIndented = false, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
File.WriteAllText(Path.Combine(outDir, "model.json"), json);
Console.WriteLine($"model.json: {projects.Count} projects, {types.Count} types, {fileCount} files");

var dotDir = Path.Combine(outDir, "dot");
var svgDir = Path.Combine(outDir, "svg");
Directory.CreateDirectory(dotDir);
Directory.CreateDirectory(svgDir);

Write("solution", SolutionDot(projects, types));
foreach (var ns in namespaces)
{
    var slug = Slug(ns.Id);
    Write($"ns-{slug}", NamespaceDot(ns, types, inherits, implements, useEdges, byId));
}

Console.WriteLine($"wrote {Directory.GetFiles(dotDir).Length} dot files to {Path.GetRelativePath(root, dotDir)}");
if (emitSvg)
{
    var rendered = 0;
    foreach (var dot in Directory.GetFiles(dotDir, "*.dot"))
    {
        if (RenderSvg(dot, Path.Combine(svgDir, Path.GetFileNameWithoutExtension(dot) + ".svg"))) rendered++;
    }
    Console.WriteLine(rendered > 0
        ? $"rendered {rendered} svg files to {Path.GetRelativePath(root, svgDir)}"
        : "graphviz 'dot' not found: skipped svg rendering");
}

return 0;

void Write(string name, string content) => File.WriteAllText(Path.Combine(dotDir, name + ".dot"), content);

static bool RenderSvg(string dotFile, string svgFile)
{
    try
    {
        var psi = new ProcessStartInfo("dot", $"-Tsvg -o \"{svgFile}\" \"{dotFile}\"") { RedirectStandardError = true };
        using var proc = Process.Start(psi);
        if (proc is null) return false;
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }
    catch (Exception)
    {
        return false;
    }
}

static string Slug(string value) => Regex.Replace(value, "[^A-Za-z0-9]+", "-").Trim('-').ToLowerInvariant();

static string Esc(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

static string SolutionDot(List<ProjectModel> projects, List<TypeModel> types)
{
    var sb = new StringBuilder();
    sb.AppendLine("digraph solution {");
    sb.AppendLine("  rankdir=BT; bgcolor=\"transparent\"; splines=spline; nodesep=0.35; ranksep=0.6;");
    sb.AppendLine("  node [shape=box style=\"filled,rounded\" fontname=\"Helvetica\" fontsize=11 penwidth=1.2];");
    sb.AppendLine("  edge [fontname=\"Helvetica\" fontsize=9 color=\"#7a8699\"];");
    foreach (var group in projects.GroupBy(p => p.Group))
    {
        sb.AppendLine($"  subgraph \"cluster_{Slug(group.Key)}\" {{");
        sb.AppendLine($"    label=\"{Esc(group.Key)}\"; fontname=\"Helvetica\"; fontsize=10; color=\"#c3ccd8\"; style=rounded;");
        foreach (var p in group)
        {
            var loc = types.Where(t => t.Project == p.Name).Sum(t => t.Loc);
            var fill = p.IsTest ? "#eef6ec" : "#eef2fb";
            var line = p.IsTest ? "#7aa06f" : "#5b7fc7";
            sb.AppendLine($"    \"{Esc(p.Name)}\" [label=\"{Esc(p.Name)}\\n{p.TargetFrameworks} | {loc} loc\" fillcolor=\"{fill}\" color=\"{line}\"];");
        }
        sb.AppendLine("  }");
    }
    foreach (var p in projects)
    {
        foreach (var r in p.ProjectRefs.Where(r => projects.Any(x => x.Name == r)))
        {
            sb.AppendLine($"  \"{Esc(p.Name)}\" -> \"{Esc(r)}\";");
        }
    }
    sb.AppendLine("}");
    return sb.ToString();
}

static string NamespaceDot(NamespaceModel ns, List<TypeModel> types, List<Edge> inherits, List<Edge> implements, List<UseEdge> uses, Dictionary<string, TypeModel> byId)
{
    var members = types.Where(t => t.Namespace == ns.Name && t.Project == ns.Project).ToList();
    var ids = members.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
    var sb = new StringBuilder();
    sb.AppendLine($"digraph \"{Esc(ns.Id)}\" {{");
    sb.AppendLine("  rankdir=BT; bgcolor=\"transparent\"; splines=spline; nodesep=0.3; ranksep=0.7; concentrate=true;");
    sb.AppendLine("  node [shape=plaintext fontname=\"Helvetica\" fontsize=12];");
    sb.AppendLine("  edge [fontname=\"Helvetica\" fontsize=8 color=\"#8a94a6\"];");
    foreach (var t in members) sb.AppendLine($"  \"{Esc(t.Id)}\" [label=<{TypeLabel(t)}>];");
    foreach (var e in inherits.Where(e => ids.Contains(e.From) && ids.Contains(e.To)))
        sb.AppendLine($"  \"{Esc(e.From)}\" -> \"{Esc(e.To)}\" [arrowhead=onormal color=\"#3f6fd1\"];");
    foreach (var e in implements.Where(e => ids.Contains(e.From) && ids.Contains(e.To)))
        sb.AppendLine($"  \"{Esc(e.From)}\" -> \"{Esc(e.To)}\" [arrowhead=onormal style=dashed color=\"#3f6fd1\"];");
    foreach (var e in uses.Where(e => ids.Contains(e.From) && ids.Contains(e.To)))
        sb.AppendLine($"  \"{Esc(e.From)}\" -> \"{Esc(e.To)}\" [arrowhead=vee style=dotted color=\"#a7b0be\"];");
    sb.AppendLine("}");
    return sb.ToString();
}

static string TypeLabel(TypeModel t)
{
    var header = t.Kind switch
    {
        "interface" => "#f3ecfb",
        "enum" => "#fdf3e6",
        "record" => "#e9f6f2",
        "struct" => "#eef6ec",
        "delegate" => "#fbeef1",
        _ => "#eef2fb",
    };
    var sb = new StringBuilder();
    sb.Append($"<TABLE BORDER=\"0\" CELLBORDER=\"1\" CELLSPACING=\"0\" CELLPADDING=\"4\" BGCOLOR=\"#ffffff\" COLOR=\"#9aa5b5\">");
    sb.Append($"<TR><TD BGCOLOR=\"{header}\"><B>{Esc(t.Name)}</B><BR/><FONT POINT-SIZE=\"9\">{Esc(t.Kind)} | {t.Loc} loc</FONT></TD></TR>");
    var shown = t.Members.Where(m => m.Accessibility is "public" or "protected").Take(10).ToList();
    foreach (var m in shown)
    {
        var glyph = m.Accessibility == "public" ? "+" : "#";
        sb.Append($"<TR><TD ALIGN=\"LEFT\"><FONT POINT-SIZE=\"9\">{Esc(glyph + m.Signature)}</FONT></TD></TR>");
    }
    var hidden = t.Members.Count - shown.Count;
    if (hidden > 0) sb.Append($"<TR><TD ALIGN=\"LEFT\"><FONT POINT-SIZE=\"9\" COLOR=\"#7a8699\">+{hidden} more</FONT></TD></TR>");
    sb.Append("</TABLE>");
    return sb.ToString();
}

static string NamespaceOf(SyntaxNode node)
{
    var parts = new List<string>();
    foreach (var ancestor in node.Ancestors())
    {
        if (ancestor is BaseNamespaceDeclarationSyntax ns) parts.Insert(0, ns.Name.ToString());
    }
    return parts.Count == 0 ? "<global>" : string.Join('.', parts);
}

static string NameOf(SyntaxNode node) => node switch
{
    BaseTypeDeclarationSyntax b => Nest(b, b.Identifier.Text),
    DelegateDeclarationSyntax d => Nest(d, d.Identifier.Text),
    _ => "?",
};

static string Nest(SyntaxNode node, string name)
{
    foreach (var ancestor in node.Ancestors().OfType<TypeDeclarationSyntax>())
    {
        name = ancestor.Identifier.Text + "." + name;
    }
    return name;
}

static string Qualify(string ns, string name) => ns == "<global>" ? name : ns + "." + name;

static string AccessOf(SyntaxTokenList modifiers, string fallback)
{
    var text = modifiers.Select(m => m.Text).ToList();
    if (text.Contains("public")) return "public";
    if (text.Contains("protected") && text.Contains("internal")) return "protected internal";
    if (text.Contains("private") && text.Contains("protected")) return "private protected";
    if (text.Contains("protected")) return "protected";
    if (text.Contains("internal")) return "internal";
    if (text.Contains("private")) return "private";
    return fallback;
}

static string DocOf(SyntaxNode node)
{
    var trivia = node.GetLeadingTrivia().ToFullString();
    var match = Regex.Match(trivia, "<summary>(.*?)</summary>", RegexOptions.Singleline);
    if (!match.Success) return "";
    var body = Regex.Replace(match.Groups[1].Value, "^\\s*///?", "", RegexOptions.Multiline);
    return Regex.Replace(body, "\\s+", " ").Trim();
}

static List<string> TypeNames(TypeSyntax? type)
{
    if (type is null) return [];
    var names = type.DescendantNodesAndSelf()
        .Select(n => n switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            GenericNameSyntax g => g.Identifier.Text,
            _ => null,
        })
        .Where(n => n is not null)
        .Select(n => n!)
        .ToList();
    return names;
}

static TypeModel BuildType(BaseTypeDeclarationSyntax decl, SyntaxTree tree, ProjectModel project, string rel)
{
    var span = tree.GetLineSpan(decl.Span);
    var ns = NamespaceOf(decl);
    var name = NameOf(decl);
    var kind = decl switch
    {
        RecordDeclarationSyntax => "record",
        InterfaceDeclarationSyntax => "interface",
        StructDeclarationSyntax => "struct",
        EnumDeclarationSyntax => "enum",
        _ => "class",
    };
    var modifiers = decl.Modifiers.Select(m => m.Text).Where(m => m is "sealed" or "abstract" or "static" or "partial" or "readonly" or "ref").ToList();
    var baseNames = decl.BaseList?.Types.SelectMany(b => TypeNames(b.Type)).Distinct().ToList() ?? [];
    var referenced = new List<string>();
    var members = new List<MemberModel>();
    var defaultAccess = kind == "interface" ? "public" : "private";

    if (decl is TypeDeclarationSyntax typeDecl)
    {
        if (typeDecl is RecordDeclarationSyntax record && record.ParameterList is not null)
        {
            foreach (var p in record.ParameterList.Parameters)
            {
                members.Add(new MemberModel("property", p.Identifier.Text, $"{p.Identifier.Text} : {p.Type}", "public", false, false, tree.GetLineSpan(p.Span).StartLinePosition.Line + 1, ""));
                referenced.AddRange(TypeNames(p.Type));
            }
        }

        foreach (var member in typeDecl.Members)
        {
            var line = tree.GetLineSpan(member.Span).StartLinePosition.Line + 1;
            switch (member)
            {
                case FieldDeclarationSyntax f:
                    referenced.AddRange(TypeNames(f.Declaration.Type));
                    foreach (var v in f.Declaration.Variables)
                        members.Add(new MemberModel("field", v.Identifier.Text, $"{v.Identifier.Text} : {f.Declaration.Type}", AccessOf(f.Modifiers, defaultAccess), f.Modifiers.Any(m => m.Text == "static"), false, line, DocOf(f)));
                    break;
                case PropertyDeclarationSyntax p:
                    referenced.AddRange(TypeNames(p.Type));
                    members.Add(new MemberModel("property", p.Identifier.Text, $"{p.Identifier.Text} : {p.Type}", AccessOf(p.Modifiers, defaultAccess), p.Modifiers.Any(m => m.Text == "static"), false, line, DocOf(p)));
                    break;
                case IndexerDeclarationSyntax ix:
                    referenced.AddRange(TypeNames(ix.Type));
                    members.Add(new MemberModel("property", "this[]", $"this[{string.Join(", ", ix.ParameterList.Parameters.Select(x => x.Type?.ToString() ?? "?"))}] : {ix.Type}", AccessOf(ix.Modifiers, defaultAccess), false, false, line, DocOf(ix)));
                    break;
                case MethodDeclarationSyntax m:
                    referenced.AddRange(TypeNames(m.ReturnType));
                    foreach (var p in m.ParameterList.Parameters) referenced.AddRange(TypeNames(p.Type));
                    members.Add(new MemberModel("method", m.Identifier.Text, $"{m.Identifier.Text}({string.Join(", ", m.ParameterList.Parameters.Select(x => x.Type?.ToString() ?? "?"))}) : {m.ReturnType}", AccessOf(m.Modifiers, defaultAccess), m.Modifiers.Any(x => x.Text == "static"), m.Modifiers.Any(x => x.Text == "async"), line, DocOf(m)));
                    break;
                case ConstructorDeclarationSyntax c:
                    foreach (var p in c.ParameterList.Parameters) referenced.AddRange(TypeNames(p.Type));
                    members.Add(new MemberModel("constructor", c.Identifier.Text, $"{c.Identifier.Text}({string.Join(", ", c.ParameterList.Parameters.Select(x => x.Type?.ToString() ?? "?"))})", AccessOf(c.Modifiers, defaultAccess), false, false, line, DocOf(c)));
                    break;
                case EventFieldDeclarationSyntax ev:
                    referenced.AddRange(TypeNames(ev.Declaration.Type));
                    foreach (var v in ev.Declaration.Variables)
                        members.Add(new MemberModel("event", v.Identifier.Text, $"{v.Identifier.Text} : {ev.Declaration.Type}", AccessOf(ev.Modifiers, defaultAccess), false, false, line, DocOf(ev)));
                    break;
                case OperatorDeclarationSyntax op:
                    members.Add(new MemberModel("method", "operator " + op.OperatorToken.Text, $"operator {op.OperatorToken.Text}(...) : {op.ReturnType}", AccessOf(op.Modifiers, defaultAccess), true, false, line, ""));
                    break;
            }
        }

        foreach (var creation in typeDecl.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
        {
            referenced.AddRange(TypeNames(creation.Type));
        }
    }
    else if (decl is EnumDeclarationSyntax e)
    {
        foreach (var v in e.Members)
        {
            members.Add(new MemberModel("enumValue", v.Identifier.Text, v.Identifier.Text, "public", true, false, tree.GetLineSpan(v.Span).StartLinePosition.Line + 1, DocOf(v)));
        }
    }

    return new TypeModel(
        Id: $"{project.Name}:{Qualify(ns, name)}",
        Name: name,
        FullName: Qualify(ns, name),
        Kind: kind,
        Accessibility: AccessOf(decl.Modifiers, "internal"),
        Modifiers: modifiers,
        TypeParameters: (decl as TypeDeclarationSyntax)?.TypeParameterList?.Parameters.Select(p => p.Identifier.Text).ToList() ?? [],
        Namespace: ns,
        Project: project.Name,
        File: rel,
        Line: span.StartLinePosition.Line + 1,
        Loc: span.EndLinePosition.Line - span.StartLinePosition.Line + 1,
        Doc: DocOf(decl),
        BaseNames: baseNames,
        Members: members,
        ReferencedNames: referenced);
}

record ProjectModel(string Name, string Path, string Dir, string Group, string TargetFrameworks, List<string> ProjectRefs, List<string> PackageRefs, bool IsTest);
record NamespaceModel(string Id, string Name, string Project, int TypeCount, int Loc);
record MemberModel(string Kind, string Name, string Signature, string Accessibility, bool IsStatic, bool IsAsync, int Line, string Doc);
record TypeModel(string Id, string Name, string FullName, string Kind, string Accessibility, List<string> Modifiers, List<string> TypeParameters, string Namespace, string Project, string File, int Line, int Loc, string Doc, List<string> BaseNames, List<MemberModel> Members, List<string> ReferencedNames);
record Edge(string From, string To);
record UseEdge(string From, string To, int Weight);
