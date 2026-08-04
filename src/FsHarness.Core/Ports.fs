namespace FsHarness.Core

open System
open System.Threading

type RepositoryInspection =
    { TopLevel: string
      CommonDirectory: string
      Head: CommitOid
      Branch: string option
      IsDirty: bool
      DirtySummary: string
      HasSubmodules: bool
      IsSparseCheckout: bool }

type ModelCapability =
    { Id: string
      SupportedReasoningEfforts: ReasoningEffort list }

[<RequireQualifiedAccess>]
type CodexWritePolicy =
    | Unrestricted
    | WorkspaceWrite
    | ReadOnly

[<RequireQualifiedAccess>]
module CodexWritePolicy =
    let defaultValue = CodexWritePolicy.Unrestricted

    let parse (value: string option) =
        match value |> Option.map (fun text -> text.Trim().ToLowerInvariant()) with
        | None
        | Some ""
        | Some "unrestricted" -> Ok CodexWritePolicy.Unrestricted
        | Some "workspace-write" -> Ok CodexWritePolicy.WorkspaceWrite
        | Some "read-only" -> Ok CodexWritePolicy.ReadOnly
        | Some invalid ->
            Error(
                $"FSHARNESS_CODEX_WRITE_POLICY must be 'unrestricted', 'workspace-write', or 'read-only'; received '{invalid}'."
            )

    let label policy =
        match policy with
        | CodexWritePolicy.Unrestricted -> "unrestricted"
        | CodexWritePolicy.WorkspaceWrite -> "workspace-write"
        | CodexWritePolicy.ReadOnly -> "read-only"

type CodexPreflight =
    { Version: string
      LoginStatus: string
      DoctorJson: string
      Models: ModelCapability list
      WritePolicy: CodexWritePolicy }

type CodexRequest =
    { Executable: string
      WorkingDirectory: string
      Model: ModelSpec
      Prompt: string
      OutputSchemaPath: string
      Timeout: TimeSpan
      JsonlPath: string
      StderrPath: string }

type CodexResult =
    { ThreadId: string
      Usage: TokenUsage option
      Summary: ExperimentSummary
      ExitCode: int
      SawThreadStarted: bool }

type CandidateWorkspace =
    { ExperimentId: ExperimentId
      GenerationPath: string
      Parent: CommitOid }

type CandidateSnapshot =
    { Commit: CommitOid
      ChangedPaths: string list
      ProtectedPaths: string list
      EvaluationPath: string
      FrontierEvaluationPath: string }

type CodexPort =
    { Preflight: string -> CancellationToken -> Async<Result<CodexPreflight, HarnessError>>
      Run: CodexRequest -> CancellationToken -> Async<Result<CodexResult, HarnessError>> }

type GitPort =
    { InspectSource: string -> CancellationToken -> Async<Result<RepositoryInspection, HarnessError>>
      CreateRun: RunId -> RepositoryInspection -> CancellationToken -> Async<Result<unit, HarnessError>>
      PrepareCandidate:
          RunId -> ExperimentId -> CommitOid -> CancellationToken -> Async<Result<CandidateWorkspace, HarnessError>>
      ApplySeedPatch: CandidateWorkspace -> string -> CancellationToken -> Async<Result<unit, HarnessError>>
      CaptureCandidate:
          RunId
              -> CandidateWorkspace
              -> string list
              -> CancellationToken
              -> Async<Result<CandidateSnapshot, HarnessError>>
      AdvanceFrontier: RunId -> CommitOid -> CommitOid -> CancellationToken -> Async<Result<unit, HarnessError>>
      ExportPatch: RunId -> CommitOid -> string -> CancellationToken -> Async<Result<string, HarnessError>> }

type EvaluatorPort =
    { Run:
        EvaluatorSpec
            -> string
            -> string
            -> string
            -> CancellationToken
            -> Async<Result<EvaluationResult, HarnessError>> }

type JournalPort =
    { Initialize: CancellationToken -> Async<Result<unit, HarnessError>>
      AppendEvent:
          RunId -> ExperimentId option -> string -> string -> CancellationToken -> Async<Result<unit, HarnessError>>
      SaveUsage: RunId -> ExperimentId -> TokenUsage option -> CancellationToken -> Async<Result<unit, HarnessError>>
      LoadMemories: RunId -> int -> int -> CancellationToken -> Async<Result<MemorySummary list, HarnessError>>
      SaveMemory: RunId -> MemorySummary -> CancellationToken -> Async<Result<unit, HarnessError>> }

type MemoryPort =
    { Select: RunId -> int -> int -> CancellationToken -> Async<Result<MemorySummary list, HarnessError>>
      Append: RunId -> MemorySummary -> CancellationToken -> Async<Result<unit, HarnessError>> }
