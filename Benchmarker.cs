using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace CompilerBenchmarker
{
    static class Benchmarker
    {
        static TimingResult? CmdTimeBenchmark(Compiler compiler, string args)
        {
            var sout = new List<string>();
            using (var p = new Process())
            {
                // RESULT: %x %e %M means (exit code, elapsed time in seconds, max resident set size)
                p.StartInfo.FileName = "/usr/bin/time";
                p.StartInfo.Arguments = $@"-f ""RESULT: %x %e %M"" {compiler.Exe} {args}";

                p.StartInfo.UseShellExecute = false;
                p.StartInfo.RedirectStandardOutput = true;
                p.StartInfo.RedirectStandardError = true;
                p.StartInfo.ErrorDialog = false;

                Console.WriteLine($"\"{p.StartInfo.FileName} {p.StartInfo.Arguments}\"");
                foreach (string k in compiler.EnvironmentVariables.Keys)
                {
                    Console.Write($" and {k}=\"{compiler.EnvironmentVariables[k]}\"");
                    p.StartInfo.EnvironmentVariables[k] = compiler.EnvironmentVariables[k];
                }

                // The last line of the output will be from /usr/bin/time
                p.OutputDataReceived += (sender, outputLine) => {
                    if (outputLine.Data != null)
                        sout.Add(outputLine.Data);
                };
                p.ErrorDataReceived += (sender, errorLine) => {
                    if (errorLine.Data != null)
                        sout.Add(errorLine.Data);
                };

                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                p.WaitForExit();

                if (p.ExitCode != 0)
                {
                    Thread.Sleep(2500);
                    return null;
                }
            }

            for (var i = sout.Count - 1; i >= 0; --i)
            {
                var line = sout[i];
                if (!line.StartsWith("RESULT:"))
                    continue;

                var results = line.Split(' ');
                var exitCode = results[1];
                if (int.Parse(exitCode) != 0)
                {
                    Console.WriteLine($"  ! Compilation failed for '{compiler.Exe} {args}'");
                    Thread.Sleep(2500);
                    return null;
                }

                var elapsedSeconds = double.Parse(results[2]);
                var maxResidentSetSize = int.Parse(results[3]);
                var timing = new TimingResult(elapsedSeconds, maxResidentSetSize);
                Console.WriteLine($"  - Took {timing.Elapsed} MRSS {maxResidentSetSize}");
                return timing;
            }

            throw new Exception("Result of /usr/bin/time not found in output of {" + string.Join("\n", sout) + "}");
        }

        static TimingResult? StopwatchBenchmark(Compiler compiler, string args)
        {
            using (var p = new Process())
            {
                var watch = new Stopwatch();
                p.StartInfo.FileName = compiler.Exe;
                p.StartInfo.Arguments = args;

                Console.WriteLine($"\"{p.StartInfo.FileName} {p.StartInfo.Arguments}\"");
                foreach (string k in compiler.EnvironmentVariables.Keys)
                {
                    Console.Write($" and {k}=\"{compiler.EnvironmentVariables[k]}\"");
                    p.StartInfo.EnvironmentVariables[k] = compiler.EnvironmentVariables[k];
                }
                Console.WriteLine();

                watch.Start();
                p.Start();
                p.WaitForExit();
                watch.Stop();
                if (p.ExitCode != 0)
                {
                    Console.WriteLine($"  ! Compilation failed for '{compiler.Exe} {args}'");
                    Thread.Sleep(2500);
                    return null;
                }

                Console.WriteLine($"  - Took {watch.Elapsed}");
                Console.WriteLine();
                return new TimingResult(watch.Elapsed);
            }
        }

        static TimingResult? RunBenchmark(Compiler compiler, string codeFilePath, int numFun)
        {
            var isDotnet = compiler.Exe == "dotnet";
            if (isDotnet)
            {
                using (var p = Process.Start(compiler.Exe, $"restore CB.{compiler.Extension}proj"))
                {
                    p.WaitForExit();
                    if (p.ExitCode != 0)
                    {
                        Console.WriteLine($"  ! Compilation failed for '{compiler.Exe}'");
                        return null;
                    }
                }
            }

            var optArgs = compiler.OptimizeArguments;
            string args = isDotnet
                ? $"{compiler.MiscArguments} {optArgs} CB.{compiler.Extension}proj"
                : $"{compiler.MiscArguments} {optArgs} {codeFilePath}";

            Console.Write($"  - Running with {numFun}: ");
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                return CmdTimeBenchmark(compiler, args);
            else
                return StopwatchBenchmark(compiler, args);
        }

        static IEnumerable<CompilerBenchmark> RunBenchmarks(
            List<Compiler> compilers, int numberAtStart, int numberOfSteps, int increaseOnStep)
        {
            // Which files to not delete after compiling
            var doNotDelete = compilers
                .Select(x => $@"\d\.{x.Extension}$")
                .Distinct()
                .Join("|") + @"|\.csv$|\.txt$";
            var codeGen = new CodeGen();

            // Going from low to high, if X functions fails, so will X+1 so skip it
            var failed = new HashSet<Compiler>(new CompilerCliComparer());
            var requireDotnetProjFile = compilers.Any(x => x.Exe == "dotnet");

            foreach (var langCompilers in compilers.GroupBy(x => x.Language))
            {
                Console.WriteLine($"Benchmarking {langCompilers.Key}:");

                for (int numFun = numberAtStart, step = 1;
                    step <= numberOfSteps;
                    step += 1, numFun += increaseOnStep)
                {
                    var ext = langCompilers.First().Extension;
                    var codeFilePath = $"test_{langCompilers.First().Extension}_{numFun}.{ext}";

                    // Sub test directory
                    var testdir = numFun.ToString();
                    if (!Directory.Exists(testdir))
                        Directory.CreateDirectory(testdir);

                    Directory.SetCurrentDirectory(testdir);
                    try
                    {
                        Console.Write($"- Generating {langCompilers.Key} with {numFun} functions");
                        codeGen.WriteLang(langCompilers.Key, numFun, codeFilePath);
                        Console.WriteLine();

                        foreach (var compiler in langCompilers)
                        {
                            // dotnet compiler requires project file
                            if (requireDotnetProjFile)
                            {
                                string csp = "CB.csproj", fsp = "CB.fsproj";
                                if (File.Exists(csp)) File.Delete(csp);
                                if (File.Exists(fsp)) File.Delete(fsp);
                                File.WriteAllText(csp, GetCsProj());
                                File.WriteAllText(fsp, GetFsProj(codeFilePath));
                            }

                            if (failed.Contains(compiler))
                            {
                                yield return CompilerBenchmark.Failure(compiler, numFun);
                                continue;
                            }

                            var bench = RunBenchmark(compiler, codeFilePath, numFun);
                            if (!bench.HasValue)
                                failed.Add(compiler);

                            yield return bench.HasValue
                                ? CompilerBenchmark.Success(compiler, bench.Value, numFun)
                                : CompilerBenchmark.Failure(compiler, numFun);

                            foreach (var file in
                                new DirectoryInfo(Directory.GetCurrentDirectory()).WalkFiles())
                            {
                                if (!Regex.IsMatch(file.FullName, doNotDelete))
                                    File.Delete(file.FullName);
                            }
                        }
                    }
                    finally
                    {
                        Directory.SetCurrentDirectory("..");
                    }
                }
            }
        }

        // Pivot final results nicely, one column per compiler, one row per function count
        static void WriteResults(
            IEnumerable<Compiler> compilersUsed,
            IEnumerable<CompilerBenchmark> marks,
            string resultFileName)
        {
            IEqualityComparer<Compiler> comparer = new CompilerCliComparer();

            // [Number of Functions -> { Compiler -> Benchmark }]
            var rowData = marks
                .GroupBy(x => x.NumberFunctions)
                .Select(x => new {
                    NumFuns = x.Key,
                    CompilersByCmd = x.ToDictionary(y => y.Compiler, comparer)
                });

            var first = rowData.First();

            var header = new List<string> { "Number Functions" }
                .Concat(first.CompilersByCmd.Select(kv => kv.Key.ToString()))
                .Join(", ");

            var rows = rowData
                .Select(x => new List<string> { x.NumFuns.ToString() }
                    .Concat(x.CompilersByCmd.Select(kv => kv.Value.SecondsToCompile))
                    .Join(", ")
                );

            var filetext = string.Join("\n", new List<string> { header }.Concat(rows));
            File.WriteAllText(resultFileName, filetext);
            Console.WriteLine($"Wrote benchmark results to {Path.GetFullPath(resultFileName)}");
        }

        static void StartBench(int numberAtStart, int numberOfSteps, int stepIncreaseNumber)
        {
            var compilers = new List<Compiler>
            {
                new Compiler("C",         "c",      "gcc", "--version"),        // default
                new Compiler("C",         "c",      "gcc", "--version", "-O2"), // optimized
                new Compiler("C",         "c",    "clang", "--version"),
                new Compiler("C",         "c",    "clang", "--version", "-O2"),
                new Compiler("C++",     "cpp",      "g++", "--version"),
                new Compiler("C++",     "cpp",      "g++", "--version", "-O2"),
                new Compiler("C++",     "cpp",  "clang++", "--version"),
                new Compiler("C++",     "cpp",  "clang++", "--version", "-O2"),
                new Compiler("D",         "d",      "dmd", "--version"),
                new Compiler("D",         "d",      "dmd", "--version", "-O"),
                new Compiler("D",         "d",     "ldc2", "--version"),
                new Compiler("D",         "d",     "ldc2", "--version", "-O"),
                new Compiler("Go",       "go",       "go",   "version", "build"),
                new Compiler("Rust",     "rs",    "rustc", "--version"),
                // new Compiler("Rust",     "rs",    "rustc", "--version", "-C opt-level=2"),
                new Compiler("Swift", "swift",   "swiftc", "--version"),
                new Compiler("Swift", "swift",   "swiftc", "--version", "-O"),
                new Compiler("OCaml",    "ml", "ocamlopt", "--version"),
                new Compiler("OCaml",    "ml", "ocamlopt", "--version", "-O2"),
                new Compiler("Haskell",  "hs",    "stack", "ghc -- --version", miscArguments: "ghc --"),
                new Compiler("Haskell",  "hs",    "stack", "ghc -- --version", "-O2", miscArguments: "ghc --"),

                new Compiler("Java",   "java",    "javac", "-version",
                    miscArguments: "-J-Xmx4096M -J-Xms64M"),
                new Compiler("Scala", "scala",   "scalac", "-version",
                    envVars: new Dictionary<string,string> { ["JAVA_OPTS"] = "-Xmx4096M -Xms64M" }),
                new Compiler("Scala", "scala",     "dotc", "-version",
                    envVars: new Dictionary<string,string> { ["JAVA_OPTS"] = "-Xmx4096M -Xms64M" }),
                new Compiler("Scala", "scala",   "scalac", "-version", "-opt:l:inline -opt-inline-from:**",
                    envVars: new Dictionary<string,string> { ["JAVA_OPTS"] = "-Xmx4096M -Xms64M" }),
                new Compiler("Kotlin",   "kt",  "kotlinc", "-version",
                    envVars: new Dictionary<string,string> { ["JAVA_OPTS"] = "-Xmx4096M -Xms64M" }),

                new Compiler("CSharp",   "cs",   "dotnet", "--version",
                    miscArguments: "build --no-restore"),
                new Compiler("CSharp",   "cs",   "dotnet", "--version", "-c release",
                    miscArguments: "build --no-restore"),
                new Compiler("FSharp",   "fs",   "dotnet", "--version",
                    miscArguments: "build --no-restore"),
                new Compiler("FSharp",   "fs",   "dotnet", "--version", "-c release",
                    miscArguments: "build --no-restore"),

                // new Compiler("Nim",     "nim",      "nim", "--version", "compile"),
                // new Compiler("Nim",     "nim",      "nim", "--version", "compile -d:release --opt:speed"),
                // new Compiler("Crystal",  "cr",  "crystal", "--version", "build"),
                // new Compiler("Crystal",  "cr",  "crystal", "--version", "build --release"),
            };

            foreach (var c in compilers.GroupBy(x => x.Exe).Select(x => x.First()))
            {
                Console.WriteLine($"Found compiler: {c.Exe} ::: {c.Version}");
            }

            Console.WriteLine("\n");

            // todo: control keys to skip/abort language/number functions while running
            // todo: timeouts for compiling

            // Create and step into 'testfiles' directory
            var home = Environment.GetEnvironmentVariable("HOME");
            var write_to = $"{home}/testfiles";
            if (!Directory.Exists(write_to))
                Directory.CreateDirectory(write_to);
            Directory.SetCurrentDirectory(write_to);

            // Record system information if it's not there
            var systemInfoFileName = $"{DateTime.Now.ToString("yyyyMMdd")}_systemInfo.txt";
            if (!File.Exists(systemInfoFileName))
            {
                var info = BasicSystemInfo.Find();
                var infoText = new[] { info.OS, info.CPU, info.Memory }.Join("\n\n");
                File.WriteAllText(systemInfoFileName, infoText);
            }

            // Delete any existing results (not an issue if this date format is used, but if not)
            var baseFileName = $"results_{DateTime.Now.ToString("yyyy-MM-dd HHmm")}";
            var ongoingResultsFileName = $"{baseFileName}_ongoing.csv";
            var finalResultFileName = $"{baseFileName}_final.csv";
            if (File.Exists(ongoingResultsFileName))
                File.Delete(ongoingResultsFileName);
            if (File.Exists(finalResultFileName))
                File.Delete(finalResultFileName);

            // Collect benchmarks while writing ongoing benchmarks
            var benchmarks = new List<CompilerBenchmark>();
            using (var ongoing = File.AppendText(ongoingResultsFileName))
            {
                // Write header for ongoing results
                ongoing.WriteLine($"Compiler, Number Functions, Time (seconds), Memory (KB)");

                // Run
                var lazyBenchmarks = RunBenchmarks(
                    compilers, numberAtStart, numberOfSteps, stepIncreaseNumber);
                foreach (var b in lazyBenchmarks)
                {
                    benchmarks.Add(b);
                    ongoing.WriteLine(
                        $"{b.Compiler.ToString()}, {b.NumberFunctions}, {b.SecondsToCompile}, {b.MaxMemory}");
                }
            }

            WriteResults(compilers, benchmarks, finalResultFileName);
        }

        static void Main(string[] args)
        {
            // int numberAtStart = 5;
            // int numberOfSteps = 1;
            // int stepIncreaseNumber = 0;
            int numberAtStart = 5000;
            int numberOfSteps = 5;
            int stepIncreaseNumber = 5000;

            try
            {
                StartBench(numberAtStart, numberOfSteps, stepIncreaseNumber);
            }
            catch (Exception e)
            {
                Console.WriteLine(e.ToString());
            }
        }

        static string GetCsProj() =>
            @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><OutputType>Exe</OutputType>" +
            @"<TargetFramework>netcoreapp2.1</TargetFramework></PropertyGroup></Project>";

        static string GetFsProj(string file) =>
            @"<Project Sdk=""Microsoft.NET.Sdk""><PropertyGroup><OutputType>Exe</OutputType>" +
            @"<TargetFramework>netcoreapp2.1</TargetFramework></PropertyGroup>" +
            @"<ItemGroup><Compile Include=""$FILE"" /></ItemGroup></Project>"
            .Replace("$FILE", file);
    }
}
