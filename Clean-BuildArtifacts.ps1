[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = "Medium")]
param(
    [switch]$IncludeFsHarness
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = [System.IO.Path]::GetFullPath($PSScriptRoot)
$excludedDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)

[void]$excludedDirectoryNames.Add(".git")
[void]$excludedDirectoryNames.Add(".fsharness")

$artifactDirectoryNames = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase
)

[void]$artifactDirectoryNames.Add("bin")
[void]$artifactDirectoryNames.Add("obj")

$pending = [System.Collections.Generic.Stack[System.IO.DirectoryInfo]]::new()
$targets = [System.Collections.Generic.List[System.IO.DirectoryInfo]]::new()

foreach ($directory in Get-ChildItem -LiteralPath $repositoryRoot -Directory -Force) {
    $pending.Push($directory)
}

if ($IncludeFsHarness) {
    $fsHarnessPath = Join-Path -Path $repositoryRoot -ChildPath ".fsharness"

    if (Test-Path -LiteralPath $fsHarnessPath -PathType Container) {
        $fsHarnessDirectory = Get-Item -LiteralPath $fsHarnessPath -Force

        if (($fsHarnessDirectory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -eq 0) {
            $targets.Add($fsHarnessDirectory)
        }
    }
}

while ($pending.Count -gt 0) {
    $directory = $pending.Pop()

    if (($directory.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        continue
    }

    if ($excludedDirectoryNames.Contains($directory.Name)) {
        continue
    }

    if ($artifactDirectoryNames.Contains($directory.Name)) {
        $targets.Add($directory)
        continue
    }

    foreach ($child in Get-ChildItem -LiteralPath $directory.FullName -Directory -Force) {
        $pending.Push($child)
    }
}

if ($targets.Count -eq 0) {
    Write-Host "No cleanup directories were found under '$repositoryRoot'."
    exit 0
}

Write-Host "Found $($targets.Count) cleanup directories under '$repositoryRoot'."

$removedCount = 0
$failures = [System.Collections.Generic.List[string]]::new()

foreach ($target in $targets | Sort-Object -Property FullName) {
    if ($PSCmdlet.ShouldProcess($target.FullName, "Remove transport cleanup directory")) {
        try {
            Remove-Item -LiteralPath $target.FullName -Recurse -Force
            $removedCount++
        }
        catch {
            $failures.Add("$($target.FullName): $($_.Exception.Message)")
        }
    }
}

if ($failures.Count -gt 0) {
    $details = $failures -join [System.Environment]::NewLine
    throw "Failed to remove $($failures.Count) cleanup directories:$([System.Environment]::NewLine)$details"
}

if ($WhatIfPreference) {
    Write-Host "Preview complete; no directories were removed."
}
else {
    Write-Host "Removed $removedCount cleanup directories."
}
