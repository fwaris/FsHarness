namespace FsHarness.Infrastructure

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Threading
open FsHarness.Core

type GitStore =
    private
        { Root: string
          GitExecutable: string }

[<RequireQualifiedAccess>]
module GitStore =
    let create root =
        { Root = Path.GetFullPath root
          GitExecutable = "git" }

    let private mapGitError operation (error: HarnessError) =
        { error with
            Code = $"git.{operation}"
            Category = HarnessErrorCategory.Git
            Summary = $"Git operation '{operation}' failed." }

    let private gitEnvironment additional =
        SanitizedEnvironment.core ()
        |> Map.add "GIT_OPTIONAL_LOCKS" "0"
        |> Map.add "GIT_TERMINAL_PROMPT" "0"
        |> Map.add "GIT_CONFIG_NOSYSTEM" "1"
        |> Map.fold (fun state key value -> state |> Map.add key value) additional

    let private execute store workingDirectory arguments standardInput environment cancellationToken =
        ProcessRunner.run
            { Executable = store.GitExecutable
              Arguments = arguments
              WorkingDirectory = workingDirectory
              StandardInput = standardInput
              Environment = gitEnvironment environment
              ClearEnvironment = true
              Timeout = TimeSpan.FromMinutes 3.0
              MaxCaptureCharacters = 2_000_000 }
            cancellationToken

    let private requireSuccess store operation workingDirectory arguments standardInput environment cancellationToken =
        async {
            let! result = execute store workingDirectory arguments standardInput environment cancellationToken

            match result with
            | Error error -> return Error(mapGitError operation error)
            | Ok value when value.ExitCode = 0 -> return Ok value.Stdout
            | Ok value ->
                return
                    Error
                        { Code = $"git.{operation}"
                          Category = HarnessErrorCategory.Git
                          Summary = $"Git operation '{operation}' exited with code {value.ExitCode}."
                          Detail = Some value.Stderr
                          Retryable = false
                          SuggestedActions = []
                          ExperimentId = None
                          LogPath = None }
        }

    let private runInSource store source arguments cancellationToken =
        requireSuccess store "inspect" (Some source) arguments None Map.empty cancellationToken

    let private canonicalFrom (basePath: string) (path: string) =
        if Path.IsPathRooted path then
            Path.GetFullPath path
        else
            Path.GetFullPath(Path.Combine(basePath, path))

    let inspectSource store source cancellationToken =
        async {
            let sourcePath = Path.GetFullPath source

            if not (Directory.Exists sourcePath) then
                return
                    Error(
                        HarnessError.create
                            "git.source_missing"
                            HarnessErrorCategory.Git
                            "Source repository does not exist."
                        |> HarnessError.withDetail sourcePath
                    )
            else
                let! topLevelResult = runInSource store sourcePath [ "rev-parse"; "--show-toplevel" ] cancellationToken

                match topLevelResult with
                | Error _ ->
                    return
                        Error(
                            HarnessError.create
                                "git.not_worktree"
                                HarnessErrorCategory.Git
                                "The selected folder is not a non-bare Git worktree with a committed baseline."
                        )
                | Ok topLevelText ->
                    let topLevel = topLevelText.Trim() |> Path.GetFullPath

                    let! bareResult =
                        runInSource store topLevel [ "rev-parse"; "--is-bare-repository" ] cancellationToken

                    let! headResult =
                        runInSource store topLevel [ "rev-parse"; "--verify"; "HEAD^{commit}" ] cancellationToken

                    let! commonResult = runInSource store topLevel [ "rev-parse"; "--git-common-dir" ] cancellationToken

                    let! statusResult =
                        runInSource
                            store
                            topLevel
                            [ "status"; "--porcelain=v1"; "-z"; "--untracked-files=all" ]
                            cancellationToken

                    let! submoduleResult = runInSource store topLevel [ "ls-files"; "--stage"; "-z" ] cancellationToken

                    match bareResult, headResult, commonResult, statusResult, submoduleResult with
                    | Ok bare, Ok head, Ok common, Ok status, Ok stage ->
                        if bare.Trim().Equals("true", StringComparison.OrdinalIgnoreCase) then
                            return
                                Error(
                                    HarnessError.create
                                        "git.bare_source"
                                        HarnessErrorCategory.Git
                                        "Bare repositories are not supported as source worktrees."
                                )
                        else
                            let! branchRaw =
                                execute
                                    store
                                    (Some topLevel)
                                    [ "symbolic-ref"; "--quiet"; "--short"; "HEAD" ]
                                    None
                                    Map.empty
                                    cancellationToken

                            let branch =
                                match branchRaw with
                                | Ok value when value.ExitCode = 0 -> Some(value.Stdout.Trim())
                                | _ -> None

                            let! sparseRaw =
                                execute
                                    store
                                    (Some topLevel)
                                    [ "config"; "--bool"; "core.sparseCheckout" ]
                                    None
                                    Map.empty
                                    cancellationToken

                            let sparse =
                                match sparseRaw with
                                | Ok value when value.ExitCode = 0 -> value.Stdout.Trim() = "true"
                                | _ -> false

                            let dirtyCount = status.Split('\000', StringSplitOptions.RemoveEmptyEntries).Length

                            let hasSubmodules =
                                stage.Split('\000', StringSplitOptions.RemoveEmptyEntries)
                                |> Array.exists (fun entry -> entry.StartsWith("160000 ", StringComparison.Ordinal))

                            return
                                Ok
                                    { TopLevel = topLevel
                                      CommonDirectory = canonicalFrom topLevel (common.Trim())
                                      Head = CommitOid.create (head.Trim())
                                      Branch = branch
                                      IsDirty = dirtyCount > 0
                                      DirtySummary =
                                        if dirtyCount = 0 then
                                            "Clean"
                                        else
                                            $"{dirtyCount} staged, unstaged, or untracked entries are excluded."
                                      HasSubmodules = hasSubmodules
                                      IsSparseCheckout = sparse }
                    | _ ->
                        return
                            Error(
                                HarnessError.create
                                    "git.inspect_incomplete"
                                    HarnessErrorCategory.Git
                                    "Git source inspection did not complete."
                            )
        }

    let private repoPath store runId = DataPaths.repository store.Root runId
    let private worktreeRoot store runId = DataPaths.worktrees store.Root runId

    let private runRef runId suffix =
        $"refs/fsharness/runs/{RunId.text runId}/{suffix}"

    let createRun store runId inspection cancellationToken =
        async {
            if inspection.HasSubmodules then
                return
                    Error(
                        HarnessError.create
                            "git.submodules_unsupported"
                            HarnessErrorCategory.Git
                            "Repositories containing gitlinks/submodules are not supported in v1."
                    )
            elif inspection.IsSparseCheckout then
                return
                    Error(
                        HarnessError.create
                            "git.sparse_unsupported"
                            HarnessErrorCategory.Git
                            "Sparse checkouts are not supported in v1."
                    )
            else
                let runRoot = DataPaths.runRoot store.Root runId
                let repository = repoPath store runId
                Directory.CreateDirectory(runRoot) |> ignore

                if Directory.Exists repository then
                    return
                        Error(
                            HarnessError.create
                                "git.run_exists"
                                HarnessErrorCategory.Git
                                "The managed Git repository for this run already exists."
                        )
                else
                    let! clone =
                        requireSuccess
                            store
                            "clone"
                            None
                            [ "clone"
                              "--bare"
                              "--no-local"
                              "--no-hardlinks"
                              "--no-tags"
                              inspection.TopLevel
                              repository ]
                            None
                            Map.empty
                            cancellationToken

                    match clone with
                    | Error error -> return Error error
                    | Ok _ ->
                        let hooksPath = Path.Combine(runRoot, "disabled-hooks")
                        Directory.CreateDirectory(hooksPath) |> ignore

                        let configurations =
                            [ [ "remote"; "remove"; "origin" ]
                              [ "config"; "core.hooksPath"; hooksPath ]
                              [ "config"; "gc.auto"; "0" ]
                              [ "config"; "maintenance.auto"; "false" ]
                              [ "config"; "commit.gpgSign"; "false" ]
                              [ "config"; "push.default"; "nothing" ] ]

                        let mutable configurationError = None

                        for arguments in configurations do
                            if configurationError.IsNone then
                                let! configured =
                                    requireSuccess
                                        store
                                        "configure_private_store"
                                        None
                                        ([ "--git-dir"; repository ] @ arguments)
                                        None
                                        Map.empty
                                        cancellationToken

                                match configured with
                                | Error error -> configurationError <- Some error
                                | Ok _ -> ()

                        match configurationError with
                        | Some error -> return Error error
                        | None ->
                            let alternates = Path.Combine(repository, "objects", "info", "alternates")

                            if File.Exists alternates then
                                return
                                    Error(
                                        HarnessError.create
                                            "git.alternates_detected"
                                            HarnessErrorCategory.Git
                                            "Private Git store unexpectedly references source objects."
                                    )
                            else
                                let oid = CommitOid.value inspection.Head
                                let baseline = runRef runId "baseline"
                                let frontier = runRef runId "frontier"

                                let! refs =
                                    requireSuccess
                                        store
                                        "initialize_refs"
                                        None
                                        [ "--git-dir"; repository; "update-ref"; "--stdin" ]
                                        (Some $"create {baseline} {oid}\ncreate {frontier} {oid}\n")
                                        Map.empty
                                        cancellationToken

                                return refs |> Result.map ignore
        }

    let prepareCandidate store runId experimentId parent cancellationToken =
        async {
            let repository = repoPath store runId

            let generation =
                Path.Combine(worktreeRoot store runId, ExperimentId.text experimentId, "generation")

            Directory.CreateDirectory(Path.GetDirectoryName generation) |> ignore

            let! result =
                requireSuccess
                    store
                    "prepare_candidate"
                    None
                    [ "--git-dir"
                      repository
                      "worktree"
                      "add"
                      "--detach"
                      generation
                      CommitOid.value parent ]
                    None
                    Map.empty
                    cancellationToken

            return
                result
                |> Result.map (fun _ ->
                    { ExperimentId = experimentId
                      GenerationPath = generation
                      Parent = parent })
        }

    let private nulValues (text: string) =
        text.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> List.ofArray

    let private executableMode (path: string) =
        if OperatingSystem.IsWindows() then
            "100644"
        else
            try
                let mode = File.GetUnixFileMode path

                if
                    mode.HasFlag UnixFileMode.UserExecute
                    || mode.HasFlag UnixFileMode.GroupExecute
                    || mode.HasFlag UnixFileMode.OtherExecute
                then
                    "100755"
                else
                    "100644"
            with _ ->
                "100644"

    let private copyRegularFile sourceRoot destinationRoot relativePath =
        let source = Path.Combine(sourceRoot, relativePath)
        let destination = Path.Combine(destinationRoot, relativePath)

        match DataPaths.ensureContained sourceRoot source, DataPaths.ensureContained destinationRoot destination with
        | Ok sourcePath, Ok destinationPath when File.Exists sourcePath ->
            let attributes = File.GetAttributes sourcePath

            if attributes.HasFlag FileAttributes.ReparsePoint then
                Error "Changed symlinks or reparse points are protected."
            else
                Directory.CreateDirectory(Path.GetDirectoryName destinationPath) |> ignore
                File.Copy(sourcePath, destinationPath, true)

                if not (OperatingSystem.IsWindows()) then
                    try
                        File.SetUnixFileMode(destinationPath, File.GetUnixFileMode sourcePath)
                    with _ ->
                        ()

                Ok(Some sourcePath)
        | Ok _, Ok destinationPath ->
            if File.Exists destinationPath then
                File.Delete destinationPath

            Ok None
        | Error error, _
        | _, Error error -> Error error

    let captureCandidate
        (store: GitStore)
        (runId: RunId)
        (workspace: CandidateWorkspace)
        (editableGlobs: string list)
        (cancellationToken: CancellationToken)
        =
        async {
            let repository = repoPath store runId
            let parentText = CommitOid.value workspace.Parent

            let! tracked =
                requireSuccess
                    store
                    "candidate_diff"
                    (Some workspace.GenerationPath)
                    [ "diff"; "--name-only"; "-z"; "--no-renames"; parentText; "--" ]
                    None
                    Map.empty
                    cancellationToken

            let! untracked =
                requireSuccess
                    store
                    "candidate_untracked"
                    (Some workspace.GenerationPath)
                    [ "ls-files"; "--others"; "--exclude-standard"; "-z" ]
                    None
                    Map.empty
                    cancellationToken

            match tracked, untracked with
            | Ok trackedText, Ok untrackedText ->
                let changed =
                    nulValues trackedText @ nulValues untrackedText
                    |> List.map PathPolicy.normalize
                    |> List.distinct

                let editable, initiallyProtected = PathPolicy.partition editableGlobs changed

                let experimentRoot =
                    Path.Combine(worktreeRoot store runId, ExperimentId.text workspace.ExperimentId)

                let assembly = Path.Combine(experimentRoot, "assembly")
                let evaluation = Path.Combine(experimentRoot, "evaluation")
                let frontierEvaluation = Path.Combine(experimentRoot, "frontier-evaluation")

                let! assemblyResult =
                    requireSuccess
                        store
                        "prepare_assembly"
                        None
                        [ "--git-dir"; repository; "worktree"; "add"; "--detach"; assembly; parentText ]
                        None
                        Map.empty
                        cancellationToken

                match assemblyResult with
                | Error error -> return Error error
                | Ok _ ->
                    let mutable validEditable = []
                    let mutable protectedPaths = initiallyProtected

                    for path in editable do
                        match copyRegularFile workspace.GenerationPath assembly path with
                        | Ok source -> validEditable <- (path, source) :: validEditable
                        | Error _ -> protectedPaths <- path :: protectedPaths

                    let mutable plumbingError = None

                    for path, source in List.rev validEditable do
                        if plumbingError.IsNone then
                            match source with
                            | None ->
                                let! removed =
                                    requireSuccess
                                        store
                                        "remove_candidate_path"
                                        (Some assembly)
                                        [ "update-index"; "--force-remove"; "--"; path ]
                                        None
                                        Map.empty
                                        cancellationToken

                                match removed with
                                | Error error -> plumbingError <- Some error
                                | Ok _ -> ()
                            | Some sourcePath ->
                                let! blob =
                                    requireSuccess
                                        store
                                        "hash_candidate_blob"
                                        (Some assembly)
                                        [ "hash-object"; "-w"; "--no-filters"; sourcePath ]
                                        None
                                        Map.empty
                                        cancellationToken

                                match blob with
                                | Error error -> plumbingError <- Some error
                                | Ok blobText ->
                                    let! indexed =
                                        requireSuccess
                                            store
                                            "index_candidate_blob"
                                            (Some assembly)
                                            [ "update-index"
                                              "--add"
                                              "--cacheinfo"
                                              executableMode sourcePath
                                              blobText.Trim()
                                              path ]
                                            None
                                            Map.empty
                                            cancellationToken

                                    match indexed with
                                    | Error error -> plumbingError <- Some error
                                    | Ok _ -> ()

                    match plumbingError with
                    | Some error -> return Error error
                    | None ->
                        let! tree =
                            requireSuccess
                                store
                                "write_candidate_tree"
                                (Some assembly)
                                [ "write-tree" ]
                                None
                                Map.empty
                                cancellationToken

                        match tree with
                        | Error error -> return Error error
                        | Ok treeText ->
                            let timestamp = DateTimeOffset.UtcNow.ToString("o")

                            let commitEnvironment =
                                Map.ofList
                                    [ "GIT_AUTHOR_NAME", "FsHarness"
                                      "GIT_AUTHOR_EMAIL", "fsharness@localhost"
                                      "GIT_COMMITTER_NAME", "FsHarness"
                                      "GIT_COMMITTER_EMAIL", "fsharness@localhost"
                                      "GIT_AUTHOR_DATE", timestamp
                                      "GIT_COMMITTER_DATE", timestamp ]

                            let message =
                                $"FsHarness experiment {ExperimentId.text workspace.ExperimentId}\n\nFsHarness-Run: {RunId.text runId}\nFsHarness-Experiment: {ExperimentId.text workspace.ExperimentId}\nFsHarness-Parent: {parentText}\n"

                            let! commit =
                                requireSuccess
                                    store
                                    "commit_candidate"
                                    (Some assembly)
                                    [ "commit-tree"; treeText.Trim(); "-p"; parentText ]
                                    (Some message)
                                    commitEnvironment
                                    cancellationToken

                            match commit with
                            | Error error -> return Error error
                            | Ok commitText ->
                                let candidateText = commitText.Trim()

                                let candidateRef =
                                    runRef runId $"candidates/{ExperimentId.text workspace.ExperimentId}"

                                let zeroOid = String('0', parentText.Length)

                                let! candidateRefResult =
                                    requireSuccess
                                        store
                                        "create_candidate_ref"
                                        None
                                        [ "--git-dir"; repository; "update-ref"; candidateRef; candidateText; zeroOid ]
                                        None
                                        Map.empty
                                        cancellationToken

                                match candidateRefResult with
                                | Error error -> return Error error
                                | Ok _ ->
                                    let! evaluationResult =
                                        requireSuccess
                                            store
                                            "prepare_evaluation"
                                            None
                                            [ "--git-dir"
                                              repository
                                              "worktree"
                                              "add"
                                              "--detach"
                                              evaluation
                                              candidateText ]
                                            None
                                            Map.empty
                                            cancellationToken

                                    let! frontierEvaluationResult =
                                        match evaluationResult with
                                        | Error error -> async { return Error error }
                                        | Ok _ ->
                                            requireSuccess
                                                store
                                                "prepare_frontier_evaluation"
                                                None
                                                [ "--git-dir"
                                                  repository
                                                  "worktree"
                                                  "add"
                                                  "--detach"
                                                  frontierEvaluation
                                                  parentText ]
                                                None
                                                Map.empty
                                                cancellationToken

                                    let! _ =
                                        execute
                                            store
                                            None
                                            [ "--git-dir"; repository; "worktree"; "remove"; "--force"; assembly ]
                                            None
                                            Map.empty
                                            cancellationToken

                                    return
                                        frontierEvaluationResult
                                        |> Result.map (fun _ ->
                                            { Commit = CommitOid.create candidateText
                                              ChangedPaths = changed
                                              ProtectedPaths = protectedPaths |> List.distinct
                                              EvaluationPath = evaluation
                                              FrontierEvaluationPath = frontierEvaluation })
            | Error error, _
            | _, Error error -> return Error error
        }

    let advanceFrontier store runId expectedParent candidate cancellationToken =
        async {
            let! result =
                requireSuccess
                    store
                    "advance_frontier"
                    None
                    [ "--git-dir"
                      repoPath store runId
                      "update-ref"
                      runRef runId "frontier"
                      CommitOid.value candidate
                      CommitOid.value expectedParent ]
                    None
                    Map.empty
                    cancellationToken

            return result |> Result.map ignore
        }

    let applySeedPatch store (workspace: CandidateWorkspace) (patchPath: string) cancellationToken =
        async {
            let normalized = patchPath.Replace('\\', '/')

            let fullPatchPath =
                Path.GetFullPath(Path.Combine(workspace.GenerationPath, normalized))

            let seedRoot =
                Path.GetFullPath(Path.Combine(workspace.GenerationPath, ".fsharness", "seeds"))

            if
                not (
                    fullPatchPath.StartsWith(
                        seedRoot + string Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                || not (File.Exists fullPatchPath)
            then
                return
                    Error(
                        HarnessError.create
                            "git.seed_patch_missing"
                            HarnessErrorCategory.Git
                            "Protected seed patch was not found below .fsharness/seeds/."
                        |> HarnessError.withDetail normalized
                    )
            else
                let! result =
                    requireSuccess
                        store
                        "apply_seed"
                        (Some workspace.GenerationPath)
                        [ "apply"; "--whitespace=nowarn"; "--"; normalized ]
                        None
                        Map.empty
                        cancellationToken

                return result |> Result.map ignore
        }

    let exportPatch store runId candidate destination cancellationToken =
        async {
            let! patch =
                requireSuccess
                    store
                    "export_patch"
                    None
                    [ "--git-dir"
                      repoPath store runId
                      "diff"
                      "--binary"
                      "--full-index"
                      runRef runId "baseline"
                      CommitOid.value candidate ]
                    None
                    Map.empty
                    cancellationToken

            match patch with
            | Error error -> return Error error
            | Ok contents ->
                try
                    AtomicFile.writeAllText destination contents
                    return Ok destination
                with error ->
                    return
                        Error(
                            HarnessError.create
                                "git.export_write_failed"
                                HarnessErrorCategory.Git
                                "Could not write patch export."
                            |> HarnessError.withDetail error.Message
                        )
        }

    let port store =
        { InspectSource = inspectSource store
          CreateRun = createRun store
          PrepareCandidate = prepareCandidate store
          ApplySeedPatch = applySeedPatch store
          CaptureCandidate = captureCandidate store
          AdvanceFrontier = advanceFrontier store
          ExportPatch = exportPatch store }
