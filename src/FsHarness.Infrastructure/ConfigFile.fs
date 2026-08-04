namespace FsHarness.Infrastructure

open System
open System.IO
open System.Text.Json
open FsHarness.Core

[<RequireQualifiedAccess>]
module ConfigFile =
    let private tryProperty (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, value -> Some value
        | false, _ -> None

    let private requiredString (name: string) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.ValueKind = JsonValueKind.String -> value.GetString()
        | _ -> raise (InvalidDataException $"Missing string property '{name}'.")

    let private optionalString (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value ->
            if value.ValueKind = JsonValueKind.String then
                value.GetString() |> Option.ofObj
            else
                None)

    let private requiredInt (name: string) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.TryGetInt32() |> fst -> value.GetInt32()
        | _ -> raise (InvalidDataException $"Missing integer property '{name}'.")

    let private requiredInt64 (name: string) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.TryGetInt64() |> fst -> value.GetInt64()
        | _ -> raise (InvalidDataException $"Missing integer property '{name}'.")

    let private requiredDecimal (name: string) (element: JsonElement) =
        match tryProperty name element with
        | Some value when value.TryGetDecimal() |> fst -> value.GetDecimal()
        | _ -> raise (InvalidDataException $"Missing decimal property '{name}'.")

    let private optionalDecimal (name: string) (element: JsonElement) =
        tryProperty name element
        |> Option.bind (fun value ->
            match value.TryGetDecimal() with
            | true, parsed -> Some parsed
            | false, _ -> None)

    let private stringList (name: string) (element: JsonElement) =
        match tryProperty name element with
        | None -> []
        | Some values when values.ValueKind = JsonValueKind.Array ->
            values.EnumerateArray()
            |> Seq.map (fun value -> value.GetString())
            |> List.ofSeq
        | _ -> raise (InvalidDataException $"Property '{name}' must be an array of strings.")

    let private parseDirection (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "maximize" -> Maximize
        | "minimize" -> Minimize
        | _ -> raise (InvalidDataException $"Unknown metric direction '{value}'.")

    let private directionText direction =
        match direction with
        | Maximize -> "maximize"
        | Minimize -> "minimize"

    let private parseEffort (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "low" -> ReasoningEffort.Low
        | "medium" -> ReasoningEffort.Medium
        | "high" -> ReasoningEffort.High
        | "xhigh" -> ReasoningEffort.XHigh
        | "max" -> ReasoningEffort.Max
        | _ -> raise (InvalidDataException $"Unknown reasoning effort '{value}'.")

    let private effortText effort =
        match effort with
        | ReasoningEffort.Low -> "low"
        | ReasoningEffort.Medium -> "medium"
        | ReasoningEffort.High -> "high"
        | ReasoningEffort.XHigh -> "xhigh"
        | ReasoningEffort.Max -> "max"

    let private parsePromotion (value: string) =
        match value.Trim().ToLowerInvariant() with
        | "auto"
        | "autowhenstrictlybetter" -> AutoWhenStrictlyBetter
        | "review"
        | "reviewstrictwinners" -> ReviewStrictWinners
        | _ -> raise (InvalidDataException $"Unknown promotion mode '{value}'.")

    let private promotionText promotion =
        match promotion with
        | AutoWhenStrictlyBetter -> "auto"
        | ReviewStrictWinners -> "review"

    let private parseComparison schemaVersion (metric: JsonElement) =
        match tryProperty "comparison" metric with
        | None when schemaVersion = 1 -> RetainedScore
        | None -> raise (InvalidDataException "Schema v2 requires metric.comparison.")
        | Some value when value.ValueKind = JsonValueKind.String ->
            match value.GetString().Trim().ToLowerInvariant() with
            | "retainedscore"
            | "retained_score" -> RetainedScore
            | other -> raise (InvalidDataException $"Unknown metric comparison '{other}'.")
        | Some value when value.ValueKind = JsonValueKind.Object ->
            EvaluationMetric(requiredString "evaluationMetric" value)
        | _ -> raise (InvalidDataException "metric.comparison must be a string or object.")

    let private comparisonValue comparison : obj =
        match comparison with
        | RetainedScore -> box "retainedScore"
        | EvaluationMetric name -> box {| evaluationMetric = name |}

    let private parsePromptProfile (root: JsonElement) =
        match tryProperty "promptProfile" root with
        | None -> Defaults.promptProfile
        | Some profile when profile.ValueKind = JsonValueKind.Object ->
            { MaxMemoryCount = requiredInt "maxMemoryCount" profile
              MaxMemoryCharacters = requiredInt "maxMemoryCharacters" profile
              MaxEvaluationFindings = requiredInt "maxEvaluationFindings" profile
              MaxEvaluationCharacters = requiredInt "maxEvaluationCharacters" profile }
        | _ -> raise (InvalidDataException "promptProfile must be an object.")

    let read (path: string) =
        try
            use document = JsonDocument.Parse(File.ReadAllText path)
            let root = document.RootElement

            let schemaVersion =
                tryProperty "schemaVersion" root
                |> Option.map _.GetInt32()
                |> Option.defaultValue 1

            let evaluator = root.GetProperty "evaluator"
            let metric = root.GetProperty "metric"
            let model = root.GetProperty "model"
            let budgets = root.GetProperty "budgets"

            let config =
                { SchemaVersion = schemaVersion
                  SourcePath = requiredString "sourcePath" root |> Path.GetFullPath
                  BaseCommit = requiredString "baseCommit" root |> CommitOid.create
                  Objective = requiredString "objective" root
                  EditablePaths = stringList "editablePaths" root
                  SeedPatches = stringList "seedPatches" root
                  Evaluator =
                    { Executable = requiredString "executable" evaluator
                      Arguments = stringList "arguments" evaluator
                      WorkingDirectory = optionalString "workingDirectory" evaluator |> Option.defaultValue "."
                      Timeout = TimeSpan.FromSeconds(float (requiredInt "timeoutSeconds" evaluator))
                      RequiredConstraints = stringList "requiredConstraints" evaluator
                      MaxInconclusiveRetries =
                        tryProperty "maxInconclusiveRetries" evaluator
                        |> Option.map _.GetInt32()
                        |> Option.defaultValue 0 }
                  Metric =
                    { Name = requiredString "name" metric
                      Direction = requiredString "direction" metric |> parseDirection
                      MinDelta = requiredDecimal "minDelta" metric
                      Target = optionalDecimal "target" metric
                      Comparison = parseComparison schemaVersion metric }
                  Model =
                    { Id = requiredString "id" model
                      Effort = requiredString "reasoningEffort" model |> parseEffort }
                  PromptProfile = parsePromptProfile root
                  Budgets =
                    { MaxExperiments = requiredInt "maxExperiments" budgets
                      MaxRawTokens = requiredInt64 "maxRawTokens" budgets
                      MaxDuration = TimeSpan.FromSeconds(float (requiredInt "maxDurationSeconds" budgets))
                      CodexTimeout = TimeSpan.FromSeconds(float (requiredInt "codexTimeoutSeconds" budgets))
                      MaxConsecutiveNonImprovements = requiredInt "maxConsecutiveNonImprovements" budgets
                      MaxConsecutiveFailures = requiredInt "maxConsecutiveFailures" budgets }
                  PromotionMode =
                    optionalString "promotionMode" root
                    |> Option.defaultValue "auto"
                    |> parsePromotion }

            HarnessConfig.validate config
        with error ->
            Error [ error.Message ]

    let write (path: string) (config: HarnessConfig) =
        match HarnessConfig.validate config with
        | Error errors -> Error errors
        | Ok validated ->
            try
                let target =
                    validated.Metric.Target
                    |> Option.map Nullable
                    |> Option.defaultValue (Nullable())

                let document =
                    {| schemaVersion = HarnessConfig.currentSchemaVersion
                       sourcePath = validated.SourcePath
                       baseCommit = CommitOid.value validated.BaseCommit
                       objective = validated.Objective
                       editablePaths = List.toArray validated.EditablePaths
                       seedPatches = List.toArray validated.SeedPatches
                       evaluator =
                        {| executable = validated.Evaluator.Executable
                           arguments = List.toArray validated.Evaluator.Arguments
                           workingDirectory = validated.Evaluator.WorkingDirectory
                           timeoutSeconds = int validated.Evaluator.Timeout.TotalSeconds
                           requiredConstraints = List.toArray validated.Evaluator.RequiredConstraints
                           maxInconclusiveRetries = validated.Evaluator.MaxInconclusiveRetries |}
                       metric =
                        {| name = validated.Metric.Name
                           direction = directionText validated.Metric.Direction
                           minDelta = validated.Metric.MinDelta
                           target = target
                           comparison = comparisonValue validated.Metric.Comparison |}
                       model =
                        {| id = validated.Model.Id
                           reasoningEffort = effortText validated.Model.Effort |}
                       promptProfile =
                        {| maxMemoryCount = validated.PromptProfile.MaxMemoryCount
                           maxMemoryCharacters = validated.PromptProfile.MaxMemoryCharacters
                           maxEvaluationFindings = validated.PromptProfile.MaxEvaluationFindings
                           maxEvaluationCharacters = validated.PromptProfile.MaxEvaluationCharacters |}
                       budgets =
                        {| maxExperiments = validated.Budgets.MaxExperiments
                           maxRawTokens = validated.Budgets.MaxRawTokens
                           maxDurationSeconds = int validated.Budgets.MaxDuration.TotalSeconds
                           codexTimeoutSeconds = int validated.Budgets.CodexTimeout.TotalSeconds
                           maxConsecutiveNonImprovements = validated.Budgets.MaxConsecutiveNonImprovements
                           maxConsecutiveFailures = validated.Budgets.MaxConsecutiveFailures |}
                       promotionMode = promotionText validated.PromotionMode |}

                let options = JsonSerializerOptions(WriteIndented = true)
                let destination = Path.GetFullPath path
                AtomicFile.writeAllText destination (JsonSerializer.Serialize(document, options))
                Ok destination
            with error ->
                Error [ error.Message ]
