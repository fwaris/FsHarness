namespace FsHarness.Core

open System

type BenchmarkTaskKind =
    | LocalizedFix
    | CrossFileChange
    | AsyncProcessIntegration
    | FuncUiElmishBehavior

type BenchmarkTask =
    { Id: string
      Kind: BenchmarkTaskKind
      Objective: string
      EditablePaths: string list
      RequiredConstraints: string list }

type BenchmarkStrategy =
    | SingleTurn
    | ExternalRatchet of maxCandidates: int

type BenchmarkArm =
    { Id: string
      Model: ModelSpec
      Strategy: BenchmarkStrategy }

type BenchmarkPreset =
    { Tasks: BenchmarkTask list
      Arms: BenchmarkArm list
      NominalTokenBudgetPerEpisode: int64
      NominalDurationPerEpisode: TimeSpan
      DisclosedAggregateTokenBudget: int64 }

type BenchmarkEpisode =
    { TaskId: string
      ArmId: string
      QualityPassed: bool
      CriticalQualityOrSafetyDefect: bool
      RawTokens: int64 option }

type SmokeComparison =
    { SolvedBySol: int
      SolvedByRatchet: int
      SolBudgetedTokensToQuality: int64
      RatchetBudgetedTokensToQuality: int64 }

type SmokeAssessment =
    | Promising of SmokeComparison
    | NotYetPromising of SmokeComparison
    | Inconclusive of reason: string

[<RequireQualifiedAccess>]
module HypothesisBenchmark =
    [<Literal>]
    let SolArmId = "sol-medium-one-turn"

    [<Literal>]
    let LunaOneTurnArmId = "luna-max-one-turn"

    [<Literal>]
    let LunaRatchetArmId = "luna-max-ratchet-3"

    let preset =
        { Tasks =
            [ { Id = "localized-fix"
                Kind = LocalizedFix
                Objective = "Repair a localized F# defect without changing the public API."
                EditablePaths = [ "src/**" ]
                RequiredConstraints = [ "build"; "tests" ] }
              { Id = "cross-file-change"
                Kind = CrossFileChange
                Objective = "Implement a typed behavior change spanning an F# module and its tests."
                EditablePaths = [ "src/**"; "tests/**" ]
                RequiredConstraints = [ "build"; "tests" ] }
              { Id = "async-process-integration"
                Kind = AsyncProcessIntegration
                Objective = "Correct cancellation and process-tree handling in an asynchronous F# integration."
                EditablePaths = [ "src/**"; "tests/**" ]
                RequiredConstraints = [ "build"; "tests"; "cancellation" ] }
              { Id = "funcui-elmish-behavior"
                Kind = FuncUiElmishBehavior
                Objective = "Correct an Avalonia FuncUI Elmish state/view behavior with keyboard-accessible coverage."
                EditablePaths = [ "src/**"; "tests/**" ]
                RequiredConstraints = [ "build"; "tests"; "headless-ui" ] } ]
          Arms =
            [ { Id = SolArmId
                Model = { Id = "gpt-5.6-sol"; Effort = Medium }
                Strategy = SingleTurn }
              { Id = LunaOneTurnArmId
                Model = { Id = "gpt-5.6-luna"; Effort = Max }
                Strategy = SingleTurn }
              { Id = LunaRatchetArmId
                Model = { Id = "gpt-5.6-luna"; Effort = Max }
                Strategy = ExternalRatchet 3 } ]
          NominalTokenBudgetPerEpisode = 50_000L
          NominalDurationPerEpisode = TimeSpan.FromMinutes 15.0
          DisclosedAggregateTokenBudget = 600_000L }

    let private budgetedTokens nominalCap episode =
        if episode.QualityPassed then
            episode.RawTokens |> Option.defaultValue nominalCap
        else
            nominalCap

    let assess (episodes: BenchmarkEpisode list) =
        let expectedKeys =
            [ for task in preset.Tasks do
                  for arm in preset.Arms do
                      task.Id, arm.Id ]
            |> Set.ofList

        let actualKeys = episodes |> List.map (fun episode -> episode.TaskId, episode.ArmId)

        if episodes |> List.exists (fun episode -> episode.RawTokens.IsNone) then
            Inconclusive "At least one episode has unknown token usage."
        elif Set.ofList actualKeys <> expectedKeys || actualKeys.Length <> expectedKeys.Count then
            Inconclusive "The smoke test requires exactly one result for each of its 12 frozen episodes."
        else
            let forArm armId =
                episodes |> List.filter (fun episode -> episode.ArmId = armId)

            let sol = forArm SolArmId
            let ratchet = forArm LunaRatchetArmId

            let solved values =
                values |> List.filter _.QualityPassed |> List.length

            let charged values =
                values |> List.sumBy (budgetedTokens preset.NominalTokenBudgetPerEpisode)

            let comparison =
                { SolvedBySol = solved sol
                  SolvedByRatchet = solved ratchet
                  SolBudgetedTokensToQuality = charged sol
                  RatchetBudgetedTokensToQuality = charged ratchet }

            let ratchetHasCriticalDefect =
                ratchet |> List.exists _.CriticalQualityOrSafetyDefect

            let withinEightyPercent =
                decimal comparison.RatchetBudgetedTokensToQuality
                <= decimal comparison.SolBudgetedTokensToQuality * 0.8M

            if
                comparison.SolvedByRatchet >= comparison.SolvedBySol
                && not ratchetHasCriticalDefect
                && withinEightyPercent
            then
                Promising comparison
            else
                NotYetPromising comparison
