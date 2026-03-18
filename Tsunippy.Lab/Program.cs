using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Tsunippy.Runtime.Controller;
using Tsunippy.Runtime.Corpus;
using Tsunippy.Runtime.Evaluation;
using Tsunippy.Runtime.Replay;
using Tsunippy.Runtime.Trace;

static int PrintUsage()
{
    Console.WriteLine("Tsunippy.Lab");
    Console.WriteLine("  selftest");
    Console.WriteLine("  synth <output-path> [sample|trust]");
    Console.WriteLine("  replay <trace-path> [captured|baseline|frontier|profile.json]");
    Console.WriteLine("  compare <trace-path> [left-profile] [right-profile]");
    Console.WriteLine("  regress <trace-path-or-directory> [captured|baseline|frontier|profile.json]");
    Console.WriteLine("  corpus scaffold <corpus-dir>");
    Console.WriteLine("  corpus list <corpus-dir-or-manifest>");
    Console.WriteLine("  corpus validate <corpus-dir-or-manifest> [captured|baseline|frontier|profile.json]");
    Console.WriteLine("  corpus regress <corpus-dir-or-manifest> [captured|baseline|frontier|profile.json]");
    return 1;
}

static TimingControllerProfile ResolveProfile(TimingTraceDocument trace, string name)
{
    var overrideProfile = ResolveProfileOverride(name);
    return overrideProfile ?? trace.CapturedProfile?.Clone() ?? TimingControllerProfile.CreateFrontierDefault();
}

static TimingControllerProfile ResolveProfileOverride(string name)
{
    if (!string.IsNullOrWhiteSpace(name) && File.Exists(name))
        return TimingControllerProfileJson.Load(name);

    var normalized = string.IsNullOrWhiteSpace(name) ? "captured" : name.Trim().ToLowerInvariant();
    return normalized switch
    {
        "baseline" => TimingControllerProfile.CreateBaseline(),
        "frontier" or "current" => TimingControllerProfile.CreateFrontierDefault(),
        _ => null,
    };
}

static void PrintRun(TimingReplayRunResult result)
{
    Console.WriteLine($"Profile: {result.ProfileName}");
    Console.WriteLine($"Events: {result.EventCount}");
    Console.WriteLine($"Decisions: {result.DecisionCount}");
    Console.WriteLine($"Predictions: {result.PredictionCount}");
    Console.WriteLine($"Corrections: {result.CorrectionCount}");
    Console.WriteLine($"Avg |correction|: {result.AverageAbsoluteCorrectionMs:F2} ms");
    Console.WriteLine($"Avg disagreement: {result.AverageAbsoluteDisagreementMs:F2} ms");
    Console.WriteLine($"Avg reduction: {result.AverageReductionMs:F2} ms");
    Console.WriteLine($"Avg confidence: {result.AverageConfidence:P1}");
    Console.WriteLine($"Dry-run decisions: {result.DryRunCount}");
    Console.WriteLine($"Quarantine decisions: {result.QuarantineCount}");
    Console.WriteLine($"Fingerprint: {result.DecisionFingerprint}");
    Console.WriteLine("Top reasons:");
    foreach (var pair in result.ReasonCounts.OrderByDescending(pair => pair.Value).Take(6))
        Console.WriteLine($"  {pair.Key}: {pair.Value}");

    Console.WriteLine("Recent decisions:");
    foreach (var decision in result.Decisions.Skip(Math.Max(result.Decisions.Count - 5, 0)))
    {
        Console.WriteLine(
            $"  t+{decision.TimelineSeconds:F3}s | {decision.Source}/{decision.Reason} | {decision.ActionKind} {decision.ActionId} seq {decision.Sequence} | pred {decision.PredictedLock * 1000f:0} ms | final {decision.FinalLock * 1000f:0} ms | note {decision.Note}");
    }
}

