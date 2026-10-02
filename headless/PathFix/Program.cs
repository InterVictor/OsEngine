// PathFix: копии исходников серверного ядра OsEngine с путями, понятными Linux.
//
// OsEngine пишет пути как "Engine\" + name; на Linux '\' — обычный символ имени файла. Инструмент переписывает
// (только в копиях, оригиналы не трогает):
//   * строковые литералы без пробелов, содержащие '\'  ->  '\' заменён на '/'  (Windows '/' тоже понимает);
//   * символ '\\' в Split/IndexOf/LastIndexOf/... -> System.IO.Path.DirectorySeparatorChar.
// Не трогает: регулярные выражения, первый аргумент Replace (очистка имён), тексты с пробелами (локализация),
// файлы из списка исключений (экранирование Telegram).
// Номера строк сохраняются; в начало файла ставится #line с оригинальным путём.
//
// Запуск: dotnet run -- <OsEngineSrc> <files.txt> <outDir> [report.tsv]
// Режим scan: dotnet run -- scan <OsEngineSrc> <files.txt>   — только перечислить литералы с '\'.

using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

internal static class Program
{
    private static readonly string[] ExcludedFiles = { "Logging/ServerTelegram.cs" };
    private static readonly string[] SeparatorMethods = { "Split", "IndexOf", "LastIndexOf", "Contains", "EndsWith", "StartsWith", "TrimEnd", "TrimStart", "Trim" };

    private static int Main(string[] args)
    {
        if (args[0] == "scan") return Scan(args[1], args[2]);

        string src = args[0], list = args[1], outDir = args[2];
        var report = new List<string>();
        int files = 0, changed = 0;
        foreach (var rel in File.ReadAllLines(list).Select(l => l.Trim()).Where(l => l != ""))
        {
            string full = Path.GetFullPath(Path.Combine(src, rel));
            string text = File.ReadAllText(full);
            string outPath = Path.Combine(outDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(outPath));
            files++;

            string result = text;
            if (!ExcludedFiles.Contains(rel.Replace('\\', '/')))
            {
                var tree = CSharpSyntaxTree.ParseText(text, path: full);
                var rw = new Rewriter(rel, report);
                var root = rw.Visit(tree.GetRoot());
                if (rw.Count > 0) { changed++; result = root.ToFullString(); }
            }
            // #line: стек вызовов и ошибки указывают на оригинальный файл, номера строк не меняются
            string header = $"#line 1 \"{full.Replace("\\", "\\\\")}\"\n";
            WriteIfChanged(outPath, header + result);
        }
        if (args.Length > 3) File.WriteAllLines(args[3], report);
        Console.WriteLine($"PathFix: files={files} changedFiles={changed} replacements={report.Count}");
        return 0;
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return; // не трогаем время — инкрементальная сборка
        File.WriteAllText(path, content, new UTF8Encoding(true));
    }

    private sealed class Rewriter : CSharpSyntaxRewriter
    {
        private readonly string _rel;
        private readonly List<string> _report;
        public int Count;

        public Rewriter(string rel, List<string> report) : base(visitIntoStructuredTrivia: false) { _rel = rel; _report = report; }

        private void Log(SyntaxNode n, string before, string after)
        {
            Count++;
            int line = n.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            _report.Add($"{_rel}:{line}\t{before}\t{after}");
        }

        public override SyntaxNode VisitLiteralExpression(LiteralExpressionSyntax node)
        {
            var tok = node.Token;
            if (tok.IsKind(SyntaxKind.StringLiteralToken))
            {
                string v = tok.ValueText;
                if (v.Contains('\\') && IsPathLike(v) && !Excluded(node))
                {
                    string nv = v.Replace('\\', '/');
                    Log(node, v, nv);
                    return node.WithToken(SyntaxFactory.Literal(tok.LeadingTrivia, SyntaxFactory.Literal(nv).Text, nv, tok.TrailingTrivia));
                }
            }
            else if (tok.IsKind(SyntaxKind.CharacterLiteralToken) && tok.ValueText == "\\" && IsSeparatorUse(node) && !Excluded(node))
            {
                Log(node, "'\\\\'", "Path.DirectorySeparatorChar");
                return SyntaxFactory.ParseExpression("global::System.IO.Path.DirectorySeparatorChar").WithTriviaFrom(node);
            }
            return base.VisitLiteralExpression(node);
        }

        public override SyntaxNode VisitInterpolatedStringText(InterpolatedStringTextSyntax node)
        {
            var tok = node.TextToken;
            string v = tok.ValueText;
            if (!v.Contains('\\') || !IsPathLike(v) || Excluded(node)) return base.VisitInterpolatedStringText(node);
            var owner = node.FirstAncestorOrSelf<InterpolatedStringExpressionSyntax>();
            bool verbatim = owner.StringStartToken.Text.Contains('@');
            string nt = verbatim ? tok.Text.Replace("\\", "/") : tok.Text.Replace("\\\\", "/");
            string nv = v.Replace('\\', '/');
            Log(node, v, nv);
            return node.WithTextToken(SyntaxFactory.Token(tok.LeadingTrivia, SyntaxKind.InterpolatedStringTextToken, nt, nv, tok.TrailingTrivia));
        }

        // путь: нет пробельных символов (иначе это текст/локализация) и нет признаков регулярного выражения
        private static bool IsPathLike(string v) =>
            !v.Contains(':') && !v.Any(char.IsWhiteSpace) &&!v.Contains("\\d") && !v.Contains("\\s") && !v.Contains("\\w") && !v.Contains("\\{") && !v.Contains("\\(") && !v.Contains("\\.");

        private static bool Excluded(SyntaxNode node)
        {
            foreach (var a in node.Ancestors())
            {
                if (a is ObjectCreationExpressionSyntax oc && oc.Type.ToString().EndsWith("Regex")) return true;
                if (a is InvocationExpressionSyntax inv)
                {
                    string name = inv.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
                        IdentifierNameSyntax id => id.Identifier.Text,
                        _ => ""
                    };
                    if (inv.Expression.ToString().StartsWith("Regex.")) return true;
                    // .Replace("\\", "") — очистка имён от символов, не путь
                    if (name == "Replace" && inv.ArgumentList.Arguments.Count > 0 && inv.ArgumentList.Arguments[0].Span.Contains(node.Span)) return true;
                }
                if (a is StatementSyntax) break;
            }
            return false;
        }

        private static bool IsSeparatorUse(SyntaxNode node)
        {
            if (node.Parent is not ArgumentSyntax arg) return false;
            if (arg.Parent?.Parent is not InvocationExpressionSyntax inv) return false;
            return inv.Expression is MemberAccessExpressionSyntax ma && SeparatorMethods.Contains(ma.Name.Identifier.Text);
        }
    }

    private static int Scan(string src, string list)
    {
        foreach (var rel in File.ReadAllLines(list).Where(l => l.Trim() != ""))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(src, rel)));
            foreach (var tok in tree.GetRoot().DescendantTokens())
            {
                string v = tok.Kind() switch
                {
                    SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken or SyntaxKind.InterpolatedStringTextToken => tok.ValueText,
                    _ => null
                };
                if (v == null || !v.Contains('\\')) continue;
                int line = tok.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
                Console.WriteLine($"{tok.Kind()}\t{rel}:{line}\t{v.Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t")}");
            }
        }
        return 0;
    }
}
