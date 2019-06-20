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
using System.Threading;
using System.Xml;

// TODO: 
// * Use workspaces and parse csproj instead?
// * Fix formatting after we mess with code.
// * Try random stuff from https://github.com/dotnet/roslyn-sdk/tree/master/samples/CSharp/TreeTransforms
// * Rethink how TransformationCount is computed
//
// See http://roslynquoter.azurewebsites.net/ for tool that shows how use roslyn APIs for C# syntax.
// Useful if you can express what you want in C# and need to see how to get a transform to create it for you.

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using static MutateTest.OptionHolder;

namespace MutateTest
{
    class OptionHolder
    {
        public string InputFile { get; set; }
        public bool EhStress { get; set; }
        public bool StructStress { get; set; }
        public bool ShowResults { get; set; }
        public bool Recursive { get; set; }
        public bool Quiet { get; set; }
        public bool Verbose { get; set; }
        public int Seed { get; set; }

        public static OptionHolder Options { get; set; }
    }

    public class MutateTestException : Exception
    {

    }

    class Program
    {
        static DateTime startTime;
        static EHMutator[] stressors;
        static int stressorCount;
        static int totalVariantCount = 0;
        static int variantCount = 0;

        private static readonly CSharpCompilationOptions DebugOptions =
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, concurrentBuild: false, optimizationLevel: OptimizationLevel.Debug).WithAllowUnsafe(true);