static void PrintEquivalence(TimingReplayEquivalenceReport report)
{
    if (report == null)
    {
        Console.WriteLine("No observed-decision stream is embedded in this trace.");
        return;
    }

    Console.WriteLine($"Observed fingerprint: {report.ObservedFingerprint}");
    Console.WriteLine($"Replay fingerprint:   {report.ReplayFingerprint}");
    Console.WriteLine($"Equivalent: {report.IsEquivalent}");
    Console.WriteLine($"Matched decisions: {report.MatchedDecisionCount}/{Math.Max(report.ObservedDecisionCount, report.ReplayDecisionCount)}");

    if (report.Divergences.Count == 0)
        return;

    Console.WriteLine("Equivalence divergences:");
    foreach (var divergence in report.Divergences)
    {
        var observed = divergence.Observed.HasValue
            ? $"{divergence.Observed.Value.Reason} final {divergence.Observed.Value.FinalLock * 1000f:0} ms"
            : "missing";
        var replay = divergence.Replay.HasValue
            ? $"{divergence.Replay.Value.Reason} final {divergence.Replay.Value.FinalLock * 1000f:0} ms"
            : "missing";
        Console.WriteLine($"  [{divergence.Index}] {string.Join(',', divergence.Reasons)} | observed {observed} <> replay {replay}");
    }
}

static void PrintCorpusList(TimingTraceCorpusDocument corpus, string corpusRoot)
{
    Console.WriteLine($"{corpus.Name} ({corpus.CorpusId})");
    Console.WriteLine(corpus.Description);
    Console.WriteLine($"Root: {corpusRoot}");
    Console.WriteLine($"Entries: {corpus.Entries.Count}");

    foreach (var entry in corpus.Entries)
    {
        var tracePath = TimingTraceCorpusPaths.ResolveTracePath(corpusRoot, entry);
        var status = File.Exists(tracePath) ? "captured" : "missing";
        var expectation = entry.DivergenceExpected
            ? "divergence-expected"
            : entry.EquivalenceExpectation.ToString().ToLowerInvariant();
        Console.WriteLine($"- [{status}] {entry.TraceId} | {entry.ScenarioBucket} | {expectation}{(entry.IsGold ? " | gold" : string.Empty)}");
        Console.WriteLine($"  {entry.Name}");
        Console.WriteLine($"  Purpose: {entry.Purpose}");
        Console.WriteLine($"  Expected: {entry.ExpectedBehavior}");
        Console.WriteLine($"  Path: {Path.GetRelativePath(corpusRoot, tracePath)}");
    }
}

static void PrintCorpusValidation(TimingTraceCorpusValidationResult result)
{
    Console.WriteLine($"{result.Corpus.Name} ({result.Corpus.CorpusId})");
    Console.WriteLine($"Manifest: {result.ManifestPath}");
    Console.WriteLine($"Root: {result.CorpusRoot}");
    Console.WriteLine($"Entries: {result.Entries.Count} | Passed: {result.PassCount}/{result.Entries.Count} | Missing: {result.MissingTraceCount}");
    Console.WriteLine($"Gold traces: {result.GoldTraceCount} | Gold failures: {result.GoldFailureCount}");
    Console.WriteLine($"Overall pass: {result.Passed}");

    if (result.Warnings.Count > 0)
    {
        Console.WriteLine("Warnings:");
        foreach (var warning in result.Warnings)
            Console.WriteLine($"  {warning}");
    }

    Console.WriteLine("Trace results:");
    foreach (var entryResult in result.Entries)
    {
        var entry = entryResult.Entry;
        var expectation = entry.DivergenceExpected
            ? "divergence-expected"
            : entry.EquivalenceExpectation.ToString().ToLowerInvariant();
        var relativePath = Path.GetRelativePath(result.CorpusRoot, entryResult.TracePath);
        Console.WriteLine($"- {(entryResult.Passed ? "PASS" : "FAIL")} {entry.TraceId} | {entry.ScenarioBucket} | {expectation}{(entry.IsGold ? " | gold" : string.Empty)}");
        Console.WriteLine($"  {entry.Name}");
        Console.WriteLine($"  Path: {relativePath}");
        Console.WriteLine($"  Deterministic: {entryResult.Deterministic}");
        Console.WriteLine($"  Equivalence outcome: {entryResult.EquivalenceOutcome}");
        Console.WriteLine($"  Expectation satisfied: {entryResult.ExpectationSatisfied}");
        foreach (var note in entryResult.Notes)
            Console.WriteLine($"  Note: {note}");
    }
}

static TimingTraceDocument LoadTrace(string path) => TimingTraceJson.Load(path);

