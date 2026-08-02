namespace FsHarness.Tests

open System
open System.IO
open FsHarness.Core
open FsHarness.Infrastructure
open Xunit

module ConfigTests =
    let private writeConfig schemaVersion comparison retries seeds promptProfile =
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
              {{promptProfile}}
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
        let path = writeConfig 1 "" 0 "[]" ""

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
                ""

        try
            match ConfigFile.read path with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok config ->
                Assert.Equal(EvaluationMetric "frontier_speed_index", config.Metric.Comparison)
                Assert.Equal(2, config.Evaluator.MaxInconclusiveRetries)
                Assert.Equal<string list>([ ".fsharness/seeds/seed.patch" ], config.SeedPatches)
        finally
            File.Delete path

    [<Fact>]
    let ``prompt profile reads compact memory and evaluator limits`` () =
        let path =
            writeConfig
                2
                ", \"comparison\": { \"evaluationMetric\": \"frontier_speed_index\" }"
                2
                "[]"
                "\"promptProfile\": { \"maxMemoryCount\": 2, \"maxMemoryCharacters\": 2000, \"maxEvaluationFindings\": 3, \"maxEvaluationCharacters\": 1000 },"

        try
            match ConfigFile.read path with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok config ->
                Assert.Equal(2, config.PromptProfile.MaxMemoryCount)
                Assert.Equal(2_000, config.PromptProfile.MaxMemoryCharacters)
                Assert.Equal(3, config.PromptProfile.MaxEvaluationFindings)
                Assert.Equal(1_000, config.PromptProfile.MaxEvaluationCharacters)
        finally
            File.Delete path

    [<Fact>]
    let ``schema v2 experiment round trips through shared config file`` () =
        let source =
            writeConfig
                2
                ", \"comparison\": { \"evaluationMetric\": \"frontier_speed_index\" }, \"target\": 110"
                2
                "[\".fsharness/seeds/seed.patch\"]"
                "\"promptProfile\": { \"maxMemoryCount\": 2, \"maxMemoryCharacters\": 2000, \"maxEvaluationFindings\": 3, \"maxEvaluationCharacters\": 1000 },"

        let destination =
            Path.Combine(Path.GetTempPath(), $"fsharness-config-roundtrip-{Guid.NewGuid():N}.json")

        try
            let expected =
                match ConfigFile.read source with
                | Ok config -> config
                | Error errors -> failwith (String.concat " " errors)

            match ConfigFile.write destination expected with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok written -> Assert.Equal(Path.GetFullPath destination, written)

            match ConfigFile.read destination with
            | Error errors -> Assert.Fail(String.concat " " errors)
            | Ok actual -> Assert.Equal(expected, actual)
        finally
            File.Delete source

            if File.Exists destination then
                File.Delete destination
