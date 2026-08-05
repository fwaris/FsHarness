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

    let private runRef runId suffix =
        $"refs/fsharness/runs/{RunId.text runId}/{suffix}"

    type GitLineage =
        { Baseline: CommitOid option
          Frontier: CommitOid option
          Candidates: Map<ExperimentId, CommitOid * CommitOid option> }

    let loadLineage store runId cancellationToken : Async<Result<GitLineage, HarnessError>> =
        async {
            let repository = repoPath store runId

            if not (Directory.Exists repository) then
                return
                    Error(
                        HarnessError.create
                            "git.lineage_repository_missing"
                            HarnessErrorCategory.Git
                            "The private Git repository for this run is missing."
                    )
            else
                let! refsResult =
                    requireSuccess
                        store
                        "lineage_refs"
                        None
                        [ "--git-dir"
                          repository
                          "for-each-ref"
                          "--format=%(refname)\t%(objectname)"
                          (runRef runId "") ]
                        None
                        Map.empty
                        cancellationToken

                match refsResult with
                | Error error -> return Error error
                | Ok refsText ->
                    let prefix = runRef runId ""
                    let candidatePrefix = runRef runId "candidates/"

                    let parsedRefs =
                        refsText.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                        |> Array.toList
                        |> List.choose (fun line ->
                            match line.Split('\t', 2, StringSplitOptions.None) with
                            | [| name; oid |] when name.StartsWith(prefix, StringComparison.Ordinal) ->
                                Some(name.Trim(), oid.Trim())
                            | _ -> None)

                    let commitByExperiment =
                        parsedRefs
                        |> List.choose (fun (name, oid) ->
                            if name.StartsWith(candidatePrefix, StringComparison.Ordinal) then
                                let suffix = name.Substring(candidatePrefix.Length)

                                match Guid.TryParse suffix with
                                | true, value -> Some(ExperimentId.ofGuid value, CommitOid.create oid)
                                | false, _ -> None
                            else
                                None)

                    let parentFor (commit: CommitOid) =
                        async {
                            let! result =
                                requireSuccess
                                    store
                                    "lineage_parent"
                                    None
                                    [ "--git-dir"
                                      repository
                                      "rev-list"
                                      "--parents"
                                      "--max-count=1"
                                      (CommitOid.value commit) ]
                                    None
                                    Map.empty
                                    cancellationToken

                            return
                                match result with
                                | Error _ -> None
                                | Ok text ->
                                    text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                                    |> Array.tryItem 1
                                    |> Option.map CommitOid.create
                        }

                    let mutable candidates = Map.empty

                    for experimentId, commit in commitByExperiment do
                        let! parent = parentFor commit
                        candidates <- candidates.Add(experimentId, (commit, parent))

                    let findRef suffix =
                        parsedRefs
                        |> List.tryPick (fun (name, oid) ->
                            if name = runRef runId suffix then
                                Some(CommitOid.create oid)
                            else
                                None)

                    return
                        Ok
                            { Baseline = findRef "baseline"
                              Frontier = findRef "frontier"
                              Candidates = candidates }
        }

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

    let prepareCandidate store runId experimentId (parents: ExperimentParent list) champion cancellationToken =
        async {
            let repository = repoPath store runId

            let primary =
                parents
                |> List.tryFind (fun parent -> parent.Role = ExperimentParentRole.Primary)

            match primary with
            | None ->
                return
                    Error(
                        HarnessError.create
                            "git.primary_parent_missing"
                            HarnessErrorCategory.Git
                            "A candidate workspace requires one primary parent."
                    )
            | Some primaryParent ->

                let generation =
                    Path.Combine(DataPaths.experiment store.Root runId experimentId, "generation")

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
                          CommitOid.value primaryParent.Commit ]
                        None
                        Map.empty
                        cancellationToken

                return
                    result
                    |> Result.map (fun _ ->
                        { ExperimentId = experimentId
                          GenerationPath = generation
                          Parents = parents
                          Champion = champion })
        }

    let prepareSynthesis store runId experimentId primary contributor champion cancellationToken =
        async {
            let parents: ExperimentParent list =
                [ { Commit = primary
                    Role = ExperimentParentRole.Primary }
                  { Commit = contributor
                    Role = ExperimentParentRole.Contributor } ]

            match! prepareCandidate store runId experimentId parents champion cancellationToken with
            | Error error -> return Error error
            | Ok workspace ->
                let! mergeResult =
                    execute
                        store
                        (Some workspace.GenerationPath)
                        [ "merge"; "--no-commit"; "--no-ff"; CommitOid.value contributor ]
                        None
                        (Map.ofList
                            [ "GIT_AUTHOR_NAME", "FsHarness"
                              "GIT_AUTHOR_EMAIL", "fsharness@localhost"
                              "GIT_COMMITTER_NAME", "FsHarness"
                              "GIT_COMMITTER_EMAIL", "fsharness@localhost" ])
                        cancellationToken

                match mergeResult with
                | Error error -> return Error(mapGitError "prepare_synthesis" error)
                | Ok result when result.ExitCode = 0 -> return Ok(SynthesisPreparation.Clean workspace)
                | Ok _ ->
                    let! filesResult =
                        requireSuccess
                            store
                            "synthesis_conflict_files"
                            (Some workspace.GenerationPath)
                            [ "diff"; "--name-only"; "--diff-filter=U" ]
                            None
                            Map.empty
                            cancellationToken

                    let! diffResult =
                        requireSuccess
                            store
                            "synthesis_conflict_diff"
                            (Some workspace.GenerationPath)
                            [ "diff"; "--cc" ]
                            None
                            Map.empty
                            cancellationToken

                    match filesResult, diffResult with
                    | Ok files, Ok conflictText ->
                        return
                            Ok(
                                SynthesisPreparation.Conflicted(
                                    workspace,
                                    { Files =
                                        files.Split([| '\r'; '\n' |], StringSplitOptions.RemoveEmptyEntries)
                                        |> List.ofArray
                                      ConflictText = conflictText }
                                )
                            )
                    | Error error, _
                    | _, Error error -> return Error error
        }

    let analyzeSynthesis store runId primary contributor cancellationToken =
        async {
            let repository = repoPath store runId
            let primaryText = CommitOid.value primary
            let contributorText = CommitOid.value contributor

            let! mergeBase =
                requireSuccess
                    store
                    "synthesis_merge_base"
                    None
                    [ "--git-dir"; repository; "merge-base"; primaryText; contributorText ]
                    None
                    Map.empty
                    cancellationToken

            match mergeBase with
            | Error error -> return Error error
            | Ok baseText ->
                let baseCommit = baseText.Trim()

                let! primaryPaths =
                    requireSuccess
                        store
                        "synthesis_primary_paths"
                        None
                        [ "--git-dir"
                          repository
                          "diff"
                          "--name-only"
                          "-z"
                          baseCommit
                          primaryText
                          "--" ]
                        None
                        Map.empty
                        cancellationToken

                let! contributorPaths =
                    requireSuccess
                        store
                        "synthesis_contributor_paths"
                        None
                        [ "--git-dir"
                          repository
                          "diff"
                          "--name-only"
                          "-z"
                          baseCommit
                          contributorText
                          "--" ]
                        None
                        Map.empty
                        cancellationToken

                let! mergeTree =
                    execute
                        store
                        None
                        [ "--git-dir"
                          repository
                          "merge-tree"
                          "--write-tree"
                          primaryText
                          contributorText ]
                        None
                        Map.empty
                        cancellationToken

                match primaryPaths, contributorPaths, mergeTree with
                | Ok primaryText, Ok contributorText, Ok merge ->
                    let paths (text: string) =
                        text.Split('\000', StringSplitOptions.RemoveEmptyEntries) |> Set.ofArray

                    let primarySet = paths primaryText
                    let contributorSet = paths contributorText

                    return
                        Ok
                            { CleanMerge = merge.ExitCode = 0
                              ChangedPathOverlap = Set.intersect primarySet contributorSet |> Set.count }
                | Error error, _, _
                | _, Error error, _ -> return Error error
                | _, _, Error error -> return Error(mapGitError "synthesis_merge_tree" error)
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

            let primaryParent =
                workspace.Parents
                |> List.find (fun parent -> parent.Role = ExperimentParentRole.Primary)

            let parentText = CommitOid.value primaryParent.Commit

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

                let unresolvedMarkers =
                    changed
                    |> List.filter (fun relativePath ->
                        let path = Path.Combine(workspace.GenerationPath, relativePath)

                        try
                            if File.Exists path then
                                let text = File.ReadAllText path

                                text.Contains("<<<<<<<", StringComparison.Ordinal)
                                || text.Contains("=======", StringComparison.Ordinal)
                                || text.Contains(">>>>>>>", StringComparison.Ordinal)
                            else
                                false
                        with _ ->
                            false)

                let policyEditable, policyProtected = PathPolicy.partition editableGlobs changed

                let editable =
                    policyEditable
                    |> List.filter (fun path -> not (List.contains path unresolvedMarkers))

                let initiallyProtected = policyProtected @ unresolvedMarkers |> List.distinct

                let experimentRoot = DataPaths.experiment store.Root runId workspace.ExperimentId

                let assembly = Path.Combine(experimentRoot, "assembly")
                let evaluation = Path.Combine(experimentRoot, "evaluation")
                let parentEvaluation = Path.Combine(experimentRoot, "parent-evaluation")
                let championEvaluation = Path.Combine(experimentRoot, "champion-evaluation")

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
                                let parentLines =
                                    workspace.Parents
                                    |> List.map (fun parent ->
                                        $"FsHarness-Parent-{parent.Role}: {CommitOid.value parent.Commit}")
                                    |> String.concat "\n"

                                $"FsHarness experiment {ExperimentId.text workspace.ExperimentId}\n\nFsHarness-Run: {RunId.text runId}\nFsHarness-Experiment: {ExperimentId.text workspace.ExperimentId}\n{parentLines}\n"

                            let commitArguments =
                                [ "commit-tree"; treeText.Trim() ]
                                @ (workspace.Parents
                                   |> List.collect (fun parent -> [ "-p"; CommitOid.value parent.Commit ]))

                            let! commit =
                                requireSuccess
                                    store
                                    "commit_candidate"
                                    (Some assembly)
                                    commitArguments
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

                                    let! parentEvaluationResult =
                                        match evaluationResult with
                                        | Error error -> async { return Error error }
                                        | Ok _ ->
                                            requireSuccess
                                                store
                                                "prepare_parent_evaluation"
                                                None
                                                [ "--git-dir"
                                                  repository
                                                  "worktree"
                                                  "add"
                                                  "--detach"
                                                  parentEvaluation
                                                  parentText ]
                                                None
                                                Map.empty
                                                cancellationToken

                                    let! championEvaluationResult =
                                        match parentEvaluationResult with
                                        | Error error -> async { return Error error }
                                        | Ok _ ->
                                            requireSuccess
                                                store
                                                "prepare_champion_evaluation"
                                                None
                                                [ "--git-dir"
                                                  repository
                                                  "worktree"
                                                  "add"
                                                  "--detach"
                                                  championEvaluation
                                                  CommitOid.value workspace.Champion ]
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
                                        championEvaluationResult
                                        |> Result.map (fun _ ->
                                            { Commit = CommitOid.create candidateText
                                              ChangedPaths = changed
                                              ProtectedPaths = protectedPaths |> List.distinct
                                              EvaluationPath = evaluation
                                              ParentEvaluationPath = parentEvaluation
                                              ChampionEvaluationPath = championEvaluation })
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

    let diffCommits store runId fromCommit toCommit cancellationToken =
        async {
            let! diff =
                requireSuccess
                    store
                    "diff_commits"
                    None
                    [ "--git-dir"
                      repoPath store runId
                      "diff"
                      "--binary"
                      "--full-index"
                      CommitOid.value fromCommit
                      CommitOid.value toCommit ]
                    None
                    Map.empty
                    cancellationToken

            return diff
        }

    let port store =
        { InspectSource = inspectSource store
          CreateRun = createRun store
          PrepareCandidate = prepareCandidate store
          PrepareSynthesis = prepareSynthesis store
          AnalyzeSynthesis = analyzeSynthesis store
          ApplySeedPatch = applySeedPatch store
          CaptureCandidate = captureCandidate store
          AdvanceFrontier = advanceFrontier store
          ExportPatch = exportPatch store }