static IEnumerable<string> EnumerateTracePaths(string pathOrDirectory)
{
    if (File.Exists(pathOrDirectory))
        return new[] { pathOrDirectory };

    if (Directory.Exists(pathOrDirectory))
        return Directory.EnumerateFiles(pathOrDirectory, "*.timing-trace.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal);

    return Array.Empty<string>();
}

static int RunRegression(string pathOrDirectory, string profileRef)
{
    var tracePaths = EnumerateTracePaths(pathOrDirectory).ToArray();
    if (tracePaths.Length == 0)
    {
        Console.WriteLine("No trace files found.");
        return 1;
    }

    var passCount = 0;
    foreach (var tracePath in tracePaths)
    {
        var trace = LoadTrace(tracePath);
        var profile = ResolveProfile(trace, profileRef);
        var first = TimingReplayRunner.Analyze(trace, profile);
        var second = TimingReplayRunner.Analyze(trace, profile);
        var deterministic = string.Equals(first.Replay.DecisionFingerprint, second.Replay.DecisionFingerprint, StringComparison.Ordinal);
        var equivalent = first.Equivalence?.IsEquivalent ?? true;
        var passed = deterministic && equivalent;
        if (passed)
            passCount++;

        Console.WriteLine($"{(passed ? "PASS" : "FAIL")} {Path.GetFileName(tracePath)}");
        Console.WriteLine($"  deterministic: {deterministic}");
        Console.WriteLine($"  observed equivalence: {equivalent}");
        if (first.Equivalence != null && first.Equivalence.Divergences.Count > 0)
            Console.WriteLine($"  first divergence: {string.Join(',', first.Equivalence.Divergences[0].Reasons)}");
    }

    Console.WriteLine($"Regression summary: {passCount}/{tracePaths.Length} passed");
    return passCount == tracePaths.Length ? 0 : 2;
}

static int RunCorpusScaffold(string corpusPathOrManifest)
{
    try
    {
        var manifestPath = TimingTraceCorpusFactory.ScaffoldRecommendedV1(corpusPathOrManifest);
        Console.WriteLine($"Scaffolded corpus manifest at {manifestPath}");
        return 0;
    }
    catch (Exception exception)
    {
        Console.WriteLine(exception.Message);
        return 2;
    }
}

static int RunCorpusList(string corpusPathOrManifest)
{
    var manifestPath = TimingTraceCorpusPaths.ResolveManifestPath(corpusPathOrManifest);
    if (!File.Exists(manifestPath))
    {
        Console.WriteLine($"Corpus manifest not found at {manifestPath}");
        return 1;
    }

    var corpus = TimingTraceCorpusJson.Load(manifestPath);
    PrintCorpusList(corpus, TimingTraceCorpusPaths.ResolveCorpusRoot(manifestPath));
    return 0;
}

static int RunCorpusValidation(string corpusPathOrManifest, string profileRef)
{
    try
    {
        var profile = ResolveProfileOverride(profileRef);
        var result = TimingTraceCorpusValidation.Validate(corpusPathOrManifest, profile);
        PrintCorpusValidation(result);
        if (!result.GoldPassed)
            return 3;
        return result.Passed ? 0 : 2;
    }
    catch (Exception exception)
    {
        Console.WriteLine(exception.Message);
        return 1;
    }
}

static void StampTraceForCorpusEntry(TimingTraceDocument trace, TimingTraceCorpusEntry entry)
{
    trace.Metadata.Label = entry.Name;
    trace.Metadata.CorpusTraceId = entry.TraceId;
    trace.Metadata.ScenarioBucket = entry.ScenarioBucket;
    trace.Metadata.Purpose = entry.Purpose;
    trace.Metadata.Tags = new List<string>(entry.Tags ?? Enumerable.Empty<string>());
    trace.Metadata.Source = "synthetic-corpus";
    trace.Metadata.Notes = entry.ExpectedBehavior;
}

static string CreateSyntheticCorpusFixture()
{
    var corpusRoot = Path.Combine(Path.GetTempPath(), $"tsunippy-corpus-{Guid.NewGuid():N}");
    var manifestPath = TimingTraceCorpusFactory.ScaffoldRecommendedV1(corpusRoot);
    var corpus = TimingTraceCorpusJson.Load(manifestPath);

    foreach (var entry in corpus.Entries)
    {
        var trace = entry.ScenarioBucket switch
        {
            TimingTraceScenarioBucket.ConflictRecovery => SyntheticTimingTraceFactory.CreateTrustworthinessSample(),
            _ when entry.TraceId.Contains("interrupt", StringComparison.OrdinalIgnoreCase) => SyntheticTimingTraceFactory.CreateTrustworthinessSample(),
            _ => SyntheticTimingTraceFactory.CreateSample(),
        };

        StampTraceForCorpusEntry(trace, entry);
        var tracePath = TimingTraceCorpusPaths.ResolveTracePath(corpusRoot, entry);
        TimingTraceJson.Save(tracePath, trace);
        entry.LastCapturedUtc = DateTime.UtcNow;
        entry.LastCapturedPluginVersion = "lab-selftest";
    }

    TimingTraceCorpusJson.Save(manifestPath, corpus);
    return corpusRoot;
}

var argsList = args;
if (argsList.Length == 0)
    return PrintUsage();

switch (argsList[0].ToLowerInvariant())
{
    case "selftest":
    {
        var sample = SyntheticTimingTraceFactory.CreateSample();
        var trust = SyntheticTimingTraceFactory.CreateTrustworthinessSample();

        var first = TimingReplayRunner.Analyze(sample);
        var second = TimingReplayRunner.Analyze(sample);
        var baseline = TimingReplayRunner.Analyze(sample, TimingControllerProfile.CreateBaseline());
        var comparison = TimingReplayEvaluator.Compare(first.Replay, baseline.Replay);
        var trustAnalysis = TimingReplayRunner.Analyze(trust);

        var tempProfilePath = Path.Combine(Path.GetTempPath(), "tsunippy-baseline-profile.json");
        TimingControllerProfileJson.Save(tempProfilePath, TimingControllerProfile.CreateBaseline());
        var loadedProfile = TimingControllerProfileJson.Load(tempProfilePath);

        var corpusTemplate = TimingTraceCorpusFactory.CreateRecommendedV1();
        var syntheticCorpusRoot = CreateSyntheticCorpusFixture();
        var corpusValidation = TimingTraceCorpusValidation.Validate(syntheticCorpusRoot);

        Console.WriteLine($"Deterministic fingerprint 1: {first.Replay.DecisionFingerprint}");
        Console.WriteLine($"Deterministic fingerprint 2: {second.Replay.DecisionFingerprint}");
        Console.WriteLine($"Deterministic: {string.Equals(first.Replay.DecisionFingerprint, second.Replay.DecisionFingerprint, StringComparison.Ordinal)}");
        Console.WriteLine($"External profile load: {loadedProfile.Name} / {loadedProfile.Strategy}");
        Console.WriteLine($"Recommended corpus template entries: {corpusTemplate.Entries.Count}");
        Console.WriteLine();
        PrintRun(first.Replay);
        Console.WriteLine();
        PrintEquivalence(first.Equivalence);
        Console.WriteLine();
        Console.WriteLine($"Baseline comparison: correction delta {comparison.CorrectionDeltaMs:F2} ms, disagreement delta {comparison.DisagreementDeltaMs:F2} ms, reduction delta {comparison.ReductionDeltaMs:F2} ms");
        foreach (var divergence in comparison.Divergences.Take(5))
        {
            var left = divergence.Left.HasValue ? $"{divergence.Left.Value.Reason} final {divergence.Left.Value.FinalLock * 1000f:0} ms" : "missing";
            var right = divergence.Right.HasValue ? $"{divergence.Right.Value.Reason} final {divergence.Right.Value.FinalLock * 1000f:0} ms" : "missing";
            Console.WriteLine($"  [{divergence.Index}] {left} <> {right}");
        }

        Console.WriteLine();
        Console.WriteLine("Trustworthiness trace:");
        PrintRun(trustAnalysis.Replay);
        PrintEquivalence(trustAnalysis.Equivalence);

        Console.WriteLine();
        Console.WriteLine("Synthetic corpus validation:");
        PrintCorpusValidation(corpusValidation);

        try
        {
            Directory.Delete(syntheticCorpusRoot, true);
        }
        catch
        {
            // Ignore temp cleanup failures in self-test mode.
        }

        return first.Equivalence?.IsEquivalent == true
               && trustAnalysis.Equivalence?.IsEquivalent == true
               && string.Equals(first.Replay.DecisionFingerprint, second.Replay.DecisionFingerprint, StringComparison.Ordinal)
               && corpusTemplate.Entries.Count == 12
               && corpusValidation.Passed
            ? 0
            : 2;
    }

    case "synth":
    {
        if (argsList.Length < 2)
            return PrintUsage();

        var outputPath = argsList[1];
        var variant = argsList.Length > 2 ? argsList[2] : "sample";
        var trace = variant.Equals("trust", StringComparison.OrdinalIgnoreCase)
            ? SyntheticTimingTraceFactory.CreateTrustworthinessSample()
            : SyntheticTimingTraceFactory.CreateSample();
        TimingTraceJson.Save(outputPath, trace);
        Console.WriteLine($"Wrote synthetic trace to {Path.GetFullPath(outputPath)}");
        return 0;
    }

    case "replay":
    {
        if (argsList.Length < 2)
            return PrintUsage();

        var trace = LoadTrace(argsList[1]);
        var profile = ResolveProfile(trace, argsList.Length > 2 ? argsList[2] : "captured");
        var analysis = TimingReplayRunner.Analyze(trace, profile);
        PrintRun(analysis.Replay);
        Console.WriteLine();
        PrintEquivalence(analysis.Equivalence);
        return analysis.Equivalence?.IsEquivalent == false ? 2 : 0;
    }

    case "compare":
    {
        if (argsList.Length < 2)
            return PrintUsage();

        var trace = LoadTrace(argsList[1]);
        var leftProfile = ResolveProfile(trace, argsList.Length > 2 ? argsList[2] : "captured");
        var rightProfile = ResolveProfile(trace, argsList.Length > 3 ? argsList[3] : "baseline");
        var left = TimingReplayRunner.Analyze(trace, leftProfile);
        var right = TimingReplayRunner.Analyze(trace, rightProfile);
        var comparison = TimingReplayEvaluator.Compare(left.Replay, right.Replay);

        PrintRun(left.Replay);
        Console.WriteLine();
        PrintEquivalence(left.Equivalence);
        Console.WriteLine();
        PrintRun(right.Replay);
        Console.WriteLine();
        PrintEquivalence(right.Equivalence);
        Console.WriteLine();
        Console.WriteLine($"Correction delta (right-left): {comparison.CorrectionDeltaMs:F2} ms");
        Console.WriteLine($"Disagreement delta (right-left): {comparison.DisagreementDeltaMs:F2} ms");
        Console.WriteLine($"Reduction delta (right-left): {comparison.ReductionDeltaMs:F2} ms");
        Console.WriteLine($"Prediction delta (right-left): {comparison.PredictionDelta}");
        Console.WriteLine($"Correction count delta (right-left): {comparison.CorrectionCountDelta}");
        Console.WriteLine("Divergences:");
        foreach (var divergence in comparison.Divergences)
        {
            var leftSummary = divergence.Left.HasValue
                ? $"{divergence.Left.Value.Reason} final {divergence.Left.Value.FinalLock * 1000f:0} ms"
                : "missing";
            var rightSummary = divergence.Right.HasValue
                ? $"{divergence.Right.Value.Reason} final {divergence.Right.Value.FinalLock * 1000f:0} ms"
                : "missing";
            Console.WriteLine($"  [{divergence.Index}] {leftSummary} <> {rightSummary}");
        }

        return 0;
    }

    case "regress":
    {
        if (argsList.Length < 2)
            return PrintUsage();

        return RunRegression(argsList[1], argsList.Length > 2 ? argsList[2] : "captured");
    }

    case "corpus":
    {
        if (argsList.Length < 3)
            return PrintUsage();

        var action = argsList[1].ToLowerInvariant();
        return action switch
        {
            "scaffold" => RunCorpusScaffold(argsList[2]),
            "list" => RunCorpusList(argsList[2]),
            "validate" => RunCorpusValidation(argsList[2], argsList.Length > 3 ? argsList[3] : "captured"),
            "regress" => RunCorpusValidation(argsList[2], argsList.Length > 3 ? argsList[3] : "captured"),
            _ => PrintUsage(),
        };
    }

    default:
        return PrintUsage();
}
