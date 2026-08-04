namespace FsHarness.App

open System
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Primitives
open Avalonia.Controls.Shapes
open Avalonia.FuncUI
open Avalonia.FuncUI.DSL
open Avalonia.FuncUI.Types
open Avalonia.Layout
open Avalonia.Media
open FsHarness.Core
open FsHarness.Infrastructure

module private Theme =
    let brush (value: string) =
        SolidColorBrush(Color.Parse value) :> IBrush

    let background = brush "#0D1117"
    let navigation = brush "#121821"
    let surface = brush "#161D27"
    let surfaceRaised = brush "#1C2633"
    let border = brush "#2A3747"
    let text = brush "#EDF3F8"
    let muted = brush "#94A4B5"
    let accent = brush "#55D6BE"
    let accentDark = brush "#163F3A"
    let warning = brush "#F2C14E"
    let danger = brush "#FF6B7A"
    let dangerDark = brush "#481F29"
    let radius = CornerRadius 10.0

[<RequireQualifiedAccess>]
module Views =
    let private text (value: string) (size: float) (foreground: IBrush) : IView =
        TextBlock.create
            [ TextBlock.text value
              TextBlock.fontSize size
              TextBlock.foreground foreground
              TextBlock.textWrapping TextWrapping.Wrap ]

    let private heading (value: string) : IView =
        TextBlock.create
            [ TextBlock.text value
              TextBlock.fontSize 24.0
              TextBlock.fontWeight FontWeight.SemiBold
              TextBlock.foreground Theme.text ]

    let private overline (value: string) : IView =
        TextBlock.create
            [ TextBlock.text value
              TextBlock.fontSize 11.0
              TextBlock.fontWeight FontWeight.Bold
              TextBlock.foreground Theme.accent ]

    let private muted (value: string) : IView = text value 12.0 Theme.muted

    let private card (children: IView list) : IView =
        Border.create
            [ Border.background Theme.surface
              Border.borderBrush Theme.border
              Border.borderThickness 1.0
              Border.cornerRadius Theme.radius
              Border.padding 16.0
              Border.child (StackPanel.create [ StackPanel.spacing 10.0; StackPanel.children children ]) ]

    let private field (label: string) (value: string) (field: DraftField) (dispatch: Msg -> unit) : IView =
        StackPanel.create
            [ StackPanel.spacing 5.0
              StackPanel.children
                  [ muted label
                    TextBox.create
                        [ TextBox.text value
                          TextBox.foreground Theme.text
                          TextBox.background Theme.surfaceRaised
                          TextBox.borderBrush Theme.border
                          TextBox.onTextChanged (fun next -> dispatch (DraftChanged(field, next))) ] ] ]

    let private multilineField
        (label: string)
        (value: string)
        (field: DraftField)
        (height: float)
        (dispatch: Msg -> unit)
        : IView =
        StackPanel.create
            [ StackPanel.spacing 5.0
              StackPanel.children
                  [ muted label
                    TextBox.create
                        [ TextBox.text value
                          TextBox.acceptsReturn true
                          TextBox.textWrapping TextWrapping.Wrap
                          TextBox.minHeight height
                          TextBox.foreground Theme.text
                          TextBox.background Theme.surfaceRaised
                          TextBox.borderBrush Theme.border
                          TextBox.onTextChanged (fun next -> dispatch (DraftChanged(field, next))) ] ] ]

    let private primaryButton (label: string) (enabled: bool) (message: Msg) (dispatch: Msg -> unit) : IView =
        Button.create
            [ Button.content label
              Button.isEnabled enabled
              Button.background Theme.accentDark
              Button.foreground Theme.accent
              Button.borderBrush Theme.accent
              Button.borderThickness 1.0
              Button.padding (Thickness(15.0, 9.0))
              Button.onClick (fun _ -> dispatch message) ]

    let private secondaryButton (label: string) (enabled: bool) (message: Msg) (dispatch: Msg -> unit) : IView =
        Button.create
            [ Button.content label
              Button.isEnabled enabled
              Button.background Theme.surfaceRaised
              Button.foreground Theme.text
              Button.borderBrush Theme.border
              Button.borderThickness 1.0
              Button.padding (Thickness(15.0, 9.0))
              Button.onClick (fun _ -> dispatch message) ]

    let private repositoryField (value: string) (enabled: bool) (dispatch: Msg -> unit) : IView =
        StackPanel.create
            [ StackPanel.spacing 5.0
              StackPanel.children
                  [ muted "Source repository"
                    Grid.create
                        [ Grid.columnDefinitions "*,Auto"
                          Grid.columnSpacing 8.0
                          Grid.children
                              [ TextBox.create
                                    [ TextBox.text value
                                      TextBox.foreground Theme.text
                                      TextBox.background Theme.surfaceRaised
                                      TextBox.borderBrush Theme.border
                                      TextBox.onTextChanged (fun next ->
                                          dispatch (DraftChanged(DraftField.SourcePath, next))) ]
                                Button.create
                                    [ Grid.column 1
                                      Button.content "Browse…"
                                      Button.isEnabled enabled
                                      Button.background Theme.surfaceRaised
                                      Button.foreground Theme.text
                                      Button.borderBrush Theme.border
                                      Button.borderThickness 1.0
                                      Button.padding (Thickness(15.0, 9.0))
                                      Button.onClick (fun _ -> dispatch BrowseRepository) ] ] ] ] ]

    let private errorBanner (error: string) (dispatch: Msg -> unit) : IView =
        Border.create
            [ Border.background Theme.dangerDark
              Border.borderBrush Theme.danger
              Border.borderThickness 1.0
              Border.cornerRadius Theme.radius
              Border.padding 12.0
              Border.child (
                  Grid.create
                      [ Grid.columnDefinitions "*,Auto"
                        Grid.columnSpacing 12.0
                        Grid.children
                            [ TextBlock.create
                                  [ Grid.column 0
                                    TextBlock.text error
                                    TextBlock.foreground Theme.danger
                                    TextBlock.textWrapping TextWrapping.Wrap ]
                              Button.create
                                  [ Grid.column 1
                                    Button.content "Dismiss"
                                    Button.onClick (fun _ -> dispatch ClearError) ] ] ]
              ) ]

    let private repositorySummary (inspection: RepositoryInspection option) : IView =
        match inspection with
        | None -> muted "Not inspected. FsHarness requires a committed Git baseline."
        | Some repository ->
            let branch = repository.Branch |> Option.defaultValue "detached HEAD"

            let dirty =
                if repository.IsDirty then
                    repository.DirtySummary
                else
                    "Clean source worktree"

            StackPanel.create
                [ StackPanel.spacing 3.0
                  StackPanel.children
                      [ text $"{branch} · {CommitOid.value repository.Head}" 12.0 Theme.text
                        muted $"{dirty}. Experiments use only the committed HEAD." ] ]

    let private codexSummary (report: CodexPreflight option) : IView =
        match report with
        | None -> muted "Codex CLI has not been checked."
        | Some value ->
            let luna = value.Models |> List.tryFind (fun model -> model.Id = "gpt-5.6-luna")

            let supportsMax =
                luna
                |> Option.exists (fun model -> model.SupportedReasoningEfforts |> List.contains ReasoningEffort.Max)

            let capability =
                if supportsMax then
                    "Luna / Max available"
                else
                    "Luna / Max unavailable"

            text
                $"{value.Version} | {value.LoginStatus} | {capability} | write policy: {CodexWritePolicy.label value.WritePolicy}"
                12.0
                Theme.text

    let private setupView (model: Model) (dispatch: Msg -> unit) : IView =
        let canLoadExperiment = not model.Busy && model.Prepared.IsNone && model.Run.IsNone
        let canSaveExperiment = not model.Busy && model.Repository.IsSome

        let directionLabel =
            match model.Draft.MetricDirection with
            | Maximize -> "Direction: maximize"
            | Minimize -> "Direction: minimize"

        let promotionLabel =
            match model.Draft.PromotionMode with
            | AutoWhenStrictlyBetter -> "Promotion: automatic strict winners"
            | ReviewStrictWinners -> "Promotion: review strict winners"

        ScrollViewer.create
            [ ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Auto
              ScrollViewer.content (
                  StackPanel.create
                      [ StackPanel.spacing 16.0
                        StackPanel.margin (Thickness 24.0)
                        StackPanel.children
                            [ Grid.create
                                  [ Grid.columnDefinitions "*,Auto"
                                    Grid.columnSpacing 12.0
                                    Grid.children
                                        [ heading "Configure the ratchet"
                                          StackPanel.create
                                              [ Grid.column 1
                                                StackPanel.orientation Orientation.Horizontal
                                                StackPanel.spacing 8.0
                                                StackPanel.children
                                                    [ secondaryButton
                                                          "Load experiment…"
                                                          canLoadExperiment
                                                          LoadExperiment
                                                          dispatch
                                                      secondaryButton
                                                          "Save experiment…"
                                                          canSaveExperiment
                                                          SaveExperiment
                                                          dispatch ] ] ] ]
                              muted
                                  "Pin one committed baseline, one deterministic evaluator, and one bounded Codex profile. The source repository is never modified."
                              match model.ExperimentFile with
                              | Some path -> muted $"Experiment file: {path}"
                              | None -> muted "Experiment configuration has not been saved."
                              card
                                  [ overline "1 · REPOSITORY"
                                    repositoryField model.Draft.SourcePath (not model.Busy) dispatch
                                    repositorySummary model.Repository
                                    secondaryButton "Inspect committed HEAD" (not model.Busy) InspectRepository dispatch ]
                              card
                                  [ overline "2 - STORAGE"
                                    dataRootField model.DataRoot (not model.Busy) dispatch
                                    muted
                                        "Private Git repositories, SQLite state, artifacts, and run worktrees are stored below this directory."
                                    muted
                                        "The selected root is used when you prepare the next private run. Active or prepared runs must be stopped first." ]
                              card
                                  [ overline "3 - EXPERIMENT CONTRACT"
                                    multilineField "Objective" model.Draft.Objective DraftField.Objective 84.0 dispatch
                                    field
                                        "Editable path globs (semicolon-separated)"
                                        model.Draft.EditablePaths
                                        DraftField.EditablePaths
                                        dispatch
                                    field
                                        "Evaluator executable"
                                        model.Draft.EvaluatorExecutable
                                        DraftField.EvaluatorExecutable
                                        dispatch
                                    multilineField
                                        "Evaluator arguments (one argument per line)"
                                        model.Draft.EvaluatorArguments
                                        DraftField.EvaluatorArguments
                                        120.0
                                        dispatch
                                    field
                                        "Evaluator working directory"
                                        model.Draft.EvaluatorWorkingDirectory
                                        DraftField.EvaluatorWorkingDirectory
                                        dispatch
                                    field
                                        "Required constraints (semicolon-separated)"
                                        model.Draft.RequiredConstraints
                                        DraftField.RequiredConstraints
                                        dispatch ]
                              card
                                  [ overline "4 - METRIC AND AGENT"
                                    Grid.create
                                        [ Grid.columnDefinitions "*,*"
                                          Grid.columnSpacing 12.0
                                          Grid.children
                                              [ field
                                                    "Primary metric"
                                                    model.Draft.MetricName
                                                    DraftField.MetricName
                                                    dispatch
                                                Border.create
                                                    [ Grid.column 1
                                                      Border.child (
                                                          field
                                                              "Strict minimum delta"
                                                              model.Draft.MinDelta
                                                              DraftField.MinDelta
                                                              dispatch
                                                      ) ] ] ]
                                    StackPanel.create
                                        [ StackPanel.orientation Orientation.Horizontal
                                          StackPanel.spacing 8.0
                                          StackPanel.children
                                              [ secondaryButton directionLabel true ToggleMetricDirection dispatch
                                                secondaryButton promotionLabel true TogglePromotionMode dispatch ] ]
                                    field "Model ID" model.Draft.ModelId DraftField.ModelId dispatch
                                    codexSummary model.CodexHealth
                                    secondaryButton
                                        "Check Codex, auth, and bundled models"
                                        (not model.Busy)
                                        CheckCodex
                                        dispatch ]
                              card
                                  [ overline "5 - BUDGETS AND BASELINE"
                                    Grid.create
                                        [ Grid.columnDefinitions "*,*"
                                          Grid.columnSpacing 12.0
                                          Grid.children
                                              [ field
                                                    "Maximum experiments"
                                                    model.Draft.MaxExperiments
                                                    DraftField.MaxExperiments
                                                    dispatch
                                                Border.create
                                                    [ Grid.column 1
                                                      Border.child (
                                                          field
                                                              "Raw-token budget"
                                                              model.Draft.MaxRawTokens
                                                              DraftField.MaxRawTokens
                                                              dispatch
                                                      ) ] ] ]
                                    muted
                                        "Token usage is reported at turn completion; the active turn can overshoot the remaining budget."
                                    primaryButton
                                        (if model.Busy then
                                             "Validating…"
                                         else
                                             "Prepare private run and evaluate baseline")
                                        (not model.Busy && model.Repository.IsSome)
                                        PrepareRun
                                        dispatch
                                    match model.Prepared with
                                    | None ->
                                        muted
                                            "Start remains unavailable until Git isolation, Codex health, and baseline evaluation pass."
                                    | Some prepared ->
                                        StackPanel.create
                                            [ StackPanel.spacing 8.0
                                              StackPanel.children
                                                  [ text
                                                        $"Ready · baseline score {prepared.BaselineScore}"
                                                        15.0
                                                        Theme.accent
                                                    muted $"Private run: {prepared.DataDirectory}"
                                                    primaryButton "Start measured loop" true StartRun dispatch ] ] ] ] ]
              ) ]

    let private statusText (status: RunStatus) =
        match status with
        | Ready -> "Ready"
        | Running -> "Running"
        | PauseAfterCurrent -> "Pause requested"
        | Paused reason -> $"Paused · {reason}"
        | Stopping -> "Stopping"
        | Completed reason -> $"Completed · {reason}"
        | RecoveryRequired error -> $"Recovery required · {error.Summary}"

    let private phaseText (phase: ExperimentPhase) =
        match phase with
        | PreparingWorktree -> "Prepare"
        | Generating -> "Codex"
        | SnapshotPrepared -> "Snapshot"
        | Evaluating -> "Verify"
        | Deciding -> "Decide"
        | AcceptPending -> "Persist"
        | Promoting -> "Promote"
        | AwaitingReview -> "Review"

    let private metricCard (label: string) (value: string) (detail: string) : IView =
        Border.create
            [ Border.background Theme.surface
              Border.borderBrush Theme.border
              Border.borderThickness 1.0
              Border.cornerRadius Theme.radius
              Border.padding 14.0
              Border.child (
                  StackPanel.create
                      [ StackPanel.spacing 4.0
                        StackPanel.children [ muted label; text value 21.0 Theme.text; muted detail ] ]
              ) ]

    let private runControls (run: RunState) (dispatch: Msg -> unit) : IView =
        let canPause = run.Status = Running

        let canResume =
            match run.Status with
            | Paused _ -> true
            | _ -> false

        let canStop =
            match run.Status with
            | Running
            | PauseAfterCurrent
            | Paused _
            | Ready -> true
            | _ -> false

        StackPanel.create
            [ StackPanel.orientation Orientation.Horizontal
              StackPanel.spacing 8.0
              StackPanel.children
                  [ secondaryButton "Pause after current" canPause Msg.PauseAfterCurrent dispatch
                    primaryButton "Resume" canResume ResumeRun dispatch
                    Button.create
                        [ Button.content "Stop now"
                          Button.isEnabled canStop
                          Button.background Theme.dangerDark
                          Button.foreground Theme.danger
                          Button.borderBrush Theme.danger
                          Button.onClick (fun _ -> dispatch StopNow) ] ] ]

    let private activityRows (activities: RuntimeActivity list) : IView list =
        activities
        |> List.truncate 200
        |> List.map (fun item ->
            Grid.create
                [ Grid.columnDefinitions "Auto,*"
                  Grid.columnSpacing 10.0
                  Grid.children
                      [ TextBlock.create
                            [ Grid.column 0
                              TextBlock.text (item.Timestamp.ToLocalTime().ToString("HH:mm:ss"))
                              TextBlock.foreground Theme.muted
                              TextBlock.fontSize 11.0 ]
                        TextBlock.create
                            [ Grid.column 1
                              TextBlock.text item.Message
                              TextBlock.foreground Theme.text
                              TextBlock.fontSize 12.0
                              TextBlock.textWrapping TextWrapping.Wrap ] ] ]
            :> IView)

    let private currentRunView (model: Model) (dispatch: Msg -> unit) : IView =
        match model.Run with
        | None ->
            StackPanel.create
                [ StackPanel.margin (Thickness 24.0)
                  StackPanel.spacing 12.0
                  StackPanel.children
                      [ heading "No active run"
                        muted "Prepare a run from Setup before starting the ratchet."
                        primaryButton "Open setup" true (Navigate Setup) dispatch ] ]
        | Some run ->
            let rawTokens = TokenUsage.rawTotal run.Usage
            let budget = run.Config.Budgets.MaxRawTokens

            let percent =
                if budget <= 0L then
                    0.0
                else
                    min 100.0 (float rawTokens / float budget * 100.0)

            let currentPhase =
                run.Current
                |> Option.map (fun value -> phaseText value.Phase)
                |> Option.defaultValue "Between experiments"

            ScrollViewer.create
                [ ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Auto
                  ScrollViewer.content (
                      StackPanel.create
                          [ StackPanel.margin (Thickness 24.0)
                            StackPanel.spacing 16.0
                            StackPanel.children
                                [ Grid.create
                                      [ Grid.columnDefinitions "*,Auto"
                                        Grid.columnSpacing 16.0
                                        Grid.children
                                            [ StackPanel.create
                                                  [ StackPanel.children
                                                        [ heading "Current run"
                                                          text (statusText run.Status) 13.0 Theme.accent ] ]
                                              Border.create [ Grid.column 1; Border.child (runControls run dispatch) ] ] ]
                                  Grid.create
                                      [ Grid.columnDefinitions "*,*,*,*"
                                        Grid.columnSpacing 10.0
                                        Grid.children
                                            [ metricCard
                                                  "Retained score"
                                                  (string run.FrontierScore)
                                                  run.Config.Metric.Name
                                              Border.create
                                                  [ Grid.column 1
                                                    Border.child (
                                                        metricCard
                                                            "Kept / attempted"
                                                            $"{run.AcceptedCount} / {run.Attempted}"
                                                            "strict improvements"
                                                    ) ]
                                              Border.create
                                                  [ Grid.column 2
                                                    Border.child (
                                                        metricCard "Raw tokens" (string rawTokens) $"of {budget}"
                                                    ) ]
                                              Border.create
                                                  [ Grid.column 3
                                                    Border.child (
                                                        metricCard
                                                            "Current phase"
                                                            currentPhase
                                                            (run.Config.Model.Id
                                                             + " / "
                                                             + ReasoningEffort.toConfigValue run.Config.Model.Effort)
                                                    ) ] ] ]
                                  card
                                      [ overline "TOKEN BUDGET"
                                        ProgressBar.create
                                            [ ProgressBar.minimum 0.0
                                              ProgressBar.maximum 100.0
                                              ProgressBar.value percent
                                              ProgressBar.height 10.0 ]
                                        muted
                                            $"Uncached total: {TokenUsage.uncachedTotal run.Usage}; reasoning output: {run.Usage.ReasoningOutputTokens}. Cached and reasoning tokens are displayed as subsets, never added twice." ]
                                  match run.Current with
                                  | Some active when active.Phase = AwaitingReview ->
                                      card
                                          [ overline "QUALIFIED WINNER"
                                            text
                                                "All automatic constraints passed and the primary metric strictly improved. Review can only accept or reject this already-qualified candidate."
                                                13.0
                                                Theme.text
                                            StackPanel.create
                                                [ StackPanel.orientation Orientation.Horizontal
                                                  StackPanel.spacing 8.0
                                                  StackPanel.children
                                                      [ primaryButton
                                                            "Accept winner"
                                                            (not model.Busy)
                                                            AcceptWinner
                                                            dispatch
                                                        secondaryButton
                                                            "Reject and continue"
                                                            (not model.Busy)
                                                            RejectWinner
                                                            dispatch ] ] ]
                                  | _ -> Border.create []
                                  card
                                      [ overline "ACTIVITY"
                                        if List.isEmpty model.Activities then
                                            muted
                                                "Activity appears here when the run starts. Full JSONL remains in the run artifacts."
                                        else
                                            StackPanel.create
                                                [ StackPanel.spacing 7.0
                                                  StackPanel.children (activityRows model.Activities) ] ] ] ]
                  ) ]

    let private shortCommit commit =
        commit
        |> Option.map CommitOid.value
        |> Option.map (fun value -> if value.Length > 8 then value.Substring(0, 8) else value)
        |> Option.defaultValue "not captured"

    let private outcomeBrush outcome =
        match outcome with
        | EvolutionOutcome.Accepted -> Theme.accentDark
        | EvolutionOutcome.Rejected _
        | EvolutionOutcome.Failed _ -> Theme.dangerDark
        | EvolutionOutcome.Inconclusive _ -> Theme.warning
        | EvolutionOutcome.Cancelled -> Theme.surfaceRaised
        | EvolutionOutcome.Active _ -> Theme.accentDark
        | EvolutionOutcome.Unknown _ -> Theme.surfaceRaised

    let private outcomeLabel outcome = Evolution.outcomeText outcome

    let private evolutionEdge (edge: EvolutionEdgeLayout) : IView =
        Line.create
            [ Line.startPoint (Point(edge.From.X, edge.From.Y))
              Line.endPoint (Point(edge.To.X, edge.To.Y))
              Line.stroke Theme.border
              Line.strokeThickness 2.0 ]

    let private evolutionNodeButton (layout: EvolutionNodeLayout) (selected: bool) (dispatch: Msg -> unit) : IView =
        let node = layout.Node

        let marker =
            match node.Outcome with
            | EvolutionOutcome.Accepted -> "✓"
            | EvolutionOutcome.Rejected _ -> "×"
            | EvolutionOutcome.Failed _ -> "!"
            | EvolutionOutcome.Inconclusive _ -> "?"
            | EvolutionOutcome.Cancelled -> "–"
            | EvolutionOutcome.Active _ -> "…"
            | EvolutionOutcome.Unknown _ -> "·"

        Button.create
            [ Canvas.left layout.X
              Canvas.top layout.Y
              Button.width layout.Width
              Button.height layout.Height
              Button.content $"{marker} {node.Label}\n{shortCommit node.Commit}"
              Button.background (
                  if selected then
                      Theme.accentDark
                  else
                      outcomeBrush node.Outcome
              )
              Button.foreground Theme.text
              Button.borderBrush (if selected then Theme.accent else Theme.border)
              Button.borderThickness (if selected then 2.0 else 1.0)
              Button.padding (Thickness(8.0, 5.0))
              Button.onClick (fun _ -> dispatch (SelectEvolutionNode node.Id)) ]

    let private scoreLine (left: EvolutionScoreLayout) (right: EvolutionScoreLayout) : IView option =
        match left.RetainedY, right.RetainedY with
        | Some leftY, Some rightY ->
            Some(
                Line.create
                    [ Line.startPoint (Point(left.X, leftY))
                      Line.endPoint (Point(right.X, rightY))
                      Line.stroke Theme.accent
                      Line.strokeThickness 2.0 ]
            )
        | _ -> None

    let private scorePointButton (point: EvolutionScoreLayout) (selected: bool) (dispatch: Msg -> unit) : IView option =
        match point.MetricY with
        | None -> None
        | Some y ->
            Some(
                Button.create
                    [ Canvas.left (point.X - 10.0)
                      Canvas.top (y - 10.0)
                      Button.width 20.0
                      Button.height 20.0
                      Button.content "●"
                      Button.fontSize 14.0
                      Button.padding 0.0
                      Button.background (if selected then Theme.accentDark else Theme.surfaceRaised)
                      Button.foreground Theme.accent
                      Button.borderBrush (if selected then Theme.accent else Theme.border)
                      Button.borderThickness 1.0
                      Button.onClick (fun _ -> dispatch (SelectEvolutionNode point.NodeId)) ]
            )

    let private evolutionGraphView
        (snapshot: EvolutionSnapshot)
        (selected: EvolutionNodeId option)
        (dispatch: Msg -> unit)
        : IView =
        let layout = EvolutionLayout.build snapshot

        let graphChildren =
            (layout.Edges |> List.map evolutionEdge)
            @ (layout.Nodes
               |> List.map (fun item -> evolutionNodeButton item (selected = Some item.Node.Id) dispatch))

        let chartLines =
            layout.Scores
            |> List.pairwise
            |> List.choose (fun pair -> scoreLine (fst pair) (snd pair))

        let chartPoints =
            layout.Scores
            |> List.choose (fun point -> scorePointButton point (selected = Some point.NodeId) dispatch)

        let axisLabels =
            [ match layout.AxisMaximum with
              | Some value ->
                  TextBlock.create
                      [ Canvas.left 0.0
                        Canvas.top 6.0
                        TextBlock.text (string value)
                        TextBlock.foreground Theme.muted
                        TextBlock.fontSize 10.0 ]
                  :> IView
              | None -> TextBlock.create [] :> IView
              match layout.AxisMinimum with
              | Some value ->
                  TextBlock.create
                      [ Canvas.left 0.0
                        Canvas.top 174.0
                        TextBlock.text (string value)
                        TextBlock.foreground Theme.muted
                        TextBlock.fontSize 10.0 ]
                  :> IView
              | None -> TextBlock.create [] :> IView ]

        let directionText =
            if snapshot.Run.Direction = Maximize then
                "higher is better"
            else
                "lower is better"

        StackPanel.create
            [ StackPanel.spacing 8.0
              StackPanel.children
                  [ overline "LINEAGE"
                    ScrollViewer.create
                        [ ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                          ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Disabled
                          ScrollViewer.content (
                              Canvas.create
                                  [ Canvas.width layout.Width
                                    Canvas.height layout.GraphHeight
                                    Canvas.children graphChildren ]
                          ) ]
                    overline "METRIC EVOLUTION"
                    muted $"{snapshot.Run.MetricName} · {directionText}"
                    ScrollViewer.create
                        [ ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Auto
                          ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Disabled
                          ScrollViewer.content (
                              Canvas.create
                                  [ Canvas.width layout.Width
                                    Canvas.height layout.ChartHeight
                                    Canvas.children (axisLabels @ chartLines @ chartPoints) ]
                          ) ] ] ]

    let private evolutionNodeForId (snapshot: EvolutionSnapshot) nodeId =
        match nodeId with
        | ExperimentNode experimentId ->
            snapshot.Nodes
            |> List.tryFind (fun node -> node.Id = ExperimentNode experimentId)
        | BaselineNode ->
            Some
                { Id = BaselineNode
                  Kind = EvolutionNodeKind.Baseline
                  Sequence = 0
                  Parent = None
                  Commit = snapshot.Run.BaselineCommit
                  Outcome = EvolutionOutcome.Unknown "Baseline"
                  Metric = snapshot.Run.BaselineScore
                  RetainedScore = snapshot.Run.BaselineScore
                  Summary = None
                  EvaluationSummary = None
                  Usage = None
                  StartedAt = snapshot.Run.CreatedAt
                  UpdatedAt = snapshot.Run.UpdatedAt
                  Label = "Baseline" }

    let private evolutionDetails (snapshot: EvolutionSnapshot) selected : IView =
        match selected |> Option.bind (evolutionNodeForId snapshot) with
        | None -> card [ overline "NODE DETAILS"; muted "Select a node to inspect its experiment." ]
        | Some node ->
            let summaryRows =
                match node.Summary with
                | None -> [ muted "No experiment summary was persisted." ]
                | Some summary ->
                    [ text summary.Hypothesis 13.0 Theme.text
                      muted summary.ChangeSummary
                      muted $"Expected effect: {summary.ExpectedEffect}"
                      if List.isEmpty summary.ValidationNotes then
                          muted "No validation notes."
                      else
                          let notes = String.concat "; " summary.ValidationNotes
                          muted $"Validation: {notes}" ]

            let metricText =
                node.Metric |> Option.map string |> Option.defaultValue "not available"

            let startedText = node.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")

            card (
                [ overline "NODE DETAILS"
                  text $"{node.Label} · {outcomeLabel node.Outcome}" 15.0 Theme.text
                  muted $"Commit: {shortCommit node.Commit}"
                  muted $"Metric: {metricText}"
                  muted $"Started: {startedText}"
                  yield! summaryRows ]
            )

    let private evolutionRunSelector (model: Model) (dispatch: Msg -> unit) : IView =
        if List.isEmpty model.EvolutionRuns then
            muted "No persisted runs yet. Prepare a run from Setup to begin tracking evolution."
        else
            ScrollViewer.create
                [ ScrollViewer.maxHeight 180.0
                  ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Auto
                  ScrollViewer.horizontalScrollBarVisibility ScrollBarVisibility.Disabled
                  ScrollViewer.content (
                      WrapPanel.create
                          [ WrapPanel.orientation Orientation.Horizontal
                            WrapPanel.itemSpacing 6.0
                            WrapPanel.lineSpacing 6.0
                            WrapPanel.children (
                                model.EvolutionRuns
                                |> List.map (fun run ->
                                    let runIdText = RunId.text run.Id
                                    let runTime = run.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                                    let label = $"{runTime} · {runIdText.Substring(0, 8)} · {run.Status}"

                                    Button.create
                                        [ Button.horizontalContentAlignment HorizontalAlignment.Left
                                          Button.content label
                                          Button.background (
                                              if model.SelectedEvolutionRun = Some run.Id then
                                                  Theme.accentDark
                                              else
                                                  Theme.surfaceRaised
                                          )
                                          Button.foreground Theme.text
                                          Button.borderBrush Theme.border
                                          Button.borderThickness 1.0
                                          Button.padding (Thickness(10.0, 7.0))
                                          Button.onClick (fun _ -> dispatch (SelectEvolutionRun run.Id)) ]
                                    :> IView)
                            ) ]
                  ) ]

    let private evolutionView (model: Model) (dispatch: Msg -> unit) : IView =
        let body =
            match model.Evolution, model.EvolutionBusy with
            | None, true -> muted "Loading run evolution…"
            | None, false -> muted "Select a run to inspect its retained frontier and candidate branches."
            | Some snapshot, _ ->
                StackPanel.create
                    [ StackPanel.spacing 14.0
                      StackPanel.children
                          [ evolutionGraphView snapshot model.SelectedEvolutionNode dispatch
                            if not (List.isEmpty snapshot.Warnings) then
                                card [ overline "PARTIAL DATA"; muted (String.concat " " snapshot.Warnings) ]
                            else
                                Border.create []
                            evolutionDetails snapshot model.SelectedEvolutionNode ] ]

        ScrollViewer.create
            [ ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Auto
              ScrollViewer.content (
                  StackPanel.create
                      [ StackPanel.margin (Thickness 24.0)
                        StackPanel.spacing 14.0
                        StackPanel.children
                            [ Grid.create
                                  [ Grid.columnDefinitions "*,Auto"
                                    Grid.children
                                        [ heading "Evolution"
                                          secondaryButton "Refresh" (not model.EvolutionBusy) RefreshEvolution dispatch
                                          |> fun view -> Border.create [ Grid.column 1; Border.child view ] ] ]
                              muted
                                  "Accepted candidates extend the central retained trunk; side branches are preserved rejected or failed attempts."
                              evolutionRunSelector model dispatch
                              body ] ]
              ) ]

    let private historyView (model: Model) (dispatch: Msg -> unit) : IView =
        let rows =
            model.History
            |> List.map (fun item ->
                Grid.create
                    [ Grid.columnDefinitions "120,145,160,*"
                      Grid.columnSpacing 12.0
                      Grid.children
                          [ text (item.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")) 11.0 Theme.muted
                            TextBlock.create
                                [ Grid.column 1
                                  TextBlock.text item.Kind
                                  TextBlock.foreground Theme.accent
                                  TextBlock.fontSize 12.0 ]
                            TextBlock.create
                                [ Grid.column 2
                                  TextBlock.text (RunId.text item.RunId)
                                  TextBlock.foreground Theme.muted
                                  TextBlock.fontSize 10.0 ]
                            TextBlock.create
                                [ Grid.column 3
                                  TextBlock.text item.Payload
                                  TextBlock.foreground Theme.text
                                  TextBlock.fontSize 12.0
                                  TextBlock.textWrapping TextWrapping.Wrap ] ] ]
                :> IView)

        StackPanel.create
            [ StackPanel.margin (Thickness 24.0)
              StackPanel.spacing 14.0
              StackPanel.children
                  [ Grid.create
                        [ Grid.columnDefinitions "*,Auto"
                          Grid.children
                              [ heading "Run history"
                                Border.create
                                    [ Grid.column 1
                                      Border.child (secondaryButton "Refresh" (not model.Busy) RefreshHistory dispatch) ] ] ]
                    muted
                        "Append-only state transitions and outcomes. Candidate code lineage remains in each run's private Git refs."
                    card
                        [ if List.isEmpty rows then
                              muted "No journal events yet."
                          else
                              ScrollViewer.create
                                  [ ScrollViewer.verticalScrollBarVisibility ScrollBarVisibility.Auto
                                    ScrollViewer.content (
                                        StackPanel.create [ StackPanel.spacing 8.0; StackPanel.children rows ]
                                    ) ] ] ] ]

    let private settingsView (model: Model) : IView =
        let writePolicy =
            model.CodexHealth
            |> Option.map (fun report -> CodexWritePolicy.label report.WritePolicy)
            |> Option.defaultValue (CodexWritePolicy.label CodexWritePolicy.defaultValue + " (default)")

        StackPanel.create
            [ StackPanel.margin (Thickness 24.0)
              StackPanel.spacing 16.0
              StackPanel.children
                  [ heading "Safety and storage"
                    card
                        [ overline "NON-INTERACTIVE CODEX POLICY"
                          text
                              $"{writePolicy} | approval_policy=never | web search disabled | command network disabled"
                              13.0
                              Theme.text
                          muted
                              "Apps, hooks, subagents, goals, remote plugins, user config, and MCP servers are disabled for experiment workers. Project AGENTS.md guidance remains active." ]
                    card
                        [ overline "LOCAL DATA"
                          text $"Data root for the next run: {model.DataRoot}" 13.0 Theme.text
                          text
                              "SQLite is the run journal; private Git refs are candidate lineage; JSONL and evaluator results are artifacts."
                              13.0
                              Theme.text
                          muted
                              "FsColBERT is not enabled. Prompt memory limits are controlled by each campaign configuration." ]
                    card
                        [ overline "OPT-IN HYPOTHESIS BENCHMARK"
                          text "4 frozen F# tasks × 3 arms = 12 episodes" 13.0 Theme.text
                          muted
                              "Sol / Medium / one turn · Luna / Max / one turn · Luna / Max / external ratchet (at most three candidates)."
                          text
                              $"Nominal cap: {HypothesisBenchmark.preset.NominalTokenBudgetPerEpisode:N0} tokens and {HypothesisBenchmark.preset.NominalDurationPerEpisode.TotalMinutes:N0} minutes per episode."
                              13.0
                              Theme.text
                          text
                              $"Disclosed aggregate cap: {HypothesisBenchmark.preset.DisclosedAggregateTokenBudget:N0} tokens, plus possible active-turn overshoot."
                              13.0
                              Theme.warning
                          muted
                              "Live execution is never automatic. The only valid smoke labels are Promising, Not yet promising, or Inconclusive; the preset makes no statistical or causal claim." ] ] ]

    let private navigationButton (current: Page) (target: Page) (label: string) (dispatch: Msg -> unit) : IView =
        Button.create
            [ Button.content label
              Button.horizontalContentAlignment HorizontalAlignment.Left
              Button.padding (Thickness(15.0, 11.0))
              Button.background (
                  if current = target then
                      Theme.accentDark
                  else
                      Theme.navigation
              )
              Button.foreground (if current = target then Theme.accent else Theme.text)
              Button.borderThickness 0.0
              Button.onClick (fun _ -> dispatch (Navigate target)) ]

    let view (model: Model) (dispatch: Msg -> unit) : IView =
        let page: IView =
            match model.Page with
            | Setup -> setupView model dispatch
            | CurrentRun -> currentRunView model dispatch
            | Evolution -> evolutionView model dispatch
            | History -> historyView model dispatch
            | Settings -> settingsView model

        Grid.create
            [ Grid.background Theme.background
              Grid.columnDefinitions "216,*"
              Grid.children
                  [ Border.create
                        [ Grid.column 0
                          Border.background Theme.navigation
                          Border.borderBrush Theme.border
                          Border.borderThickness (Thickness(0.0, 0.0, 1.0, 0.0))
                          Border.padding (Thickness 14.0)
                          Border.child (
                              DockPanel.create
                                  [ DockPanel.children
                                        [ StackPanel.create
                                              [ DockPanel.dock Dock.Top
                                                StackPanel.margin (Thickness(6.0, 10.0, 6.0, 24.0))
                                                StackPanel.children
                                                    [ overline "FSHARNESS"; text "Experiment Ratchet" 17.0 Theme.text ] ]
                                          StackPanel.create
                                              [ StackPanel.spacing 5.0
                                                StackPanel.children
                                                    [ navigationButton model.Page Setup "Setup" dispatch
                                                      navigationButton model.Page CurrentRun "Current Run" dispatch
                                                      navigationButton model.Page Evolution "Evolution" dispatch
                                                      navigationButton model.Page History "History" dispatch
                                                      navigationButton model.Page Settings "Settings" dispatch ] ] ] ]
                          ) ]
                    Grid.create
                        [ Grid.column 1
                          Grid.rowDefinitions "Auto,*"
                          Grid.children
                              [ match model.Error with
                                | Some error ->
                                    Border.create
                                        [ Grid.row 0
                                          Border.margin (Thickness(24.0, 18.0, 24.0, 0.0))
                                          Border.child (errorBanner error dispatch) ]
                                | None -> Border.create [ Grid.row 0 ]
                                Border.create [ Grid.row 1; Border.child page ] ] ] ] ]
