using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Data;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Xml;

// TODO: 
// Use workspaces and parse csproj instead?
// Fix formatting after we mess with code.
// Try random stuff from https://github.com/dotnet/roslyn-sdk/tree/master/samples/CSharp/TreeTransforms
// See http://roslynquoter.azurewebsites.net/ for tool that shows how use roslyn APIs for C# syntax.
// Useful if you can express what you want in C# and need to see how to get a transform to create it for you.

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace MutateTest
{
    class Options
    {
        public string InputFile { get; set; }
        public bool EhStress { get; set; }
        public bool StructStress { get; set; }
        public bool ShowResults { get; set; }
    }

    public class MutateTestException : Exception
    {

    }

    class Program
    {
        private static readonly CSharpCompilationOptions DebugOptions =
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, concurrentBuild: false, optimizationLevel: OptimizationLevel.Debug);

        private static readonly CSharpCompilationOptions ReleaseOptions =
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, concurrentBuild: false, optimizationLevel: OptimizationLevel.Release);

        private static readonly CSharpParseOptions ParseOptions = new CSharpParseOptions(LanguageVersion.Latest);

        private static readonly MetadataReference[] References =
{
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Assembly.GetExecutingAssembly().Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            // These two are needed to properly pick up System.Object when using methods on System.Console.
            // See here: https://github.com/dotnet/corefx/issues/11601
            MetadataReference.CreateFromFile(Assembly.Load(new AssemblyName("System.Runtime")).Location),
            MetadataReference.CreateFromFile(Assembly.Load(new AssemblyName("mscorlib")).Location),
        };

        static int Main(string[] args)
        {
            RootCommand rootCommand = new RootCommand();
            rootCommand.Description = "Take an existing test case and produce new test cases via mutation";

            Argument inputFile = new Argument<string>();
            inputFile.Name = "inputFile";
            inputFile.Description = "Input test case file";
            rootCommand.AddArgument(inputFile);

            Option ehStressOption = new Option("--ehStress", "add EH to methods", new Argument<bool>());
            rootCommand.AddOption(ehStressOption);

            Option structStressOption = new Option("--structStress", "replace locals with structs", new Argument<bool>());
            rootCommand.AddOption(structStressOption);

            Option showResultsOption = new Option("--showResults", "print modified programs to stdout", new Argument<bool>());
            rootCommand.AddOption(showResultsOption);

            rootCommand.Handler = CommandHandler.Create<Options>((options) =>
            {
                return InnerMain(options);
            });

            return rootCommand.InvokeAsync(args).Result;
        }

        static int InnerMain(Options options)
        {
            Console.WriteLine("---------------------------------------");
            Console.WriteLine("// Original Program");

            // Access input and build parse tree
            if (!File.Exists(options.InputFile))
            {
                Console.WriteLine($"Can't access '{options.InputFile}'");
                return -1;
            }

            string inputText = File.ReadAllText(options.InputFile);
            SyntaxTree inputTree = CSharpSyntaxTree.ParseText(inputText,
                    path: options.InputFile,
                    options: ParseOptions);

            int inputResult = CompileAndExecute(inputTree, options.InputFile);

            if (inputResult != 100)
            {
                return inputResult;
            }

            // Ok, we have a compile and runnable test case. Now, mess with it....
            if (options.EhStress)
            {
                // Singleton stressors
                EHMutator tryCatch = new WrapBlocksInTryCatch();
                EHMutator tryEmptyFinally = new WrapBlocksInTryEmptyFinally();
                EHMutator emptyTryFinally = new WrapBlocksInEmptyTryFinally();
                EHMutator moveToCatch = new MoveBlocksIntoCatchClauses();

                // Repeated stressors
                EHMutator tryCatchx2 = new RepeatMutator(tryCatch, 2);
                EHMutator tryEmtpyFinallyx2 = new RepeatMutator(tryEmptyFinally, 2);
                EHMutator emptyTryFinallyx2 = new RepeatMutator(emptyTryFinally, 2);
                EHMutator moveToCatchx2 = new RepeatMutator(moveToCatch, 2);

                // Combination stressors
                EHMutator combo1 = new ComboMutator(tryEmptyFinally, tryCatch);
                EHMutator combo2 = new ComboMutator(emptyTryFinally, tryCatch);
                EHMutator combo3 = new ComboMutator(emptyTryFinally, tryEmptyFinally);
                EHMutator combo4 = new ComboMutator(moveToCatch, tryEmptyFinally);

                // Combos of combos
                EHMutator combo12 = new ComboMutator(combo1, combo2);
                EHMutator combo34 = new ComboMutator(combo3, combo4);
                EHMutator combo1234 = new ComboMutator(combo12, combo34);
                EHMutator combo3412 = new ComboMutator(combo34, combo12);

                // Repeats of Combos
                EHMutator combo1x2 = new RepeatMutator(combo1, 2);
                EHMutator combo4x2 = new RepeatMutator(combo4, 2);
                EHMutator combo1234x2 = new RepeatMutator(combo1234, 2);

                // More
                EHMutator complex1 = new ComboMutator(combo1x2, combo4x2);
                EHMutator complex2 = new RepeatMutator(complex1, 2);

                EHMutator[] stressors = new EHMutator[] {
                    tryCatch, tryCatchx2,
                    tryEmptyFinally, tryEmtpyFinallyx2,
                    emptyTryFinally, emptyTryFinallyx2,
                    moveToCatch, moveToCatchx2,
                    combo1, combo2, combo3, combo4,
                    combo12, combo34, combo1234, combo3412,
                    combo1x2, combo4x2, combo1234x2,
                    complex1, complex2};

                int variantNumber = 0;

                foreach (var stressor in stressors)
                {
                    int stressResult = ApplyStress(variantNumber++, stressor, inputTree, options);

                    if (stressResult != 100)
                    {
                        return stressResult;
                    }
                }
            }

            return 100;
        }

        static int ApplyStress(int variantNumber, EHMutator m, SyntaxTree tree, Options options)
        {
            string title = $"// EH Stress [{variantNumber}]: {m.Name}";
            Console.WriteLine();
            Console.WriteLine("---------------------------------------");
            Console.WriteLine(title);
            SyntaxNode newRoot = m.Mutate(tree.GetRoot());

            if (options.ShowResults)
            {
                Console.WriteLine(newRoot.ToFullString());
            }

            SyntaxTree newTree = SyntaxTree(newRoot, ParseOptions);

            int stressResult = CompileAndExecute(newTree, title);

            return stressResult;
        }

        static int CompileAndExecute(SyntaxTree inputTree, string name)
        {
            //Console.WriteLine($"Compiling {name} with assembly references:");
            //foreach (var reference in References)
            //{
            //    Console.WriteLine($"{reference.Display}");
            //}

            SyntaxTree[] inputTrees = { inputTree };
            CSharpCompilation compilation = CSharpCompilation.Create("InputProgram", inputTrees, References, ReleaseOptions);

            using (var ms = new MemoryStream())
            {
                EmitResult result;
                try
                {
                    result = compilation.Emit(ms);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"// Compilation of '{name}' failed: {ex.Message}");
                    return -1;
                }

                if (!result.Success)
                {
                    Console.WriteLine($"// Compilation of '{name}' failed: {result.Diagnostics.Length} errors");
                    foreach (var d in result.Diagnostics)
                    {
                        Console.WriteLine(d);
                    }
                    return -1;
                }

                Console.WriteLine($"// Compiled '{name}' successfully");

                // Load up the assembly and run the test case.
                Assembly inputAssembly = Assembly.Load(ms.GetBuffer());
                MethodInfo inputAssemblyEntry = inputAssembly.EntryPoint;
                object inputResult = null;

                if (inputAssemblyEntry.GetParameters().Length == 0)
                {
                    inputResult = inputAssemblyEntry.Invoke(null, new object[] { });
                }
                else
                {
                    string[] arglist = new string[] { };
                    inputResult = inputAssemblyEntry.Invoke(null, new object[] { arglist });
                }

                if ((int)inputResult != 100)
                {
                    Console.WriteLine($"// Execution of '{name}' failed (exitCode {inputResult})");
                    return -1;
                }

                Console.WriteLine($"// Execution of '{name}' succeeded (exitCode {inputResult})");
                return 100;
            }
        }
    }

    // Base class for EH Mutations
    //
    // EH Mutations add "semantic preserving" EH constructs to
    // methods. This can be useful for stress testing the EH
    // handling in the jit, or for getting an estimate of the
    // perf impact of having EH constructs in code.
    //
    // TODO: 
    //   bottom-up rewriting?
    //   apply to all blocks or more blocks
    //   split up statements within a block
    //   mix stress transforms randomly; iterate on them
    public abstract class EHMutator : CSharpSyntaxRewriter
    {
        public abstract string Name { get; }

        protected void Announce(SyntaxNode node)
        {
            var lineSpan = node.GetLocation().GetMappedLineSpan();
            Console.WriteLine($"// Adding {Name} around lines {lineSpan.StartLinePosition.Line}-{lineSpan.EndLinePosition.Line}");
        }

        protected static bool IsInTryBlock(SyntaxNode baseNode)
        {
            SyntaxNode node = baseNode.Parent;
            while (node != null)
            {
                switch (node.Kind())
                {
                    case SyntaxKind.TryStatement:
                    case SyntaxKind.UsingStatement:
                    case SyntaxKind.ForEachStatement:
                    case SyntaxKind.ForEachVariableStatement:
                        // Latter 3 may not create trys, but can
                        return true;
                    case SyntaxKind.SimpleLambdaExpression:
                    case SyntaxKind.ParenthesizedLambdaExpression:
                    case SyntaxKind.AnonymousMethodExpression:
                        // Stop looking.
                        return false;
                    case SyntaxKind.CatchClause:
                        // If we're in the catch of a try-catch-finally, then
                        // we're still in the scope of the try-finally handler.
                        if (((TryStatementSyntax)node.Parent).Finally != null)
                        {
                            return true;
                        }
                        goto case SyntaxKind.FinallyClause;
                    case SyntaxKind.FinallyClause:
                        // Skip past the enclosing try to avoid a false positive.
                        node = node.Parent;
                        node = node.Parent;
                        break;
                    default:
                        if (node is MemberDeclarationSyntax)
                        {
                            // Stop looking.
                            return false;
                        }
                        node = node.Parent;
                        break;
                }
            }

            return false;
        }

        // More generally, things that can't be in finallys
        protected static bool ContainsReturnOrThrow(SyntaxNode node)
        {
            return node.DescendantNodes(descendIntoTrivia: false).
                Any(x =>
                    (x.Kind() == SyntaxKind.ReturnStatement) ||
                     x.Kind() == SyntaxKind.ThrowStatement);
        }

        public virtual SyntaxNode Mutate(SyntaxNode node)
        {
            return Visit(node);
        }
    }

    // Rewrite any top-level block that is not enclosed in a try
    // as a try { <block> } catch (MutateTest.MutateTestException) { throw; }
    public class WrapBlocksInTryCatch : EHMutator
    {
        public override string Name => "TryCatch";

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            Announce(node);

            var newNode = Block(
                        SingletonList<StatementSyntax>(
                            TryStatement(
                                SingletonList<CatchClauseSyntax>(
                                    CatchClause()
                                    .WithDeclaration(
                                        CatchDeclaration(
                                            QualifiedName(
                                                IdentifierName("MutateTest"),
                                                IdentifierName("MutateTestException"))))
                                    .WithBlock(
                                        Block(
                                            SingletonList<StatementSyntax>(
                                                ThrowStatement())))))
                            .WithBlock(node)))
                .NormalizeWhitespace();
            return newNode;
        }
    }

    // Rewrite any top-level block that is not enclosed in a try
    // as a try { } finally { <block> }
    public class WrapBlocksInEmptyTryFinally : EHMutator
    {
        public override string Name => "EmptyTryFinally";

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            if (ContainsReturnOrThrow(node)) return base.VisitBlock(node);

            Announce(node);

            var newNode = Block(
                            SingletonList<StatementSyntax>(
                                TryStatement()
                                    .WithFinally(
                                        FinallyClause(node))))
                            .NormalizeWhitespace();
            return newNode;
        }
    }

    // Rewrite any top-level block that is not enclosed in a try
    // as a try { <block> } finally { }
    public class WrapBlocksInTryEmptyFinally : EHMutator
    {
        public override string Name => "TryEmptyFinally";


        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            Announce(node);

            var newNode = Block(
                            SingletonList<StatementSyntax>(
                                TryStatement()
                                .WithBlock(node)
                                .WithFinally(FinallyClause(Block()))))
                            .NormalizeWhitespace();
            return newNode;
        }
    }

    // Rewrite any top-level block into a 
    // try { throw MutateTestException; } catch (MutateTestException) { <block> }
    public class MoveBlocksIntoCatchClauses : EHMutator
    {
        public override string Name => "IntoCatch";

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            Announce(node);

            var newNode = Block(
                        SingletonList<StatementSyntax>(
                            TryStatement(
                                SingletonList<CatchClauseSyntax>(
                                    CatchClause()
                                    .WithDeclaration(
                                        CatchDeclaration(
                                            QualifiedName(
                                                IdentifierName("MutateTest"),
                                                IdentifierName("MutateTestException"))))
                                    .WithBlock(node)))
                               .WithBlock(
                                   Block(
                                            SingletonList<StatementSyntax>(
                                                ThrowStatement(
                                                    ObjectCreationExpression(
                                                         QualifiedName(
                                                            IdentifierName("MutateTest"),
                                                            IdentifierName("MutateTestException")))
                                                    .WithArgumentList(
                                                     ArgumentList())))))))
                            .NormalizeWhitespace();

            return newNode;
        }
    }

    // Rewrite any top-level block into a 
    // try { throw MutateTestException; } filter (1) filter-handler  { <block> } catch { }
    public class MoveBlocksIntoFilterClauses : EHMutator
    {
        public override string Name => "IntoFilter";
    }

    // Apply two mutators in sequence
    public class ComboMutator : EHMutator
    {
        readonly EHMutator _m1;
        readonly EHMutator _m2;

        public ComboMutator(EHMutator m1, EHMutator m2)
        {
            _m1 = m1;
            _m2 = m2;
        }

        public override string Name => _m1.Name + " then " + _m2.Name;

        public override SyntaxNode Mutate(SyntaxNode node)
        {
            return _m2.Mutate(_m1.Mutate(node));
        }
    }

    // Repeatedly apply a mutator
    public class RepeatMutator : EHMutator
    {
        readonly EHMutator _m;
        readonly int _n;
        public RepeatMutator(EHMutator m, int n)
        {
            _m = m;
            _n = n;
        }

        public override string Name => "(" + _m.Name + ") repeated " + _n + " times";

        public override SyntaxNode Mutate(SyntaxNode node)
        {
            SyntaxNode result = node;

            for (int i = 0; i < _n; i++)
            {
                result = _m.Mutate(result);
            }

            return result;
        }
    }
}
