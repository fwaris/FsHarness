namespace FsHarness.Infrastructure

open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.RegularExpressions

[<RequireQualifiedAccess>]
module DataPaths =
    let root () =
        match Environment.GetEnvironmentVariable "FSHARNESS_DATA_DIR" with
        | value when not (String.IsNullOrWhiteSpace value) -> Path.GetFullPath value
        | _ -> Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FsHarness")

    let runRoot root runId =
        Path.Combine(root, "runs", FsHarness.Core.RunId.text runId)

    let repository root runId =
        Path.Combine(runRoot root runId, "repo.git")

    let worktrees root runId =
        Path.Combine(runRoot root runId, "worktrees")

    let artifacts root runId =
        Path.Combine(runRoot root runId, "artifacts")

    let projectLock root sourcePath =
        let canonical = Path.GetFullPath(sourcePath)

        let key =
            SHA256.HashData(Encoding.UTF8.GetBytes canonical)
            |> Convert.ToHexString
            |> _.ToLowerInvariant()

        Path.Combine(root, "locks", key + ".lock")

    let ensureContained root candidate =
        let normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
        let rootPath = normalizedRoot + string Path.DirectorySeparatorChar
        let candidatePath = Path.GetFullPath candidate

        if
            candidatePath = normalizedRoot
            || candidatePath.StartsWith(rootPath, StringComparison.Ordinal)
        then
            Ok candidatePath
        else
            Error $"Path '{candidatePath}' is outside managed root '{rootPath}'."

[<RequireQualifiedAccess>]
module AtomicFile =
    let writeAllText (path: string) (contents: string) =
        let directory = Path.GetDirectoryName path
        Directory.CreateDirectory directory |> ignore

        let temporaryPath =
            Path.Combine(directory, $".{Path.GetFileName path}.{Guid.NewGuid():N}.tmp")

        File.WriteAllText(temporaryPath, contents, UTF8Encoding(false))
        File.Move(temporaryPath, path, true)

    let sha256 (path: string) =
        use stream = File.OpenRead path

        SHA256.HashData stream
        |> Convert.ToHexString
        |> fun value -> value.ToLowerInvariant()

type ProjectLock(stream: FileStream) =
    interface IDisposable with
        member _.Dispose() = stream.Dispose()

[<RequireQualifiedAccess>]
module ProjectLock =
    let tryAcquire (path: string) =
        try
            Directory.CreateDirectory(Path.GetDirectoryName path) |> ignore

            let stream =
                new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

            Ok(new ProjectLock(stream) :> IDisposable)
        with :? IOException ->
            Error "Another FsHarness process owns this run."

[<RequireQualifiedAccess>]
module PathPolicy =
    let normalize (path: string) = path.Replace('\\', '/').TrimStart('/')

    let private globRegex (pattern: string) =
        let normalized = normalize pattern
        let builder = StringBuilder("^")
        let mutable index = 0

        while index < normalized.Length do
            match normalized[index] with
            | '*' when index + 1 < normalized.Length && normalized[index + 1] = '*' ->
                builder.Append(".*") |> ignore
                index <- index + 2
            | '*' ->
                builder.Append("[^/]*") |> ignore
                index <- index + 1
            | '?' ->
                builder.Append("[^/]") |> ignore
                index <- index + 1
            | character ->
                builder.Append(Regex.Escape(string character)) |> ignore
                index <- index + 1

        builder.Append("$") |> ignore
        Regex(builder.ToString(), RegexOptions.CultureInvariant)

    let private fixedProtected path =
        path = ".git"
        || path.StartsWith(".git/", StringComparison.Ordinal)
        || path = ".gitmodules"
        || path = ".gitattributes"
        || path = ".fsharness"
        || path.StartsWith(".fsharness/", StringComparison.Ordinal)
        || Path.IsPathRooted path
        || path.Split('/') |> Array.contains ".."

    let isEditable editableGlobs path =
        let normalized = normalize path

        not (fixedProtected normalized)
        && (editableGlobs
            |> List.exists (fun pattern -> globRegex pattern |> fun regex -> regex.IsMatch normalized))

    let partition editableGlobs paths =
        paths |> List.distinct |> List.partition (isEditable editableGlobs)
