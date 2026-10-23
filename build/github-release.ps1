<#
    Releases EasyVPN on GitHub: tags the current commit v<version> and pushes the tag. That makes
    .github\workflows\release.yml build both installers on GitHub and publish them as a release.
    The script then follows that build and prints the release's address.

        .\build\github-release.ps1 -Version 1.2.3
        .\build\github-release.ps1 -Version 1.2.3 -NoWait    push the tag and return

    release.ps1 builds the same installers on this PC, but publishes nothing.
    Needs git and the GitHub CLI, signed in (gh auth login).
#>
param(
    # Three or four numbers: MSI compares numbers only, so 1.2.3-beta could never upgrade an install.
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(\.\d+)?$')]
    [string]$Version,
    [switch]$NoWait
)
$ErrorActionPreference = 'Stop'
$tag = "v$Version"

function Invoke-Native([string]$what, [scriptblock]$command) {
    $output = & $command
    if ($LASTEXITCODE) { throw "$what failed." }
    $output
}

Push-Location (Split-Path $PSScriptRoot)
try {
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        throw 'The GitHub CLI (gh) was not found: winget install GitHub.cli, then gh auth login.'
    }
    gh auth status *> $null
    if ($LASTEXITCODE) { throw 'The GitHub CLI is not signed in: gh auth login.' }

    # GitHub builds the commit the tag points at, so it must be exactly what is here, and pushed.
    if (Invoke-Native 'git status' { git status --porcelain }) {
        throw 'There are uncommitted changes. Commit and push them first: GitHub builds only what is pushed.'
    }
    Invoke-Native 'git fetch' { git fetch --quiet --tags origin }
    if (-not (Invoke-Native 'git branch' { git branch --remotes --contains HEAD })) {
        throw 'This commit is not on GitHub yet. Push it first (git push).'
    }
    if (git tag --list $tag) {
        throw "$tag already exists. Release a new version, or delete the tag to redo this one: git push origin :refs/tags/$tag; git tag -d $tag"
    }

    $commit = Invoke-Native 'git log' { git log -1 --format='%h %s' }
    Write-Host "Releasing $tag from $commit"
    Invoke-Native 'git tag' { git tag --annotate $tag --message "EasyVPN $Version" }
    Invoke-Native 'git push' { git push --quiet origin $tag }

    $runs = "https://github.com/$(Invoke-Native 'gh repo view' { gh repo view --json nameWithOwner --jq .nameWithOwner })/actions"
    if ($NoWait) {
        Write-Host "Pushed $tag. GitHub is building the release: $runs"
        return
    }

    # The run appears a few seconds after the push.
    $run = $null
    for ($i = 0; $i -lt 30 -and -not $run; $i++) {
        Start-Sleep -Seconds 2
        $run = (gh run list --workflow release.yml --event push --limit 20 --json databaseId,headBranch | Out-String | ConvertFrom-Json) |
            Where-Object headBranch -eq $tag | Select-Object -First 1
    }
    if (-not $run) { throw "Pushed $tag, but no release build started within a minute. Look at $runs" }

    gh run watch $run.databaseId --interval 10 --exit-status
    if ($LASTEXITCODE) {
        throw "The release build failed: gh run view $($run.databaseId) --log-failed. Once fixed and pushed, delete the tag (git push origin :refs/tags/$tag; git tag -d $tag) and run this again."
    }
    Write-Host "Released: $(Invoke-Native 'gh release view' { gh release view $tag --json url --jq .url })"
}
finally {
    Pop-Location
}
