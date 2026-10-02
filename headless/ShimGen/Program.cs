// ShimGen: генератор «фальшивого интерфейса» для серверной (headless) сборки OsEngine.
//
// 1. Строит полную Roslyn-модель OsEngine с настоящими ссылками Windows (refs.json из
//    `dotnet msbuild -t:ResolveAssemblyReferences -getItem:ReferencePath`) и кодом окон из obj\Debug\**\*.g.cs.
// 2. Обходит только файлы серверного ядра (files.txt) и роботов, собирает все обращения к типам,
//    которых на сервере нет: WPF/WinForms/Chart/System.Drawing.Common и классы OsEngine вне серверного набора.
// 3. Пишет Shim.Generated.cs: те же имена и сигнатуры, пустые безопасные реализации.
//
// Запуск: dotnet run -- <OsEngineSrc> <files.txt> <refs.json> <out.cs> [robot.cs ...]

using System.Text;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private static readonly string[] UiAssemblyPrefixes =
    {
        "PresentationCore", "PresentationFramework", "PresentationUI", "WindowsBase", "System.Xaml",
        "System.Windows.Forms", "WindowsFormsIntegration", "WinForms.DataVisualization", "System.Drawing.Common",
        "UIAutomation", "ReachFramework", "System.Printing", "DirectWriteForwarder", "Accessibility",
        "Microsoft.Web.WebView2", "System.Windows.Controls.Ribbon", "System.Windows.Input.Manipulations",
        "System.Windows.Presentation", "System.Design", "System.Drawing.Design", "System.Windows.Extensions", "Microsoft.VisualBasic.Forms",
    };

    private static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers
                                  | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    private static HashSet<SyntaxTree> _compiledTrees;   // компилируются в серверной сборке
    private static HashSet<SyntaxTree> _ownTrees;        // + роботы: их типы не подменяем
    private static readonly Dictionary<INamedTypeSymbol, HashSet<ISymbol>> Types = new(SymbolEqualityComparer.Default);
    private static readonly HashSet<ISymbol> OverriddenInOwnCode = new(SymbolEqualityComparer.Default);
    private static readonly SortedSet<string> UsingNamespaces = new();

    private static int Main(string[] args)
    {
        string src = args[0], filesList = args[1], refsJson = args[2], outFile = args[3];
        string[] robots = args.Skip(4).ToArray();

        var parse = new CSharpParseOptions(LanguageVersion.Preview, preprocessorSymbols: new[] { "DEBUG", "TRACE", "NET", "NET10_0" });

        var serverFiles = new HashSet<string>(File.ReadAllLines(filesList).Where(l => l.Trim() != "")
            .Select(l => Path.GetFullPath(Path.Combine(src, l.Trim()))), StringComparer.OrdinalIgnoreCase);

        var allFiles = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !Contains(f, @"\bin\") && !Contains(f, @"\obj\") && !Contains(f, @"\lua\") && !Contains(f, @"\Theme\")
                        && !f.EndsWith(@"Robots\DataTransferObjects.cs") && !f.EndsWith(@"Robots\OptionsDataCollector.cs"))
            .Concat(Directory.EnumerateFiles(Path.Combine(src, @"obj\Debug"), "*.g.cs", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".AssemblyInfo.cs") && !f.EndsWith("GlobalUsings.g.cs")))
            .ToList();

        var trees = new List<SyntaxTree>();
        _compiledTrees = new HashSet<SyntaxTree>();
        _ownTrees = new HashSet<SyntaxTree>();
        foreach (var f in allFiles)
        {
            var t = CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f);
            trees.Add(t);
            if (serverFiles.Contains(Path.GetFullPath(f))) { _compiledTrees.Add(t); _ownTrees.Add(t); }
        }
        foreach (var r in robots)
        {
            var t = CSharpSyntaxTree.ParseText(File.ReadAllText(r), parse, r);
            trees.Add(t);
            _ownTrees.Add(t);
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(refsJson));
        var refs = doc.RootElement.GetProperty("Items").GetProperty("ReferencePath").EnumerateArray()
            .Select(e => e.GetProperty("Identity").GetString()).Where(File.Exists)
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToList();

        var comp = CSharpCompilation.Create("OsEngineFull", trees, refs,
            new CSharpCompilationOptions(OutputKind.WindowsApplication, allowUnsafe: true, nullableContextOptions: NullableContextOptions.Disable));

        var errs = comp.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        Console.WriteLine($"trees={trees.Count} server={_compiledTrees.Count} refs={refs.Count} fullCompileErrors={errs.Count}");
        foreach (var e in errs.Take(15)) Console.WriteLine("  " + e);

        foreach (var t in _ownTrees) Walk(comp.GetSemanticModel(t), t.GetRoot());

        // замыкание: базовые типы, типы из сигнатур, вложенность
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var type in Types.Keys.ToList())
            {
                int before = Types.Count;
                foreach (var m in Types[type].ToList()) AddSignatureTypes(m);
                if (type.BaseType != null) AddTypeRef(type.BaseType);
                if (type.ContainingType != null) AddTypeRef(type.ContainingType);
                if (type.TypeKind == TypeKind.Delegate && type.DelegateInvokeMethod != null) AddSignatureTypes(type.DelegateInvokeMethod);
                foreach (var i in RetainedInterfaces(type)) AddTypeRef(i);
                foreach (var m in RequiredMembers(type)) { AddSignatureTypes(m); }
                if (IsCollection(type, out var elem)) AddTypeRef(elem);
                if (type.TypeKind == TypeKind.Interface && IsShimType(type))
                    foreach (var im in type.GetMembers().Where(x => x.IsAbstract && x is not IMethodSymbol { MethodKind: not MethodKind.Ordinary })) if (Types[type].Add(im)) AddSignatureTypes(im);
                if (Types.Count != before) changed = true;
            }
        }

        var sb = new StringBuilder();
        Emit(sb);
        File.WriteAllText(outFile, sb.ToString(), new UTF8Encoding(true));

        var byAsm = Types.Keys.GroupBy(t => IsUi(t) ? t.ContainingAssembly.Name : "OsEngine (вне серверного набора)")
            .OrderByDescending(g => g.Count());
        // подменённые классы OsEngine: файл \t тип — для проверки, что среди них нет нужной логики
        File.WriteAllLines(Path.Combine(Path.GetDirectoryName(outFile), "shimmed-osengine-types.txt"), Types.Keys.Where(t => !IsUi(t) && t.ContainingType == null)
            .Select(t => string.Join(";", t.DeclaringSyntaxReferences.Select(r => Path.GetRelativePath(src, r.SyntaxTree.FilePath).Replace('\\', '/')).Distinct()) + "\t" + t.ToDisplayString()).OrderBy(x => x));
        Console.WriteLine($"types={Types.Count} members={Types.Values.Sum(v => v.Count)} usingNamespaces={UsingNamespaces.Count}");
        foreach (var g in byAsm) Console.WriteLine($"  {g.Count(),4} {g.Key}");
        return 0;
    }

    private static bool Contains(string s, string part) => s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;

    // ---------- что подменяем ----------

    private static bool IsUi(ITypeSymbol t)
    {
        var asm = t.ContainingAssembly?.Name;
        return asm != null && UiAssemblyPrefixes.Any(p => asm.StartsWith(p, StringComparison.Ordinal));
    }

    private static bool IsShimType(ITypeSymbol t)
    {
        if (t is not INamedTypeSymbol nt || t is IErrorTypeSymbol) return false;
        if (IsUi(nt)) return true;
        if (nt.DeclaringSyntaxReferences.Length == 0) return false;
        // исходный тип OsEngine, ни одна часть которого не входит в серверный набор/роботов
        return nt.DeclaringSyntaxReferences.All(r => !_ownTrees.Contains(r.SyntaxTree));
    }

    // ---------- обход ----------

    private static void Walk(SemanticModel model, SyntaxNode root)
    {
        foreach (var node in root.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case UsingDirectiveSyntax u when u.Name != null:
                    if (u.Alias == null && u.StaticKeyword.IsKind(SyntaxKind.None)) UsingNamespaces.Add(u.Name.ToString());
                    Record(model.GetSymbolInfo(u.Name));
                    continue;
                case ForEachStatementSyntax fe:
                    var fi = model.GetForEachStatementInfo(fe);
                    AddMember(fi.GetEnumeratorMethod); AddTypeRef(fi.ElementType);
                    break;
                case BaseTypeDeclarationSyntax td:
                    if (model.GetDeclaredSymbol(td) is INamedTypeSymbol decl)
                    {
                        AddTypeRef(decl.BaseType);
                        foreach (var i in decl.Interfaces) AddTypeRef(i);
                    }
                    break;
                case MethodDeclarationSyntax md when md.Modifiers.Any(SyntaxKind.OverrideKeyword):
                    if (model.GetDeclaredSymbol(md) is IMethodSymbol ms && ms.OverriddenMethod != null) { AddMember(ms.OverriddenMethod); OverriddenInOwnCode.Add(Root(ms.OverriddenMethod)); }
                    break;
                case PropertyDeclarationSyntax pd when pd.Modifiers.Any(SyntaxKind.OverrideKeyword):
                    if (model.GetDeclaredSymbol(pd) is IPropertySymbol ps && ps.OverriddenProperty != null) { AddMember(ps.OverriddenProperty); OverriddenInOwnCode.Add(Root(ps.OverriddenProperty)); }
                    break;
                case EventDeclarationSyntax ed when ed.Modifiers.Any(SyntaxKind.OverrideKeyword):
                    if (model.GetDeclaredSymbol(ed) is IEventSymbol es && es.OverriddenEvent != null) { AddMember(es.OverriddenEvent); OverriddenInOwnCode.Add(Root(es.OverriddenEvent)); }
                    break;
                case InitializerExpressionSyntax ie when ie.IsKind(SyntaxKind.CollectionInitializerExpression):
                    foreach (var el in ie.Expressions) Record(model.GetCollectionInitializerSymbolInfo(el));
                    break;
            }

            if (node is ExpressionSyntax || node is ConstructorInitializerSyntax || node is AttributeSyntax)
            {
                Record(model.GetSymbolInfo(node));
                if (node is ExpressionSyntax ex)
                {
                    var ti = model.GetTypeInfo(ex);
                    AddTypeRef(ti.Type); AddTypeRef(ti.ConvertedType);
                    var conv = model.GetConversion(ex);
                    if (conv.IsUserDefined) AddMember(conv.MethodSymbol);
                }
            }
        }
    }

    private static void Record(SymbolInfo info)
    {
        if (info.Symbol != null) RecordSymbol(info.Symbol);
        foreach (var c in info.CandidateSymbols) RecordSymbol(c);
    }

    private static void RecordSymbol(ISymbol s)
    {
        switch (s)
        {
            case ITypeSymbol t: AddTypeRef(t); break;
            case IAliasSymbol a: RecordSymbol(a.Target); break;
            case IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol: AddMember(s); break;
        }
    }

    private static void AddTypeRef(ITypeSymbol t)
    {
        switch (t)
        {
            case null: return;
            case IArrayTypeSymbol a: AddTypeRef(a.ElementType); return;
            case IPointerTypeSymbol p: AddTypeRef(p.PointedAtType); return;
            case INamedTypeSymbol n:
                foreach (var ta in n.TypeArguments) AddTypeRef(ta);
                var def = n.OriginalDefinition;
                if (IsShimType(def) && !Types.ContainsKey(def))
                {
                    Types[def] = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
                    if (def.TypeKind == TypeKind.Enum)
                        foreach (var f in def.GetMembers().OfType<IFieldSymbol>()) Types[def].Add(f);
                }
                return;
        }
    }

    private static ISymbol Root(ISymbol m)
    {
        while (true)
        {
            ISymbol next = m switch
            {
                IMethodSymbol ms => ms.OverriddenMethod,
                IPropertySymbol ps => ps.OverriddenProperty,
                IEventSymbol es => es.OverriddenEvent,
                _ => null
            };
            if (next == null) return m;
            m = next;
        }
    }

    private static void AddMember(ISymbol m)
    {
        if (m == null) return;
        if (m is IMethodSymbol ms)
        {
            if (ms.ReducedFrom != null) ms = ms.ReducedFrom;
            if (ms.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet && ms.AssociatedSymbol != null) { AddMember(ms.AssociatedSymbol); return; }
            if (ms.MethodKind is MethodKind.EventAdd or MethodKind.EventRemove && ms.AssociatedSymbol != null) { AddMember(ms.AssociatedSymbol); return; }
            if (ms.MethodKind is MethodKind.LocalFunction or MethodKind.AnonymousFunction) return;
            m = ms.OriginalDefinition;
        }
        else m = m.OriginalDefinition;

        m = Root(m);
        var owner = m.ContainingType?.OriginalDefinition;
        if (owner == null) return;
        AddTypeRef(owner);
        if (!Types.TryGetValue(owner, out var set)) return;
        set.Add(m);
        AddSignatureTypes(m);
    }

    private static void AddSignatureTypes(ISymbol m)
    {
        switch (m)
        {
            case IMethodSymbol ms:
                AddTypeRef(ms.ReturnType);
                foreach (var p in ms.Parameters) AddTypeRef(p.Type);
                break;
            case IPropertySymbol ps:
                AddTypeRef(ps.Type);
                foreach (var p in ps.Parameters) AddTypeRef(p.Type);
                break;
            case IFieldSymbol fs: AddTypeRef(fs.Type); break;
            case IEventSymbol es:
                AddTypeRef(es.Type);
                if (es.Type is INamedTypeSymbol d && d.DelegateInvokeMethod != null) AddSignatureTypes(d.DelegateInvokeMethod);
                break;
        }
    }

    // ---------- интерфейсы, абстрактные члены, коллекции ----------

    private static bool IsOwnOrRealInterface(INamedTypeSymbol i)
    {
        if (IsShimType(i)) return IsUi(i) ? i.DeclaredAccessibility == Accessibility.Public && Types.ContainsKey(i.OriginalDefinition) : Types.ContainsKey(i.OriginalDefinition);
        if (i.DeclaringSyntaxReferences.Length > 0) return true; // интерфейс серверного ядра (IIBotTab, IServer...)
        string n = i.OriginalDefinition.ToDisplayString();
        return n is "System.IDisposable";
    }

    private static IEnumerable<INamedTypeSymbol> RetainedInterfaces(INamedTypeSymbol t)
    {
        var baseIfaces = t.BaseType?.AllInterfaces ?? System.Collections.Immutable.ImmutableArray<INamedTypeSymbol>.Empty;
        return t.Interfaces.Where(i => IsOwnOrRealInterface(i) && !baseIfaces.Contains(i, SymbolEqualityComparer.Default));
    }

    // члены, которые шим обязан реализовать: интерфейсы и абстрактные члены базового класса серверного ядра
    private static IEnumerable<ISymbol> RequiredMembers(INamedTypeSymbol t)
    {
        if (t.TypeKind == TypeKind.Interface) yield break;
        foreach (var i in RetainedInterfaces(t))
            foreach (var ii in new[] { i }.Concat(i.AllInterfaces))
                foreach (var m in ii.GetMembers().Where(x => x.IsAbstract && x is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
                    yield return m;
        var b = t.BaseType;
        if (b != null && !IsShimType(b) && b.IsAbstract)
            for (var cur = b; cur != null; cur = cur.BaseType)
                foreach (var m in cur.GetMembers().Where(x => x.IsAbstract && x is IMethodSymbol { MethodKind: MethodKind.Ordinary } or IPropertySymbol or IEventSymbol))
                    yield return m;
    }

    private static bool IsCollection(INamedTypeSymbol t, out ITypeSymbol elem)
    {
        elem = null;
        if (!IsUi(t) || t.TypeKind != TypeKind.Class) return false;
        if (!t.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable")) return false;
        for (var cur = t; cur != null; cur = cur.BaseType)
        {
            var idx = cur.GetMembers().OfType<IPropertySymbol>().FirstOrDefault(p => p.IsIndexer && p.Parameters.Length == 1 && p.Parameters[0].Type.SpecialType == SpecialType.System_Int32);
            if (idx != null) { elem = idx.Type; return elem.SpecialType != SpecialType.System_Object || true; }
        }
        return false;
    }

    // ---------- генерация ----------

    private static string T(ITypeSymbol t) => t.ToDisplayString(TypeFormat);

    private static string Id(string name) => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    private static void Emit(StringBuilder sb)
    {
        sb.AppendLine("// <auto-generated> ShimGen: фальшивый интерфейс для серверной сборки OsEngine. Не править вручную. </auto-generated>");
        sb.AppendLine("#pragma warning disable CS0067, CS0108, CS0114, CS0169, CS0414, CS0649, CS0659, CS0661, CS0660, CS8618, CS0109, CS0628, CS1591");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();

        var tops = Types.Keys.Where(t => t.ContainingType == null)
            .GroupBy(t => t.ContainingNamespace.IsGlobalNamespace ? "" : t.ContainingNamespace.ToDisplayString())
            .OrderBy(g => g.Key);
        var emittedNs = new HashSet<string>();
        foreach (var g in tops)
        {
            emittedNs.Add(g.Key);
            bool hasNs = g.Key != "";
            if (hasNs) sb.AppendLine($"namespace {g.Key}\n{{");
            foreach (var t in g.OrderBy(x => x.Name)) EmitType(sb, t, hasNs ? 1 : 0);
            if (hasNs) sb.AppendLine("}\n");
        }

        // пустые пространства имён из using, которых иначе не будет (неиспользуемые коннекторы и т.п.)
        sb.AppendLine("// пространства имён из using-директив");
        foreach (var ns in UsingNamespaces)
            sb.AppendLine($"namespace {ns} {{ internal static class __ShimNamespace {{ }} }}");

        sb.AppendLine();
        sb.AppendLine(ShimRuntime);
    }

    private const string ShimRuntime = @"namespace OsEngine.Headless
{
    internal static class ShimRuntime
    {
        public static object Run(global::System.Delegate d, params object[] args)
        {
            if (d == null) return null;
            int n = d.Method.GetParameters().Length;
            if (args == null || args.Length != n) args = new object[n];
            return d.DynamicInvoke(args);
        }

        public static void Message(string where, params object[] args)
        {
            global::System.Console.Error.WriteLine(""[UI "" + where + ""] "" + string.Join("" | "", args ?? new object[0]));
        }

        // ComputerInfo: Linux — /proc/meminfo (MemTotal/MemAvailable), иначе — данные сборщика мусора
        public static ulong PhysicalMemory(bool available)
        {
            try
            {
                if (global::System.IO.File.Exists(""/proc/meminfo""))
                {
                    string key = available ? ""MemAvailable:"" : ""MemTotal:"";
                    foreach (string line in global::System.IO.File.ReadLines(""/proc/meminfo""))
                        if (line.StartsWith(key))
                            return ulong.Parse(line.Substring(key.Length).Trim().Split(' ')[0]) * 1024;
                }
            }
            catch { }
            var gc = global::System.GC.GetGCMemoryInfo();
            long total = gc.TotalAvailableMemoryBytes;
            return (ulong)global::System.Math.Max(1, available ? total - gc.MemoryLoadBytes : total);
        }
    }
}";

    private static string Access(Accessibility a) => a switch
    {
        Accessibility.Public => "public",
        Accessibility.Protected => "protected",
        Accessibility.Internal => "internal",
        Accessibility.ProtectedOrInternal => "protected internal",
        Accessibility.ProtectedAndInternal => "private protected",
        _ => "public"
    };

    private static string Ind(int n) => new string(' ', n * 4);

    private static void EmitType(StringBuilder sb, INamedTypeSymbol t, int ind)
    {
        string pad = Ind(ind);
        string acc = t.DeclaredAccessibility == Accessibility.Internal || t.DeclaredAccessibility == Accessibility.Private || t.DeclaredAccessibility == Accessibility.NotApplicable
            ? (IsUi(t) ? "public" : "internal") : "public";
        if (t.ContainingType != null && t.DeclaredAccessibility == Accessibility.Protected) acc = "protected";
        string name = Id(t.Name) + (t.TypeParameters.Length > 0 ? "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">" : "");

        if (t.TypeKind == TypeKind.Enum)
        {
            if (t.GetAttributes().Any(a => a.AttributeClass?.Name == "FlagsAttribute")) sb.AppendLine(pad + "[global::System.Flags]");
            sb.AppendLine($"{pad}{acc} enum {name} : {T(t.EnumUnderlyingType)}\n{pad}{{");
            foreach (var f in t.GetMembers().OfType<IFieldSymbol>().Where(f => f.HasConstantValue))
                sb.AppendLine($"{pad}    {Id(f.Name)} = unchecked(({T(t.EnumUnderlyingType)}){Convert.ToInt64(f.ConstantValue)}L),");
            sb.AppendLine(pad + "}");
            return;
        }
        if (t.TypeKind == TypeKind.Delegate)
        {
            var inv = t.DelegateInvokeMethod;
            sb.AppendLine($"{pad}{acc} delegate {RetType(inv)} {name}({Params(inv.Parameters)});");
            return;
        }

        string kind = t.TypeKind switch { TypeKind.Struct => "struct", TypeKind.Interface => "interface", _ => "class" };
        string mods = t.TypeKind == TypeKind.Class ? (t.IsStatic ? "static " : t.IsAbstract ? "abstract " : "") : "";
        var bases = new List<string>();
        if (t.TypeKind == TypeKind.Class && t.BaseType != null && t.BaseType.SpecialType != SpecialType.System_Object && !t.IsStatic)
            bases.Add(T(t.BaseType));
        var ifaces = t.TypeKind == TypeKind.Interface
            ? t.Interfaces.Where(i => Types.ContainsKey(i.OriginalDefinition) || !IsShimType(i)).ToList()
            : RetainedInterfaces(t).ToList();
        bool coll = IsCollection(t, out var elem);
        if (coll) bases.Add("global::System.Collections.IEnumerable");
        bases.AddRange(ifaces.Select(T));
        sb.AppendLine($"{pad}{acc} {mods}partial {kind} {name}{(bases.Count > 0 ? " : " + string.Join(", ", bases.Distinct()) : "")}\n{pad}{{");

        string ip = Ind(ind + 1);
        var sigs = new HashSet<string>();
        bool isOsEngineNonUi = !IsUi(t);
        bool derivesFromServerCore = t.BaseType != null && !IsShimType(t.BaseType) && t.BaseType.SpecialType != SpecialType.System_Object && t.BaseType.DeclaringSyntaxReferences.Length > 0;

        if (coll)
            sb.AppendLine($"{ip}private readonly global::System.Collections.Generic.List<{T(elem)}> __items = new global::System.Collections.Generic.List<{T(elem)}>();\n" +
                          $"{ip}global::System.Collections.IEnumerator global::System.Collections.IEnumerable.GetEnumerator() => __items.GetEnumerator();");

        // конструктор без параметров всегда (для наследников и ленивого создания)
        if (t.TypeKind == TypeKind.Class && !t.IsStatic)
        {
            string init = BaseInit(t);
            string body = derivesFromServerCore ? "throw new global::System.NotSupportedException(\"" + t.Name + " не входит в серверную сборку\");" : "";
            sb.AppendLine($"{ip}public {Id(t.Name)}(){init} {{ {body} }}");
            sigs.Add(".ctor()");
        }

        var members = Types[t].Concat(t.TypeKind == TypeKind.Interface ? Enumerable.Empty<ISymbol>() : RequiredMembers(t))
            .Distinct(SymbolEqualityComparer.Default).ToList();
        foreach (var m in members.OrderBy(m => m.Name))
            EmitMember(sb, t, m, ip, sigs, coll, elem, derivesFromServerCore);

        foreach (var nested in Types.Keys.Where(n => SymbolEqualityComparer.Default.Equals(n.ContainingType, t)).OrderBy(n => n.Name))
            EmitType(sb, nested, ind + 1);

        sb.AppendLine(pad + "}");
    }

    private static string BaseInit(INamedTypeSymbol t)
    {
        var b = t.BaseType;
        if (b == null || b.SpecialType == SpecialType.System_Object) return "";
        if (IsShimType(b)) return "";
        var ctors = b.InstanceConstructors.Where(c => c.DeclaredAccessibility != Accessibility.Private).OrderBy(c => c.Parameters.Length).ToList();
        if (ctors.Count == 0 || ctors[0].Parameters.Length == 0) return "";
        return " : base(" + string.Join(", ", ctors[0].Parameters.Select(p => $"default({T(p.Type)})")) + ")";
    }

    private static string RetType(IMethodSymbol m) => (m.ReturnsByRef ? "ref " : m.ReturnsByRefReadonly ? "ref readonly " : "") + T(m.ReturnType);

    private static string Params(IEnumerable<IParameterSymbol> ps) => string.Join(", ", ps.Select(p =>
    {
        string mod = p.RefKind switch { RefKind.Ref => "ref ", RefKind.Out => "out ", RefKind.In => "in ", _ => "" };
        if (p.IsParams) mod = "params " + mod;
        if (p.Ordinal == 0 && p.ContainingSymbol is IMethodSymbol { IsExtensionMethod: true }) mod = "this " + mod;
        string def = p.HasExplicitDefaultValue ? " = default" : "";
        return $"{mod}{T(p.Type)} {Id(p.Name)}{def}";
    }));

    private static string DefaultValue(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String) return "\"\"";
        return $"default({T(type)})";
    }

    private static bool Lazy(ITypeSymbol type) =>
        type is INamedTypeSymbol n && n.TypeKind == TypeKind.Class && !n.IsAbstract && !n.IsStatic && IsShimType(n)
        && n.TypeParameters.Length == 0 && Types.ContainsKey(n.OriginalDefinition);

    private static void EmitMember(StringBuilder sb, INamedTypeSymbol owner, ISymbol m, string ip, HashSet<string> sigs, bool coll, ITypeSymbol elem, bool derivesFromServerCore)
    {
        bool iface = owner.TypeKind == TypeKind.Interface;
        bool fromOtherIface = !iface && m.ContainingType.TypeKind == TypeKind.Interface;           // явная реализация
        bool fromServerBase = !iface && !fromOtherIface && !SymbolEqualityComparer.Default.Equals(m.ContainingType.OriginalDefinition, owner);
        string acc = iface || fromOtherIface ? "" : Access(m.DeclaredAccessibility) + " ";
        string stat = m.IsStatic ? "static " : "";
        string virt = "";
        if (!iface && !fromOtherIface && !m.IsStatic && owner.TypeKind == TypeKind.Class)
            virt = fromServerBase ? "override " : owner.IsSealed ? "" : "virtual ";
        if (owner.TypeKind == TypeKind.Struct) virt = "";
        string explicitPrefix = fromOtherIface ? T(m.ContainingType) + "." : "";

        switch (m)
        {
            case IFieldSymbol f:
            {
                if (owner.TypeKind == TypeKind.Enum) return;
                if (!sigs.Add("F:" + f.Name)) return;
                if (f.IsConst) sb.AppendLine($"{ip}{acc}const {T(f.Type)} {Id(f.Name)} = {Literal(f)};");
                else if (Lazy(f.Type)) sb.AppendLine($"{ip}{acc}{stat}{T(f.Type)} {Id(f.Name)} = new {T(f.Type)}();");
                else sb.AppendLine($"{ip}{acc}{stat}{T(f.Type)} {Id(f.Name)}{(f.Type.SpecialType == SpecialType.System_String && owner.TypeKind != TypeKind.Struct ? " = \"\"" : "")};");
                return;
            }
            case IEventSymbol e:
            {
                if (!sigs.Add("E:" + e.Name)) return;
                if (iface) sb.AppendLine($"{ip}event {T(e.Type)} {Id(e.Name)};");
                else if (fromOtherIface) sb.AppendLine($"{ip}event {T(e.Type)} {explicitPrefix}{Id(e.Name)} {{ add {{ }} remove {{ }} }}");
                else sb.AppendLine($"{ip}{acc}{stat}{virt}event {T(e.Type)} {Id(e.Name)};");
                return;
            }
            case IPropertySymbol p:
            {
                string ps = p.IsIndexer ? "this[" + Params(p.Parameters) + "]" : Id(p.Name);
                string key = "P:" + (p.IsIndexer ? "this[" + string.Join(",", p.Parameters.Select(x => T(x.Type))) + "]" : p.Name);
                if (!sigs.Add(key)) return;
                bool hasSet = p.SetMethod != null || !fromServerBase && !fromOtherIface && !iface;
                bool hasGet = p.GetMethod != null || iface || fromOtherIface || fromServerBase ? p.GetMethod != null : true;
                if (iface)
                {
                    sb.AppendLine($"{ip}{T(p.Type)} {ps} {{ {(p.GetMethod != null ? "get; " : "")}{(p.SetMethod != null ? "set; " : "")}}}");
                    return;
                }
                string getBody, setBody;
                if (coll && p.IsIndexer && p.Parameters.Length == 1 && p.Parameters[0].Type.SpecialType == SpecialType.System_Int32)
                {
                    // абстрактная ячейка таблицы: создаём текстовую (Log.GetLastMessages читает Cells[2] у пустой строки)
                    string concrete = Lazy(elem) ? T(elem)
                        : elem is INamedTypeSymbol en && en.IsAbstract && en.ToDisplayString() == "System.Windows.Forms.DataGridViewCell"
                            ? "global::System.Windows.Forms.DataGridViewTextBoxCell" : null;
                    string create = concrete != null ? $"while (__items.Count <= {Id(p.Parameters[0].Name)}) __items.Add(new {concrete}());" : "";
                    getBody = $"get {{ {create} return {Id(p.Parameters[0].Name)} >= 0 && {Id(p.Parameters[0].Name)} < __items.Count ? ({T(p.Type)})(object)__items[{Id(p.Parameters[0].Name)}] : default({T(p.Type)}); }}";
                    setBody = $"set {{ }}";
                }
                else if (coll && !p.IsIndexer && p.Name == "Count" && p.Type.SpecialType == SpecialType.System_Int32)
                {
                    getBody = "get => __items.Count;"; setBody = "set { }";
                }
                else if (owner.Name == "ComputerInfo" && p.Name is "TotalPhysicalMemory" or "AvailablePhysicalMemory")
                {
                    // SystemUsageAnalyzeMaster делит на объём памяти: ноль из шима дал бы DivideByZero каждую секунду
                    getBody = $"get => global::OsEngine.Headless.ShimRuntime.PhysicalMemory({(p.Name == "AvailablePhysicalMemory" ? "true" : "false")});";
                    setBody = "set { }";
                }
                else if (p.IsIndexer)
                {
                    getBody = Lazy(p.Type) ? $"get => new {T(p.Type)}();" : $"get => {DefaultValue(p.Type)};";
                    setBody = "set { }";
                }
                else if (fromOtherIface || fromServerBase || p.IsStatic && Lazy(p.Type) || p.ReturnsByRef)
                {
                    getBody = Lazy(p.Type) ? $"get => new {T(p.Type)}();" : $"get => {DefaultValue(p.Type)};";
                    setBody = "set { }";
                }
                else
                {
                    // автосвойство; ссылочные типы шима создаются лениво, строки — пустые
                    string field = "__" + p.Name;
                    if (Lazy(p.Type))
                    {
                        sb.AppendLine($"{ip}private {stat}{T(p.Type)} {field};");
                        getBody = $"get => {field} ?? ({field} = new {T(p.Type)}());";
                    }
                    else
                    {
                        sb.AppendLine($"{ip}private {stat}{T(p.Type)} {field}{(p.Type.SpecialType == SpecialType.System_String && owner.TypeKind != TypeKind.Struct ? " = \"\"" : "")};");
                        getBody = $"get => {field};";
                    }
                    setBody = $"set => {field} = value;";
                }
                string accessors = (p.GetMethod != null || !(fromOtherIface || fromServerBase) ? getBody + " " : "")
                                   + (p.SetMethod != null || !(fromOtherIface || fromServerBase) ? setBody + " " : "");
                if (p.ReturnsByRef) accessors = getBody + " ";
                sb.AppendLine($"{ip}{acc}{stat}{virt}{(p.ReturnsByRef ? "ref " : "")}{T(p.Type)} {explicitPrefix}{ps} {{ {accessors}}}");
                return;
            }
            case IMethodSymbol ms:
            {
                if (ms.MethodKind == MethodKind.StaticConstructor || ms.MethodKind == MethodKind.Destructor) return;
                string key = "M:" + ms.MethodKind + ":" + ms.Name + "(" + string.Join(",", ms.Parameters.Select(x => x.RefKind + T(x.Type))) + ")";
                if (ms.MethodKind == MethodKind.Constructor) key = ".ctor(" + string.Join(",", ms.Parameters.Select(x => T(x.Type))) + ")";
                if (!sigs.Add(key)) return;
                if (ms.MethodKind == MethodKind.Constructor)
                {
                    if (owner.TypeKind == TypeKind.Struct && ms.Parameters.Length == 0) return;
                    string body = derivesFromServerCore ? "throw new global::System.NotSupportedException(\"" + owner.Name + " не входит в серверную сборку\");" : OutInit(ms);
                    sb.AppendLine($"{ip}public {Id(owner.Name)}({Params(ms.Parameters)}){BaseInit(owner)} {{ {body} }}");
                    return;
                }
                string tps = ms.TypeParameters.Length > 0 ? "<" + string.Join(", ", ms.TypeParameters.Select(x => x.Name)) + ">" : "";
                if (ms.MethodKind == MethodKind.UserDefinedOperator || ms.MethodKind == MethodKind.Conversion)
                {
                    string opName = ms.MethodKind == MethodKind.Conversion
                        ? (ms.Name == "op_Implicit" ? "implicit" : "explicit") + " operator " + T(ms.ReturnType)
                        : T(ms.ReturnType) + " operator " + OperatorToken(ms.Name);
                    sb.AppendLine($"{ip}public static {opName}({Params(ms.Parameters)}) => default({T(ms.ReturnType)});");
                    return;
                }
                if (iface) { sb.AppendLine($"{ip}{RetType(ms)} {Id(ms.Name)}{tps}({Params(ms.Parameters)});"); return; }
                string mbody = MethodBody(owner, ms, coll, elem);
                sb.AppendLine($"{ip}{acc}{stat}{virt}{RetType(ms)} {explicitPrefix}{Id(ms.Name)}{tps}({Params(ms.Parameters)}) {{ {mbody} }}");
                return;
            }
        }
    }

    private static string OutInit(IMethodSymbol ms) =>
        string.Concat(ms.Parameters.Where(p => p.RefKind == RefKind.Out).Select(p => $"{Id(p.Name)} = default({T(p.Type)}); "));

    private static string MethodBody(INamedTypeSymbol owner, IMethodSymbol ms, bool coll, ITypeSymbol elem)
    {
        string outs = OutInit(ms);
        bool isVoid = ms.ReturnsVoid;
        string ret = isVoid ? "" : ms.ReturnsByRef ? $"throw new global::System.NotSupportedException();" : $"return {(ms.ReturnType.SpecialType == SpecialType.System_String ? "\"\"" : $"default({T(ms.ReturnType)})")};";
        string n = ms.Name;
        var p = ms.Parameters;
        string P(int i) => Id(p[i].Name);

        // выполнить переданный делегат сразу (Dispatcher.Invoke, Control.Invoke, BeginInvoke ...)
        int di = p.ToList().FindIndex(x => x.Type.TypeKind == TypeKind.Delegate || x.Type.ToDisplayString() == "System.Delegate");
        if (n.Contains("Invoke") && di >= 0)
        {
            string args = p.Length > di + 1 && p.Last().IsParams ? ", " + P(p.Length - 1) : "";
            string call = $"global::OsEngine.Headless.ShimRuntime.Run({P(di)}{args})";
            if (isVoid) return outs + call + ";";
            if (ms.ReturnType.SpecialType == SpecialType.System_Object) return outs + "return " + call + ";";
            if (ms.ReturnType is INamedTypeSymbol rt && rt.TypeArguments.Length == 0 && ms.TypeParameters.Length > 0 && SymbolEqualityComparer.Default.Equals(ms.ReturnType, ms.TypeParameters[0]))
                return outs + $"return ({T(ms.ReturnType)}){call};";
            if (SymbolEqualityComparer.Default.Equals(ms.ReturnType, ms.TypeParameters.FirstOrDefault()))
                return outs + $"return ({T(ms.ReturnType)}){call};";
            return outs + call + "; " + ret;
        }
        if (n == "CheckAccess" && ms.ReturnType.SpecialType == SpecialType.System_Boolean) return "return true;";
        if (owner.Name == "MessageBox" && n == "Show") return outs + $"global::OsEngine.Headless.ShimRuntime.Message(\"MessageBox\"{string.Concat(p.Select(x => ", " + Id(x.Name)))}); " + ret;

        if (coll)
        {
            string e = T(elem);
            if (n == "Add" && p.Length == 1 && SymbolEqualityComparer.Default.Equals(p[0].Type, elem))
                return outs + $"__items.Add({P(0)}); " + (isVoid ? "" : ms.ReturnType.SpecialType == SpecialType.System_Int32 ? "return __items.Count - 1;" : ret);
            if (n == "Add" && Lazy(elem))
                return outs + $"__items.Add(new {e}()); " + (isVoid ? "" : ms.ReturnType.SpecialType == SpecialType.System_Int32 ? "return __items.Count - 1;" : SymbolEqualityComparer.Default.Equals(ms.ReturnType, elem) ? "return __items[__items.Count - 1];" : ret);
            if (n == "Insert" && p.Length == 2 && SymbolEqualityComparer.Default.Equals(p[1].Type, elem)) return $"__items.Insert(global::System.Math.Min({P(0)}, __items.Count), {P(1)});";
            if (n == "Insert" && p.Length >= 1 && p[0].Type.SpecialType == SpecialType.System_Int32 && Lazy(elem)) return $"__items.Insert(global::System.Math.Min({P(0)}, __items.Count), new {e}());";
            if (n == "Remove" && p.Length == 1 && SymbolEqualityComparer.Default.Equals(p[0].Type, elem)) return $"__items.Remove({P(0)}); " + ret;
            if (n == "RemoveAt" && p.Length == 1) return $"if ({P(0)} >= 0 && {P(0)} < __items.Count) __items.RemoveAt({P(0)});";
            if (n == "Clear" && p.Length == 0) return "__items.Clear();";
            if (n == "Contains" && p.Length == 1 && SymbolEqualityComparer.Default.Equals(p[0].Type, elem)) return $"return __items.Contains({P(0)});";
            if (n == "IndexOf" && p.Length == 1 && SymbolEqualityComparer.Default.Equals(p[0].Type, elem)) return $"return __items.IndexOf({P(0)});";
            if (n == "AddRange" && p.Length == 1 && p[0].Type is IArrayTypeSymbol at && SymbolEqualityComparer.Default.Equals(at.ElementType, elem)) return $"__items.AddRange({P(0)});";
            if (n == "GetEnumerator" && p.Length == 0) return $"return ({T(ms.ReturnType)})(object)__items.GetEnumerator();";
        }
        // коллекция на настоящем Collection<T> (DataPointCollection и т.п.): AddXY/AddY/... добавляют пустой элемент
        // и возвращают его индекс, иначе следующее обращение по индексу падает (FF143: Points[Points.AddXY(..)])
        var baseElem = RealCollectionElement(owner);
        if (baseElem != null && !ms.IsStatic && n.StartsWith("Add") && ms.ReturnType.SpecialType == SpecialType.System_Int32 && Lazy(baseElem))
            return outs + $"var __e = new {T(baseElem)}(); ((global::System.Collections.ObjectModel.Collection<{T(baseElem)}>)(object)this).Add(__e); return ((global::System.Collections.ObjectModel.Collection<{T(baseElem)}>)(object)this).Count - 1;";
        if (baseElem != null && !ms.IsStatic && n.StartsWith("Add") && SymbolEqualityComparer.Default.Equals(ms.ReturnType, baseElem) && Lazy(baseElem))
            return outs + $"var __e = new {T(baseElem)}(); ((global::System.Collections.ObjectModel.Collection<{T(baseElem)}>)(object)this).Add(__e); return __e;";
        if (!isVoid && Lazy(ms.ReturnType) && n != "Clone") return outs + $"return new {T(ms.ReturnType)}();";
        return outs + ret;
    }

    private static ITypeSymbol RealCollectionElement(INamedTypeSymbol t)
    {
        for (var cur = t.BaseType; cur != null; cur = cur.BaseType)
            if (cur.OriginalDefinition.ToDisplayString() == "System.Collections.ObjectModel.Collection<T>")
                return cur.TypeArguments[0] is ITypeParameterSymbol ? null : cur.TypeArguments[0];
        return null;
    }

    private static string Literal(IFieldSymbol f)
    {
        var v = f.ConstantValue;
        if (v == null) return "default";
        if (f.Type.TypeKind == TypeKind.Enum) return $"({T(f.Type)}){Convert.ToInt64(v)}";
        return v switch
        {
            string s => SymbolDisplay.FormatLiteral(s, true),
            char c => SymbolDisplay.FormatLiteral(c, true),
            bool b => b ? "true" : "false",
            float x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f",
            double x => x.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "d",
            decimal x => x.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m",
            _ => $"unchecked(({T(f.Type)}){Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)})"
        };
    }

    private static string OperatorToken(string name) => name switch
    {
        "op_Addition" => "+", "op_Subtraction" => "-", "op_Multiply" => "*", "op_Division" => "/", "op_Modulus" => "%",
        "op_Equality" => "==", "op_Inequality" => "!=", "op_LessThan" => "<", "op_GreaterThan" => ">",
        "op_LessThanOrEqual" => "<=", "op_GreaterThanOrEqual" => ">=", "op_BitwiseAnd" => "&", "op_BitwiseOr" => "|",
        "op_ExclusiveOr" => "^", "op_UnaryNegation" => "-", "op_UnaryPlus" => "+", "op_LogicalNot" => "!",
        "op_OnesComplement" => "~", "op_Increment" => "++", "op_Decrement" => "--", "op_True" => "true", "op_False" => "false",
        _ => throw new NotSupportedException(name)
    };
}
