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
            if value.ValueKind = JsonValueKind.Number then
                match value.TryGetDecimal() with
                | true, parsed -> Some parsed
                | false, _ -> None
            else
                None)

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

    let private parseGraphSearch (root: JsonElement) =
        match tryProperty "graphSearch" root with
        | None -> Defaults.graphSearch
        | Some graph when graph.ValueKind = JsonValueKind.Object ->
            let intValue name fallback =
                tryProperty name graph
                |> Option.map _.GetInt32()
                |> Option.defaultValue fallback

            let decimalValue name fallback =
                tryProperty name graph
                |> Option.map _.GetDecimal()
                |> Option.defaultValue fallback

            { InitialFanOut = intValue "initialFanOut" Defaults.graphSearch.InitialFanOut
              BeamWidth = intValue "beamWidth" Defaults.graphSearch.BeamWidth
              ExpansionsPerHeadPerRound =
                intValue "expansionsPerHeadPerRound" Defaults.graphSearch.ExpansionsPerHeadPerRound
              OrdinaryCandidatesPerSynthesis =
                intValue "ordinaryCandidatesPerSynthesis" Defaults.graphSearch.OrdinaryCandidatesPerSynthesis
              StagnationTrigger = intValue "stagnationTrigger" Defaults.graphSearch.StagnationTrigger
              MaxSynthesisBudgetFraction =
                decimalValue "maxSynthesisBudgetFraction" Defaults.graphSearch.MaxSynthesisBudgetFraction
              MaxSynthesisDepth = intValue "maxSynthesisDepth" Defaults.graphSearch.MaxSynthesisDepth
              ConflictResolutionAttempts =
                intValue "conflictResolutionAttempts" Defaults.graphSearch.ConflictResolutionAttempts
              MaxConflictFiles = intValue "maxConflictFiles" Defaults.graphSearch.MaxConflictFiles
              MaxConflictCharacters = intValue "maxConflictCharacters" Defaults.graphSearch.MaxConflictCharacters }
        | _ -> raise (InvalidDataException "graphSearch must be an object.")

    let parse (contents: string) =
        try
            use document = JsonDocument.Parse contents
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
                        |> Option.defaultValue 0
                      MaxInfrastructureRetries =
                        tryProperty "maxInfrastructureRetries" evaluator
                        |> Option.map _.GetInt32()
                        |> Option.defaultValue Defaults.evaluatorMaxInfrastructureRetries
                      InfrastructureRetryDelay =
                        tryProperty "infrastructureRetryDelaySeconds" evaluator
                        |> Option.map (fun value -> TimeSpan.FromSeconds(value.GetDouble()))
                        |> Option.defaultValue Defaults.evaluatorInfrastructureRetryDelay }
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
                  GraphSearch = parseGraphSearch root
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

    let read (path: string) =
        try
            File.ReadAllText path |> parse
        with error ->
            Error [ error.Message ]

    let readDataRoot (path: string) =
        try
            use document = JsonDocument.Parse(File.ReadAllText path)

            let value = optionalString "dataRoot" document.RootElement

            value
            |> Option.map (fun configured ->
                if Path.IsPathRooted configured then
                    Path.GetFullPath configured
                else
                    Path.GetFullPath(configured, Path.GetDirectoryName(Path.GetFullPath path)))
            |> Ok
        with error ->
            Error [ error.Message ]

    let writeWithDataRoot (dataRoot: string option) (path: string) (config: HarnessConfig) =
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
                       dataRoot = dataRoot |> Option.map Path.GetFullPath |> Option.toObj
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
                           maxInconclusiveRetries = validated.Evaluator.MaxInconclusiveRetries
                           maxInfrastructureRetries = validated.Evaluator.MaxInfrastructureRetries
                           infrastructureRetryDelaySeconds = validated.Evaluator.InfrastructureRetryDelay.TotalSeconds |}
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
                       graphSearch =
                        {| initialFanOut = validated.GraphSearch.InitialFanOut
                           beamWidth = validated.GraphSearch.BeamWidth
                           expansionsPerHeadPerRound = validated.GraphSearch.ExpansionsPerHeadPerRound
                           ordinaryCandidatesPerSynthesis = validated.GraphSearch.OrdinaryCandidatesPerSynthesis
                           stagnationTrigger = validated.GraphSearch.StagnationTrigger
                           maxSynthesisBudgetFraction = validated.GraphSearch.MaxSynthesisBudgetFraction
                           maxSynthesisDepth = validated.GraphSearch.MaxSynthesisDepth
                           conflictResolutionAttempts = validated.GraphSearch.ConflictResolutionAttempts
                           maxConflictFiles = validated.GraphSearch.MaxConflictFiles
                           maxConflictCharacters = validated.GraphSearch.MaxConflictCharacters |}
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

    let write (path: string) (config: HarnessConfig) = writeWithDataRoot None path config