        private static readonly CSharpCompilationOptions ReleaseOptions =
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, concurrentBuild: false, optimizationLevel: OptimizationLevel.Release).WithAllowUnsafe(true);

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
            inputFile.Name = "InputFile";
            inputFile.Description = "Input test case file or directory (for --recursive)";
            rootCommand.AddArgument(inputFile);

            Option ehStressOption = new Option("--ehStress", "add EH to methods", new Argument<bool>());
            rootCommand.AddOption(ehStressOption);

            Option structStressOption = new Option("--structStress", "replace locals with structs", new Argument<bool>());
            rootCommand.AddOption(structStressOption);

            Option showResultsOption = new Option("--showResults", "print modified programs to stdout", new Argument<bool>());
            rootCommand.AddOption(showResultsOption);

            Option quietOption = new Option("--quiet", "display minimal output: only failures", new Argument<bool>());
            rootCommand.AddOption(quietOption);

            Option verboseOption = new Option("--verbose", "describe each transformation", new Argument<bool>());
            rootCommand.AddOption(verboseOption);

            Option recursiveOption = new Option("--recursive", "process each file recursively", new Argument<bool>());
            rootCommand.AddOption(recursiveOption);

            Option seedOption = new Option("--seed", "random seed", new Argument<int>(42));
            rootCommand.AddOption(seedOption);

            rootCommand.Handler = CommandHandler.Create<OptionHolder>((options) =>
            {
                Options = options;
                return InnerMain();
            });

            return rootCommand.InvokeAsync(args).Result;
        }

        static int InnerMain()
        {
            startTime = DateTime.Now;

            if (Options.EhStress)
            {
                Random random = new Random(Options.Seed);

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

                // Random stressors
                EHMutator tryCatchRandom = new RandomMutator(tryCatch, random, 0.25);
                EHMutator tryEmtpyFinallyRandom = new RandomMutator(tryEmptyFinally, random, 0.25);
                EHMutator emptyTryFinallyRandom = new RandomMutator(emptyTryFinally, random, 0.25);
                EHMutator moveToCatchRandom = new RandomMutator(moveToCatch, random, 0.25);

                // Alternative stressors
                EHMutator either12 = new RandomChoiceMutator(tryCatch, tryEmptyFinally, random, 0.5);
                EHMutator either34 = new RandomChoiceMutator(moveToCatch, tryEmptyFinally, random, 0.5);

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
                EHMutator combo3412x2 = new RepeatMutator(combo3412, 2);

                // More
                EHMutator complex1 = new ComboMutator(combo1x2, combo4x2);
                EHMutator complex2 = new RepeatMutator(complex1, 2);
                EHMutator complex3 = new ComboMutator(combo1234x2, combo3412x2);

                stressors = new EHMutator[] {
                    tryCatch, tryCatchx2,
                    tryEmptyFinally, tryEmtpyFinallyx2,
                    emptyTryFinally, emptyTryFinallyx2,
                    moveToCatch, moveToCatchx2,
                    tryCatchRandom, tryEmtpyFinallyRandom, emptyTryFinallyRandom, moveToCatchRandom,
                    either12, either34,
                    combo1, combo2, combo3, combo4,
                    combo12, combo34, combo1234, combo3412,
                    combo1x2, combo4x2, combo1234x2, combo3412x2,
                    complex1, complex2, complex3
                };

                stressorCount = stressors.Length;
            }

            if (Options.Recursive)
            {
                if (!Options.Quiet)
                {
                    Console.WriteLine("** Directory Mode **");
                }

                //if (!Directory.Exists(options.InputFile))
                //{
                //    Console.WriteLine($"Can't access directory '{options.InputFile}'");
                //    return -1;
                //}

                var inputFiles = Directory.EnumerateFiles(Options.InputFile, "*", SearchOption.AllDirectories)
                                    .Where(s => (s.EndsWith(".cs")));

                int totalFileCount = inputFiles.Count();
                totalVariantCount = totalFileCount * (stressorCount + 1); // +1 to count the baseline run
                Console.WriteLine($"// File count: {totalFileCount}, stressor count: {stressorCount}, total variant count: {totalVariantCount}");

                int total = 0;
                int failed = 0;
                int succeeded = 0;

                foreach (var subInputFile in inputFiles)
                {
                    total++;

                    int result = MutateOneTest(subInputFile);

                    if (result == 100)
                    {
                        succeeded++;
                    }
                    else
                    {
                        failed++;
                    }
                }

                TimeSpan elapsedTime = DateTime.Now - startTime;
                Console.WriteLine($"Final Results: {total} files, {variantCount} variants, {succeeded} succeeded files, {failed} failed files, total time: {elapsedTime}, time/test: {elapsedTime.TotalSeconds / variantCount:F2}");

                if (failed == 0)
                {
                    return 100;
                }
                else
                {
                    return -1;
                }
            }
            else
            {
                int result = MutateOneTest(Options.InputFile);
                return result;
            }
        }

        static int MutateOneTest(string testFile)
        {
            if (!Options.Quiet)
            {
                Console.WriteLine("---------------------------------------");
                Console.WriteLine("// Original Program");
            }

            // Access input and build parse tree
            if (!File.Exists(testFile))
            {
                Console.WriteLine($"Can't access '{testFile}'");
                totalVariantCount -= stressorCount; // Don't count this one.
                return -1;
            }

            string inputText = File.ReadAllText(testFile);
            SyntaxTree inputTree = CSharpSyntaxTree.ParseText(inputText,
                    path: testFile,
                    options: ParseOptions);

            int inputResult = CompileAndExecute(isBaseline: true, inputTree, testFile);

            if (inputResult != 100)
            {
                totalVariantCount -= stressorCount; // Don't count this one.
                return inputResult;
            }

            int result = 100; // assume success

            // Ok, we have a compile and runnable test case. Now, mess with it....
            if (Options.EhStress)
            {
                int variantNumber = 0;

                foreach (var stressor in stressors)
                {
                    int stressResult = ApplyStress(testFile, variantNumber++, stressorCount, stressor, inputTree);

                    if (stressResult != 100 && result != 100)
                    {
                        // Only save the first non-success value, but continue running all tests.
                        result = stressResult;
                    }
                }
            }

            return result;
        }

        static int ApplyStress(string testFile, int variantNumber, int availableVariantCount, EHMutator m, SyntaxTree tree)
        {
            string shortTitle = $"{testFile}: EH Stress [{variantNumber}/{availableVariantCount}]";
            if (!Options.Quiet)
            {
                string title = $"// {shortTitle}: {m.Name}";
                Console.WriteLine();
                Console.WriteLine("---------------------------------------");
                Console.WriteLine(title);
            }
            SyntaxNode newRoot = m.Mutate(tree.GetRoot());
            if (!Options.Quiet)
            {
                Console.WriteLine($"// {shortTitle}: made {m.TransformCount} mutations");
            }

            if (Options.ShowResults)
            {
                Console.WriteLine(newRoot.ToFullString());
            }

            SyntaxTree newTree = SyntaxTree(newRoot, ParseOptions);

            int stressResult = CompileAndExecute(isBaseline: false, newTree, shortTitle);

            return stressResult;
        }

        static int CompileAndExecute(bool isBaseline, SyntaxTree tree, string name)
        {
            ++variantCount;

            //Console.WriteLine($"Compiling {name} with assembly references:");
            //foreach (var reference in References)
            //{
            //    Console.WriteLine($"{reference.Display}");
            //}

            SyntaxTree[] inputTrees = { tree };
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

                if (!Options.Quiet)
                {
                    Console.WriteLine($"// Compiled '{name}' successfully");
                }

                // TODO: redirect/capture stdout

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

                TimeSpan elapsedTime = DateTime.Now - startTime;
                double averageSecondsPerVariant = elapsedTime.TotalSeconds / variantCount;
                double progress = (double)variantCount / totalVariantCount;
                DateTime finishTime = DateTime.Now + TimeSpan.FromSeconds(averageSecondsPerVariant * (totalVariantCount - variantCount));
                Console.WriteLine($"// Total time: {elapsedTime}, seconds/test: {averageSecondsPerVariant:F2}, progress: {variantCount}/{totalVariantCount} ({progress:P}), estimated finish: {finishTime}");
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
    public abstract class EHMutator : CSharpSyntaxRewriter
    {
        public abstract string Name { get; }

        public int TransformCount { get; set; }

        protected void Announce(SyntaxNode node)
        {
            if (Options.Verbose)
            {
                var lineSpan = node.GetLocation().GetMappedLineSpan();
                Console.WriteLine($"// {Name} [{TransformCount}] @ lines {lineSpan.StartLinePosition.Line}-{lineSpan.EndLinePosition.Line}");
            }
            TransformCount++;
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
        protected static bool InvalidInFinally(SyntaxNode node)
        {
            return node.DescendantNodes(descendIntoTrivia: false).
                Any(x =>
                    (x.Kind() == SyntaxKind.ReturnStatement) ||
                     x.Kind() == SyntaxKind.ThrowStatement);
        }

        // More generally, things that can't be in finallys
        protected static bool InvalidInCatch(SyntaxNode node)
        {
            return node.DescendantNodes(descendIntoTrivia: false).
                Any(x =>
                    (x.Kind() == SyntaxKind.ImplicitStackAllocArrayCreationExpression) ||
                     x.Kind() == SyntaxKind.StackAllocArrayCreationExpression);
        }

        public virtual SyntaxNode Mutate(SyntaxNode node)
        {
            TransformCount = 0;
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
            if (InvalidInFinally(node)) return base.VisitBlock(node);

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
            if (InvalidInCatch(node)) return base.VisitBlock(node);

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
        protected readonly EHMutator _m1;
        protected readonly EHMutator _m2;

        public ComboMutator(EHMutator m1, EHMutator m2)
        {
            _m1 = m1;
            _m2 = m2;
        }

        public override string Name => $"({_m1.Name})+({_m2.Name})";

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            SyntaxNode result = _m1.VisitBlock(node);
            if (result != node)
            {
                TransformCount++;
            }

            if (result is BlockSyntax)
            {
                result = _m2.VisitBlock((BlockSyntax)node);
                if (result != node)
                {
                    TransformCount++;
                }
            }

            return result;
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

        public override string Name => $"({_m.Name})x{_n}";

        public override SyntaxNode Mutate(SyntaxNode node)
        {
            SyntaxNode result = node;
            TransformCount = 0;
            _m.TransformCount = 0;
            result = base.Mutate(result);
            TransformCount += _m.TransformCount;
            return result;
        }

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            SyntaxNode result = node;

            for (int i = 0; i < _n; i++)
            {
                if (result is BlockSyntax)
                {
                    var newResult = _m.VisitBlock((BlockSyntax)result);
                    if (newResult != result)
                    {
                        TransformCount++;
                    }
                    result = newResult;
                }
                else
                {
                    break;
                }
            }

            return result;
        }
    }

    // Randomly apply a mutator
    public class RandomMutator : EHMutator
    {
        readonly EHMutator _m;
        readonly Random _random;
        readonly double _p;

        public RandomMutator(EHMutator m, Random r, double p)
        {
            _m = m;
            _random = r;
            _p = p;
        }

        public override String Name => $"({_m.Name})|()@{_p:F2}";

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            double x = _random.NextDouble();

            if (x < _p)
            {
                if (Options.Verbose)
                {
                    Console.WriteLine($"// {Name}: random choose x={x:F2} < p={_p:F2}");
                }

                var result = _m.VisitBlock(node);

                if (result != node)
                {
                    TransformCount++;
                }

                return result;
            }
            else
            {
                if (Options.Verbose)
                {
                    Console.WriteLine($"// {Name}: random skip x={x:F2} >= p={_p:F2}");
                }

                return base.VisitBlock(node);
            }
        }
    }

    // Randomly choose between two mutators
    public class RandomChoiceMutator : ComboMutator
    {
        readonly Random _random;
        readonly double _p;

        public RandomChoiceMutator(EHMutator m1, EHMutator m2, Random r, double p) : base(m1, m2)
        {
            _random = r;
            _p = p;
        }

        public override string Name => $"({_m1.Name})|({_m2.Name})@{_p:F2}";

        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            double x = _random.NextDouble();
            SyntaxNode result = null;

            if (x < _p)
            {
                if (Options.Verbose)
                {
                    Console.WriteLine($"// {Name}: random choice x={x:F2} < p={_p:F2}: {_m1.Name}");
                }

                result = _m1.VisitBlock(node);
            }
            else
            {
                if (Options.Verbose)
                {
                    Console.WriteLine($"// {Name}: random choice x={x:F2} >= p={_p:F2}: {_m2.Name}");
                }
                result = _m2.VisitBlock(node);
            }

            if (result != node)
            {
                TransformCount++;
            }

            return node;
        }
    }
}
