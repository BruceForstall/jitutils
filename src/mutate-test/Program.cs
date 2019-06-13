using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Emit;
using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Reflection;

// TODO: 
// Use workspaces and parse csproj instead?
// Fix formatting after we mess with code.
// Try random stuff from https://github.com/dotnet/roslyn-sdk/tree/master/samples/CSharp/TreeTransforms

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
                Console.WriteLine();
                Console.WriteLine("---------------------------------------");
                Console.WriteLine("// EH Stress");
                WrapBlocksInTryCatch stressor = new WrapBlocksInTryCatch();
                SyntaxNode newRoot = stressor.Visit(inputTree.GetRoot());

                if (options.ShowResults)
                {
                    Console.WriteLine(newRoot.ToFullString());
                }

                SyntaxTree newTree = SyntaxTree(newRoot, ParseOptions);

                int stressResult = CompileAndExecute(newTree, "EH Stress");

                if (stressResult != 100)
                {
                    return stressResult;
                }
            }
 
            return 100;
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

    // Rewrite any top-level block that is not enclosed in a try
    // as a try { <block> } catch (MutateTest.MutateTestException) { throw; }
    //
    // TODO: bottom-up rewrite?
    //
    // See http://roslynquoter.azurewebsites.net/ for tool that shows how use roslyn APIs for C# syntax.

    public class WrapBlocksInTryCatch : CSharpSyntaxRewriter
    {
        public override SyntaxNode VisitBlock(BlockSyntax node)
        {
            if (IsInTryBlock(node)) return base.VisitBlock(node);

            var lineSpan = node.GetLocation().GetMappedLineSpan();
            Console.WriteLine($"// Adding try block around lines {lineSpan.StartLinePosition.Line}-{lineSpan.EndLinePosition.Line}");

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

        private static bool IsInTryBlock(SyntaxNode initialNode)
        {
            SyntaxNode node = initialNode.Parent;
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
    }
}
