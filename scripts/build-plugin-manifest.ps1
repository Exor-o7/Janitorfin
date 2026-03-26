param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Checksum,

    [Parameter(Mandatory = $true)]
    [string]$Timestamp,

    [Parameter(Mandatory = $true)]
    [string]$SourceUrl,

    [string]$OutputPath = "manifest.json",

    [string]$RepositoryName = "Janitorfin",

    [string]$RepositoryUrl = "https://raw.githubusercontent.com/Exor-o7/Janitorfin/main/manifest.json",

    [string]$Changelog = "",

    [string]$ChangelogPath = ""
)

$resolvedChangelog = $Changelog
if (-not [string]::IsNullOrWhiteSpace($ChangelogPath) -and (Test-Path $ChangelogPath)) {
    $resolvedChangelog = Get-Content -Raw -Path $ChangelogPath
}

$manifest = @(
    [ordered]@{
        name = "Janitorfin"
        description = "A Jellyfin-native cleanup plugin for automatically finding stale media, staging it for review, and eventually deleting it once it still matches your retention rules after a grace period."
        overview = "Automated media cleanup for Jellyfin with staged deletion and review."
        owner = "Exor.dev"
        category = "Administration"
        guid = "8eab83a3-4377-4036-8b37-68bc4767bc9e"
        versions = @(
            [ordered]@{
                version = $Version
                changelog = $resolvedChangelog
                targetAbi = "10.11.0.0"
                sourceUrl = $SourceUrl
                checksum = $Checksum.ToLowerInvariant()
                timestamp = $Timestamp
                repositoryName = $RepositoryName
                repositoryUrl = $RepositoryUrl
            }
        )
    }
)

$json = ConvertTo-Json -InputObject $manifest -Depth 10
$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

[System.IO.File]::WriteAllText($OutputPath, $json + [Environment]::NewLine, [System.Text.UTF8Encoding]::new($false))