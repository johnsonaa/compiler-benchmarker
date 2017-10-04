/* Run the compilation benchmark */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;

namespace CompilerBenchmarker
{
	static class StringExtensions
	{
		public static string Join(this IEnumerable<string> s, string delimiter)
		{
			return string.Join(delimiter, s);
		}
	}

	class CompilerExeComparer : IComparer<Compiler>, IEqualityComparer<Compiler>
	{
		public int Compare(Compiler left, Compiler right)
		{
			var sc = StringComparer.OrdinalIgnoreCase;
			var exeCompared = sc.Compare(left.Exe, right.Exe);
			if (exeCompared == 0)
			{
				var leftArgs = string.Join("", left.Arguments);
				var rightArgs = string.Join("", right.Arguments);
				return sc.Compare(leftArgs, rightArgs);
			}

			return exeCompared;
		}

		public bool Equals(Compiler left, Compiler right)
		{
			return Compare(left, right) == 0;
		}

		public int GetHashCode(Compiler c)
		{
			return new[] { c.Language, c.Exe }
				.Concat(c.Arguments)
				.Aggregate(4021, (prev, next) => prev ^ next.GetHashCode());
		}
	}

	struct Compiler
	{
		// The name of the language, e.g. "C"
		public string Language { get; set; }
		// The extension of the files in this language, e.g. "c"
		public string Extension { get; set; }
		// The compiler executable, e.g. "gcc"
		public string Exe { get; set; }
		// The arguments to the compiler executabe, e.g. "-O"
		public string[] Arguments { get; set; }

		public Compiler(string language, string extension, string exe, params string[] arguments)
		{
			if (string.IsNullOrWhiteSpace(language))
				throw new ArgumentNullException(nameof(language));
			if (string.IsNullOrWhiteSpace(extension))
				throw new ArgumentNullException(nameof(extension));
			if (string.IsNullOrWhiteSpace(exe))
				throw new ArgumentNullException(nameof(exe));

			Language = language;
			Extension = extension;
			Exe = exe;
			Arguments = arguments ?? new string[] {};
		}

		public override string ToString()
		{
			if (Arguments.Length == 0)
				return $"{Language} ({Exe})";

			var args = string.Join(" ", Arguments);
			return $"{Language} ({Exe} {args})";
		}
	}

	struct CompilerBenchmark
	{
		public Compiler Compiler;
		public TimeSpan TimeToCompile;
		public bool Compiled;
		public int NumberFunctions;
		public string SecondsToCompile => Compiled ? TimeToCompile.TotalSeconds.ToString() : "";

		public static CompilerBenchmark Success(Compiler compiler, TimeSpan timeToCompile, int numberFunctions)
		{
			if (timeToCompile == TimeSpan.Zero)
				throw new ArgumentException("Compiling cannot take zero seconds", nameof(timeToCompile));
			return new CompilerBenchmark(compiler, timeToCompile, numberFunctions, true);
		}

		public static CompilerBenchmark Failure(Compiler compiler, int numberFunctions)
		{
			return new CompilerBenchmark(compiler, TimeSpan.Zero, numberFunctions, false);
		}

		private CompilerBenchmark(Compiler compiler, TimeSpan timeToCompile, int numberFunctions, bool compiled)
		{
			if (numberFunctions < 0)
				throw new ArgumentException("Cannot compile zero functions", nameof(numberFunctions));

			Compiler = compiler;
			TimeToCompile = timeToCompile;
			NumberFunctions = numberFunctions;
			Compiled = compiled;
		}
	}

	static class Benchmarker
	{
		static TimeSpan? RunBenchmark(Compiler compiler, string codeFilePath, int numFun)
		{
			var watch = new Stopwatch();
			watch.Start();
			var args = string.Join(" ", compiler.Arguments.Concat(new[] { codeFilePath }));
			Console.WriteLine($"  - Running with {numFun}: {compiler.Exe} {args}");
			var p = Process.Start(compiler.Exe, args);
			// todo: pass in compiler timeout option
			p.WaitForExit();
			if (p.ExitCode != 0)
			{
				watch.Stop();
				Console.WriteLine($"  ! Compilation failed for '{compiler.Exe} {args}'");
				Thread.Sleep(2500);
				return null;
			}

			// todo: also track memory consumption
			watch.Stop();
			Console.WriteLine($"  - Took {watch.Elapsed}");
			Console.WriteLine();
			return watch.Elapsed;
		}

		static IEnumerable<CompilerBenchmark> RunBenchmarks(
			List<Compiler> compilers, int numberAtStart, int numberOfSteps, int increaseOnStep)
		{
			var codeGen = new CodeGen();
			foreach (var langCompilers in compilers.GroupBy(x => x.Language))
			{
				Console.WriteLine($"Benchmarking {langCompilers.Key}:");
				for (int numFun = numberAtStart, step = 1;
					step <= numberOfSteps;
					step += 1, numFun += increaseOnStep)
				{
					// generate file
					Console.Write($"- Generating {langCompilers.Key} with {numFun} functions.. ");
					// todo: option to force-make already made files
		            var codeFilePath = $"test_{numFun}.{langCompilers.First().Extension}";
		            if (File.Exists(codeFilePath))
		            {
		            	Console.Write("Exists already.");
		            }
		            else
		            {
						codeGen.WriteLang(langCompilers.Key, numFun, codeFilePath);
		            }
	            	Console.WriteLine();

					foreach (var compiler in langCompilers)
					{
						// run benchmark
						// todo: if compiler fails at a certain number of functions, do not run it for the next round
						var bench = RunBenchmark(compiler, codeFilePath, numFun);
						yield return bench.HasValue
							? CompilerBenchmark.Success(compiler, bench.Value, numFun)
							: CompilerBenchmark.Failure(compiler, numFun);
					}

					// todo: pass in file cleanup options
				}
			}
		}

