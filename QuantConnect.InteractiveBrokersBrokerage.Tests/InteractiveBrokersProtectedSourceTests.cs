/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersProtectedSourceTests
    {
        private const string LeanRepositoryName = "Lean";
        private const string IbRepositoryName = "Lean.Brokerages.InteractiveBrokers-update";
        private const string LeanBaseline = "0136529cd8d9194f401aa5322bf90e547d1f0b56";
        private const string IbBaseline = "d0c9b8ad36b285d79294d53e93172c04d36c4c94";
        private const string IbBrokeragePath =
            "QuantConnect.InteractiveBrokersBrokerage/InteractiveBrokersBrokerage.cs";
        private const string IbBaselineSha256 =
            "cc58838a62d260fd9c8ec81bed15964c57ac5e6211da1c5c079b1aea0a1f9ab3";

        private static readonly (string Path, string Sha256)[] LeanProtectedFiles =
        {
            ("Engine/AlgorithmManager.cs", "ddcf6a1fef56245632253ac1d98967007120c367c1d2901cdd0b7fc7d21d3dcd"),
            ("Engine/TransactionHandlers/BrokerageTransactionHandler.cs", "93420a304ba75c82f9dc73b477f2b03cb554af21f0386be37a4d4cf1cf547ffc"),
            ("Engine/Storage/LocalObjectStore.cs", "5572b81b189ab6900ede966b9b79bffbd181b8c235e21715fc63aab96418b5ef"),
            // Brokerage.cs is in Lean/Brokerages, not the non-existent Lean/Common/Brokerages path.
            ("Brokerages/Brokerage.cs", "6609d22ca48eabb949c242ca814e13b40e6bc573177b5b9d413faf6071f20e0f")
        };

        // Inclusive line ranges in the immutable d0c9b8a baseline.
        private static readonly MethodRange[] FrozenMethods =
        {
            new MethodRange("GetOpenOrders", 560, 571),
            new MethodRange("GetOpenOrdersInternal", 573, 668),
            new MethodRange("GetOpenOrderContract", 670, 710),
            new MethodRange("Connect", 850, 1056),
            new MethodRange("Disconnect", 1285, 1320),
            new MethodRange("Dispose", 1325, 1352),
            new MethodRange("IBPlaceOrder", 1536, 1667),
            new MethodRange("HandleOrderStatusUpdates", 2305, 2391),
            new MethodRange("HandleOpenOrder", 2414, 2455),
            new MethodRange("TryGetLeanOrder", 2488, 2506),
            new MethodRange("HandleOpenOrderEnd", 2532, 2535),
            new MethodRange("HandleExecutionDetails", 2544, 2610),
            new MethodRange("HandleCommissionReport", 2618, 2657),
            new MethodRange("GetOrder", 2659, 2693),
            new MethodRange("EmitOrderFill", 2758, 2826)
        };

        private static readonly MethodRange PlaceOrder = new MethodRange("PlaceOrder", 436, 459);
        private static readonly MethodRange UpdateOrder = new MethodRange("UpdateOrder", 466, 493);
        private static readonly Regex HunkHeader = new Regex(
            @"^@@ -(?<oldStart>\d+)(?:,(?<oldCount>\d+))? \+(?<newStart>\d+)(?:,(?<newCount>\d+))? @@",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex DirectValidationCall = new Regex(
            @"^\s*(?<target>[A-Za-z_]\w*(?:(?:\?\.|\.)[A-Za-z_]\w*)*)\s*\(\s*order\s*\)\s*;\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        [Test]
        public void ProtectedOrderPathRemainsUpstreamTest()
        {
            var repositories = FindRepositories();

            foreach (var protectedFile in LeanProtectedFiles)
            {
                AssertRawFileMatchesBaseline(
                    repositories.Lean,
                    LeanBaseline,
                    protectedFile.Path,
                    protectedFile.Sha256);
            }

            var baseline = ReadGitBlob(repositories.InteractiveBrokers, IbBaseline, IbBrokeragePath);
            Assert.AreEqual(
                IbBaselineSha256,
                ComputeSha256(baseline),
                "The pinned InteractiveBrokersBrokerage.cs baseline did not have the audited hash.");

            var brokeragePath = Path.Combine(
                repositories.InteractiveBrokers,
                IbBrokeragePath.Replace('/', Path.DirectorySeparatorChar));
            var hunks = GetRawDiffHunks(repositories.InteractiveBrokers, baseline, brokeragePath);

            foreach (var frozenMethod in FrozenMethods)
            {
                foreach (var hunk in hunks)
                {
                    Assert.IsFalse(
                        hunk.Touches(frozenMethod),
                        $"{frozenMethod.Name} must remain byte-identical to {IbBaseline}; changed hunk: {hunk.Header}");
                }
            }

            var placeOrderChanges = GetChangesWithin(hunks, PlaceOrder);
            var updateOrderChanges = GetChangesWithin(hunks, UpdateOrder);
            AssertPermittedValidationCalls(placeOrderChanges, updateOrderChanges);
        }

        private static void AssertRawFileMatchesBaseline(
            string repository,
            string baselineCommit,
            string relativePath,
            string expectedBaselineHash)
        {
            var baseline = ReadGitBlob(repository, baselineCommit, relativePath);
            var currentPath = Path.Combine(repository, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var current = File.ReadAllBytes(currentPath);
            var baselineHash = ComputeSha256(baseline);
            var currentHash = ComputeSha256(current);

            Assert.AreEqual(
                expectedBaselineHash,
                baselineHash,
                $"The pinned {relativePath} baseline did not have the audited hash.");
            Assert.IsTrue(
                baseline.AsSpan().SequenceEqual(current),
                $"{relativePath} must remain raw-byte identical to {baselineCommit}. " +
                $"Expected SHA-256 {baselineHash}, found {currentHash}.");
        }

        private static List<DiffHunk> GetRawDiffHunks(
            string repository,
            byte[] baseline,
            string currentPath)
        {
            var temporaryPath = Path.Combine(
                Path.GetTempPath(),
                $"lean-fa-protected-{Guid.NewGuid():N}.cs");
            try
            {
                File.WriteAllBytes(temporaryPath, baseline);
                var result = RunGit(
                    repository,
                    "-c", "diff.algorithm=myers",
                    "-c", "diff.indentHeuristic=false",
                    "diff",
                    "--no-index",
                    "--no-ext-diff",
                    "--no-textconv",
                    "--no-color",
                    "--no-prefix",
                    "--unified=0",
                    "--text",
                    "--",
                    temporaryPath,
                    currentPath);

                Assert.IsTrue(
                    result.ExitCode == 0 || result.ExitCode == 1,
                    $"Unable to compare protected IB source. git exited {result.ExitCode}: {result.Error}");
                return ParseHunks(Encoding.UTF8.GetString(result.Output));
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }

        private static List<DiffHunk> ParseHunks(string patch)
        {
            var hunks = new List<DiffHunk>();
            DiffHunk current = null;
            foreach (var line in patch.Split('\n'))
            {
                var match = HunkHeader.Match(line);
                if (match.Success)
                {
                    current = new DiffHunk(
                        line.TrimEnd('\r'),
                        ParseNumber(match, "oldStart"),
                        ParseCount(match, "oldCount"),
                        ParseNumber(match, "newStart"),
                        ParseCount(match, "newCount"));
                    hunks.Add(current);
                    continue;
                }

                if (current == null)
                {
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                {
                    current.AddedLines.Add(line.Substring(1).TrimEnd('\r'));
                }
                else if (line.StartsWith("-", StringComparison.Ordinal))
                {
                    current.RemovedLines.Add(line.Substring(1).TrimEnd('\r'));
                }
            }
            return hunks;
        }

        private static List<DiffHunk> GetChangesWithin(
            IEnumerable<DiffHunk> hunks,
            MethodRange method)
        {
            var result = new List<DiffHunk>();
            foreach (var hunk in hunks)
            {
                if (hunk.Touches(method))
                {
                    result.Add(hunk);
                }
            }
            return result;
        }

        private static void AssertPermittedValidationCalls(
            List<DiffHunk> placeOrderChanges,
            List<DiffHunk> updateOrderChanges)
        {
            if (placeOrderChanges.Count == 0 && updateOrderChanges.Count == 0)
            {
                return;
            }

            Assert.AreEqual(
                1,
                placeOrderChanges.Count,
                "PlaceOrder may contain only one added validation-call statement.");
            Assert.AreEqual(
                1,
                updateOrderChanges.Count,
                "UpdateOrder may contain only one added validation-call statement.");

            var placeTarget = AssertSingleValidationCall(placeOrderChanges[0], PlaceOrder);
            var updateTarget = AssertSingleValidationCall(updateOrderChanges[0], UpdateOrder);
            Assert.AreEqual(
                placeTarget,
                updateTarget,
                "PlaceOrder and UpdateOrder must call the same validation method.");
        }

        private static string AssertSingleValidationCall(DiffHunk hunk, MethodRange method)
        {
            Assert.AreEqual(
                0,
                hunk.OldCount,
                $"{method.Name} may add one validation call but may not replace or delete upstream bytes.");
            Assert.AreEqual(
                0,
                hunk.RemovedLines.Count,
                $"{method.Name} may not remove upstream bytes.");
            Assert.AreEqual(
                1,
                hunk.AddedLines.Count,
                $"{method.Name} may add exactly one single-line validation-call statement.");

            var match = DirectValidationCall.Match(hunk.AddedLines[0]);
            Assert.IsTrue(
                match.Success,
                $"{method.Name} addition must be a direct call taking only 'order': {hunk.AddedLines[0]}");
            var target = match.Groups["target"].Value;
            var separator = target.LastIndexOf('.');
            var methodName = separator < 0 ? target : target.Substring(separator + 1);
            Assert.IsTrue(
                methodName.StartsWith("Validate", StringComparison.Ordinal),
                $"{method.Name} may only add a call to a validation method, found '{target}'.");
            return target;
        }

        private static byte[] ReadGitBlob(string repository, string commit, string relativePath)
        {
            var result = RunGit(repository, "show", $"{commit}:{relativePath}");
            Assert.AreEqual(
                0,
                result.ExitCode,
                $"Unable to read {commit}:{relativePath}. git error: {result.Error}");
            return result.Output;
        }

        private static GitResult RunGit(string workingDirectory, params string[] arguments)
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--no-replace-objects");
            startInfo.ArgumentList.Add("--no-pager");
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = Process.Start(startInfo);
            using var output = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(output);
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            return new GitResult(process.ExitCode, output.ToArray(), error);
        }

        private static Repositories FindRepositories()
        {
            var seeds = new[]
            {
                TestContext.CurrentContext.TestDirectory,
                AppContext.BaseDirectory,
                Directory.GetCurrentDirectory()
            };
            foreach (var seed in seeds)
            {
                var directory = new DirectoryInfo(Path.GetFullPath(seed));
                while (directory != null)
                {
                    if (TryFindRepositories(directory, out var repositories))
                    {
                        return repositories;
                    }
                    directory = directory.Parent;
                }
            }

            Assert.Fail(
                $"Could not locate sibling {LeanRepositoryName} and {IbRepositoryName} repositories " +
                "from the test directory or current working directory.");
            return null;
        }

        private static bool TryFindRepositories(DirectoryInfo directory, out Repositories repositories)
        {
            var ibRepository = directory.FullName;
            if (!File.Exists(Path.Combine(
                    ibRepository,
                    IbBrokeragePath.Replace('/', Path.DirectorySeparatorChar))))
            {
                ibRepository = Path.Combine(directory.FullName, IbRepositoryName);
            }

            var parent = Directory.GetParent(ibRepository);
            var leanRepository = parent == null
                ? string.Empty
                : Path.Combine(parent.FullName, LeanRepositoryName);
            if (File.Exists(Path.Combine(
                    ibRepository,
                    IbBrokeragePath.Replace('/', Path.DirectorySeparatorChar))) &&
                File.Exists(Path.Combine(leanRepository, "Engine", "AlgorithmManager.cs")))
            {
                repositories = new Repositories(leanRepository, ibRepository);
                return true;
            }

            repositories = null;
            return false;
        }

        private static int ParseNumber(Match match, string groupName)
        {
            return int.Parse(match.Groups[groupName].Value);
        }

        private static int ParseCount(Match match, string groupName)
        {
            var value = match.Groups[groupName].Value;
            return value.Length == 0 ? 1 : int.Parse(value);
        }

        private static string ComputeSha256(byte[] value)
        {
            return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
        }

        private sealed class MethodRange
        {
            public string Name { get; }
            public int Start { get; }
            public int End { get; }

            public MethodRange(string name, int start, int end)
            {
                Name = name;
                Start = start;
                End = end;
            }
        }

        private sealed class DiffHunk
        {
            public string Header { get; }
            public int OldStart { get; }
            public int OldCount { get; }
            public int NewStart { get; }
            public int NewCount { get; }
            public List<string> AddedLines { get; } = new List<string>();
            public List<string> RemovedLines { get; } = new List<string>();

            public DiffHunk(string header, int oldStart, int oldCount, int newStart, int newCount)
            {
                Header = header;
                OldStart = oldStart;
                OldCount = oldCount;
                NewStart = newStart;
                NewCount = newCount;
            }

            public bool Touches(MethodRange method)
            {
                if (OldCount == 0)
                {
                    // A zero-count old hunk inserts after OldStart.
                    return OldStart >= method.Start && OldStart < method.End;
                }

                var oldEnd = OldStart + OldCount - 1;
                return OldStart <= method.End && oldEnd >= method.Start;
            }
        }

        private sealed class GitResult
        {
            public int ExitCode { get; }
            public byte[] Output { get; }
            public string Error { get; }

            public GitResult(int exitCode, byte[] output, string error)
            {
                ExitCode = exitCode;
                Output = output;
                Error = error;
            }
        }

        private sealed class Repositories
        {
            public string Lean { get; }
            public string InteractiveBrokers { get; }

            public Repositories(string lean, string interactiveBrokers)
            {
                Lean = lean;
                InteractiveBrokers = interactiveBrokers;
            }
        }
    }
}
