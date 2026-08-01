namespace FsHarness.Tests

open System
open System.IO
open FsHarness.Cli
open FsHarness.Core
open Xunit

module ConfigTests =
    let private writeConfig schemaVersion comparison retries seeds =
        let path =
            Path.Combine(Path.GetTempPath(), $"fsharness-config-{Guid.NewGuid():N}.json")

        File.WriteAllText(
            path,
            $$"""
            {
              "schemaVersion": {{schemaVersion}},
              "sourcePath": "C:\\fixture",
              "baseCommit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
              "objective": "Improve",
              "editablePaths": ["src/**"],
              "seedPatches": {{seeds}},
              "evaluator": {
                "executable": "evaluate.exe",
                "arguments": [],
                "workingDirectory": ".",
                "timeoutSeconds": 30,
                "requiredConstraints": ["tests"],
                "maxInconclusiveRetries": {{retries}}
              },
              "metric": {
                "name": "candidate_speed_index",
                "direction": "maximize",
                "minDelta": 2{{comparison}}
              },
              "model": { "id": "gpt-5.6-luna", "reasoningEffort": "max" },
              "budgets": {
                "maxExperiments": 3,
                "maxRawTokens": 50000,
                "maxDurationSeconds": 7200,
                "codexTimeoutSeconds": 1200,
                "maxConsecutiveNonImprovements": 3,
                "maxConsecutiveFailures": 3
              },
              "promotionMode": "auto"
            }
            """
        )

        path

    [<Fact>]
    let ``schema v1 defaults to retained score and no seeds`` () =
        let path = writeConfig 1 "" 0 "[]"

        try
            match ConfigFile.read path with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok config ->
                Assert.Equal(RetainedScore, config.Metric.Comparison)
                Assert.Empty config.SeedPatches
        finally
            File.Delete path

    [<Fact>]
    let ``schema v2 reads paired metric retries and protected seeds`` () =
        let path =
            writeConfig
                2
                ", \"comparison\": { \"evaluationMetric\": \"frontier_speed_index\" }"
                2
                "[\".fsharness/seeds/seed.patch\"]"

        try
            match ConfigFile.read path with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok config ->
                Assert.Equal(EvaluationMetric "frontier_speed_index", config.Metric.Comparison)
                Assert.Equal(2, config.Evaluator.MaxInconclusiveRetries)
                Assert.Equal<string list>([ ".fsharness/seeds/seed.patch" ], config.SeedPatches)
        finally
            File.Delete path