		static void WriteResults(
			IEnumerable<Compiler> compilersUsed,
			IEnumerable<CompilerBenchmark> marks,
			string resultFileName)
		{
			var compilerComp = new CompilerExeComparer();

			// [Number of Functions -> { Compiler -> Benchmark }]
			var rowData = marks
				.GroupBy(x => x.NumberFunctions)
				.Select(x => new {
					N = x.Key,
					M = x.ToDictionary(y => y.Compiler, compilerComp)
				});

			var first = rowData.First();
			var header = new List<string> { "Number Functions" }
				.Concat(first.M.Select(x => x.Key.ToString()))
				.Join(", ");

			var rows = rowData
				.Select(x => new List<string> { x.N.ToString() }
					.Concat(x.M.Select(y => y.Value.SecondsToCompile))
					.Join(", ")
				);

			var filetext = string.Join("\n", new List<string> { header }.Concat(rows));
			File.WriteAllText(resultFileName, filetext);
			Console.WriteLine($"Wrote benchmark results to {Path.GetFullPath(resultFileName)}");
		}

		static void Main(string[] args)
		{
			int numberAtStart = 20;
			int numberOfSteps = 1;
			int stepIncreaseNumber = 5000;

			// todo: good command-line options library for C#?
			var helpInfo = new Dictionary<string, int>
			{
				{ "numberAtStart", numberAtStart },
				{ "numberOfSteps", numberOfSteps },
				{ "increaseOnStep", stepIncreaseNumber }
				// todo:
				// cleanup on/off (on by default)
				// result file name, + show results to stdout to be on by default
				// csv file name (timestamped default)
				// put results in timestamped CSV file (on by default)
			};

			var compilers = new List<Compiler>
			{
				// native section
				// new Compiler("C", "c", "gcc", "-O2"),
				// new Compiler("C++", "cpp", "g++", "-O2"),
				// new Compiler("C++", "cpp", "clang", "-O2"),
				// new Compiler("Go", "go", "go", "build"),
				// new Compiler("Rust", "rs", "rustc", "-C", "opt-level=2"),
				// new Compiler("D", "d", "dmd", "-O"),
				// new Compiler("D", "d", "gdc", "-O"),
				// new Compiler("D", "d", "ldc2", "-O"),
				// new Compiler("Haskell", "hs", "ghc", "-O"),
				// new Compiler("OCaml", "ml", "ocamlopt", "-O2"),

				// // VM section
				new Compiler("CSharp", "cs", "csc", "/o"),
				new Compiler("FSharp", "fs", "fsharpc", "-O"),
				// new Compiler("Java", "java", "javac", "-J-Xmx4096M", "-J-Xms64M"),
				new Compiler("Scala", "scala", "scalac", "-optimise"), // modified to use Java -Xmx4096M -Xms64M -Xss4m
				new Compiler("Scala", "scala", "dotc", "-optimise"), // modified to use Java -Xmx4096M -Xss4m
				new Compiler("Kotlin", "kt", "kotlinc"), // modified to use Java -Xmx4096M -Xms64M -Xss4m
			};
			// todo: verify compilers exist on system
			// todo: write hardware/software report (compiler version, OS, Kernel, CPU, Memory, HD)
			// todo: duplicate compiler detection
			// todo: Ctrl+C writes results so far
			// todo: other keys to skip/abort language/number functions?
			// todo: compiler timeout feature?
			// todo: total timeout feature?

			try
			{
				// todo: pass in this option, enforce it is in bin at least if inside
				var home = Environment.GetEnvironmentVariable("HOME");
				var write_to = $"{home}/testfiles";
				if (!Directory.Exists(write_to))
                	Directory.CreateDirectory(write_to);

            	Directory.SetCurrentDirectory(write_to);

            	var baseFileName = $"results_{DateTime.Now.ToString("yyyyMMddTHHmmss")}";
            	var ongoingResultsFileName = $"{baseFileName}_ongoing.csv";
				var finalResultFileName = $"{baseFileName}_final.csv";
				if (File.Exists(ongoingResultsFileName))
					File.Delete(ongoingResultsFileName);
				if (File.Exists(finalResultFileName))
					File.Delete(finalResultFileName);

				var allBenchmarks = new List<CompilerBenchmark>(compilers.Count * 100);
				using (var ongoing = File.AppendText(ongoingResultsFileName))
				{
					ongoing.WriteLine($"Compiler, Number Functions, Time");

					var benchmarks = RunBenchmarks(compilers, numberAtStart, numberOfSteps, stepIncreaseNumber);
					foreach (var b in benchmarks)
					{
						allBenchmarks.Add(b);
						ongoing.WriteLine($"{b.Compiler.ToString()}, {b.NumberFunctions}, {b.SecondsToCompile}");
					}
				}

				WriteResults(compilers, allBenchmarks, finalResultFileName);
			}
			catch (Exception e)
			{
				Console.WriteLine(e.Message);
			}
		}
	}
}