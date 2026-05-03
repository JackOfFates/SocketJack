using System;
using System.IO;

namespace SocketJack.Net.Database {

    /// <summary>
    /// Command-line tool for generating DbContext from database schema.
    /// Can be used as a pre-build event or MSBuild target.
    /// </summary>
    public static class DbContextGeneratorTool {

        public static int Main(string[] args) {
            Console.WriteLine("SocketJack DbContext Generator v1.0");
            Console.WriteLine();

            try {
                var options = ParseArguments(args);

                if (options.ShowHelp) {
                    ShowHelp();
                    return 0;
                }

                if (string.IsNullOrEmpty(options.ConnectionString)) {
                    Console.Error.WriteLine("Error: Connection string is required.");
                    Console.Error.WriteLine("Use --help for usage information.");
                    return 1;
                }

                // Auto-detect language if not specified
                if (options.Language == null && !string.IsNullOrEmpty(options.ProjectPath)) {
                    options.Language = DbContextGenerator.DetectLanguage(options.ProjectPath);
                    Console.WriteLine($"Detected language: {options.Language}");
                } else if (options.Language == null && !string.IsNullOrEmpty(options.OutputDirectory)) {
                    options.Language = DbContextGenerator.DetectLanguageFromDirectory(options.OutputDirectory);
                    Console.WriteLine($"Detected language from output directory: {options.Language}");
                }

                var generator = new DbContextGenerator {
                    ConnectionString = options.ConnectionString,
                    Namespace = options.Namespace ?? "Generated",
                    ContextName = options.ContextName ?? "AppDbContext",
                    OutputDirectory = options.OutputDirectory ?? ".",
                    Language = options.Language ?? CodeLanguage.CSharp,
                    GeneratePartialClasses = options.GeneratePartial,
                    IncludeNavigationProperties = options.IncludeNavigation
                };

                // Add excluded tables
                if (options.ExcludedTables != null) {
                    foreach (var table in options.ExcludedTables)
                        generator.ExcludedTables.Add(table);
                }

                // Progress reporting
                generator.Progress += (s, e) => Console.WriteLine($"  {e.Message}");

                Console.WriteLine($"Generating DbContext '{generator.ContextName}'...");
                Console.WriteLine($"  Output: {Path.GetFullPath(generator.OutputDirectory)}");
                Console.WriteLine($"  Language: {generator.Language}");
                Console.WriteLine();

                var result = generator.Generate();

                if (result.Success) {
                    Console.WriteLine();
                    Console.WriteLine($"Successfully generated {result.WrittenFiles.Count} files:");
                    foreach (var file in result.WrittenFiles)
                        Console.WriteLine($"  - {file}");
                    return 0;
                } else {
                    Console.Error.WriteLine($"Generation failed: {result.Error}");
                    if (result.Exception != null)
                        Console.Error.WriteLine(result.Exception.ToString());
                    return 1;
                }
            } catch (Exception ex) {
                Console.Error.WriteLine($"Error: {ex.Message}");
                return 1;
            }
        }

        private static Options ParseArguments(string[] args) {
            var options = new Options();

            for (int i = 0; i < args.Length; i++) {
                var arg = args[i].ToLowerInvariant();

                switch (arg) {
                    case "-h":
                    case "--help":
                        options.ShowHelp = true;
                        break;

                    case "-c":
                    case "--connection":
                        if (i + 1 < args.Length)
                            options.ConnectionString = args[++i];
                        break;

                    case "-n":
                    case "--namespace":
                        if (i + 1 < args.Length)
                            options.Namespace = args[++i];
                        break;

                    case "--context":
                        if (i + 1 < args.Length)
                            options.ContextName = args[++i];
                        break;

                    case "-o":
                    case "--output":
                        if (i + 1 < args.Length)
                            options.OutputDirectory = args[++i];
                        break;

                    case "-l":
                    case "--language":
                        if (i + 1 < args.Length) {
                            var lang = args[++i].ToLowerInvariant();
                            options.Language = lang switch {
                                "cs" or "csharp" or "c#" => CodeLanguage.CSharp,
                                "vb" or "visualbasic" or "vb.net" => CodeLanguage.VisualBasic,
                                _ => null
                            };
                        }
                        break;

                    case "-p":
                    case "--project":
                        if (i + 1 < args.Length)
                            options.ProjectPath = args[++i];
                        break;

                    case "--no-partial":
                        options.GeneratePartial = false;
                        break;

                    case "--no-navigation":
                        options.IncludeNavigation = false;
                        break;

                    case "-x":
                    case "--exclude":
                        if (i + 1 < args.Length) {
                            options.ExcludedTables = args[++i].Split(',', StringSplitOptions.RemoveEmptyEntries);
                        }
                        break;
                }
            }

            return options;
        }

        private static void ShowHelp() {
            Console.WriteLine("Usage: DbContextGenerator [options]");
            Console.WriteLine();
            Console.WriteLine("Options:");
            Console.WriteLine("  -c, --connection <string>   Database connection string (required)");
            Console.WriteLine("  -n, --namespace <name>      Namespace for generated classes (default: Generated)");
            Console.WriteLine("  --context <name>            DbContext class name (default: AppDbContext)");
            Console.WriteLine("  -o, --output <path>         Output directory (default: current directory)");
            Console.WriteLine("  -l, --language <lang>       Target language: cs, vb (auto-detected if not specified)");
            Console.WriteLine("  -p, --project <path>        Project file path for language detection");
            Console.WriteLine("  --no-partial                Don't generate partial classes");
            Console.WriteLine("  --no-navigation             Don't include navigation properties");
            Console.WriteLine("  -x, --exclude <tables>      Comma-separated list of tables to exclude");
            Console.WriteLine("  -h, --help                  Show this help message");
            Console.WriteLine();
            Console.WriteLine("Examples:");
            Console.WriteLine("  DbContextGenerator -c \"Server=localhost;Database=MyDb;...\" -o ./Generated");
            Console.WriteLine("  DbContextGenerator -c \"...\" --context MyDbContext --language vb");
            Console.WriteLine("  DbContextGenerator -c \"...\" -p ./MyProject.vbproj --exclude \"__EFMigrations,sysdiagrams\"");
            Console.WriteLine();
            Console.WriteLine("Pre-Build Event (Visual Studio):");
            Console.WriteLine("  \"$(SolutionDir)tools\\DbContextGenerator.exe\" -c \"$(ConnectionString)\" -o \"$(ProjectDir)Generated\" -p \"$(ProjectPath)\"");
        }

        private class Options {
            public bool ShowHelp { get; set; }
            public string ConnectionString { get; set; }
            public string Namespace { get; set; }
            public string ContextName { get; set; }
            public string OutputDirectory { get; set; }
            public CodeLanguage? Language { get; set; }
            public string ProjectPath { get; set; }
            public bool GeneratePartial { get; set; } = true;
            public bool IncludeNavigation { get; set; } = true;
            public string[] ExcludedTables { get; set; }
        }
    }
}
