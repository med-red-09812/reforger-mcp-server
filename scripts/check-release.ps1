$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
# Audit both tracked files (even if ignored) and the files a normal git add would include.
$files = @(& git -C $root ls-files --cached --others --exclude-standard) | Sort-Object -Unique
if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate public source candidates with Git.' }
$allowed = @(
    '^(AGENTS\.md|README\.md|LICENSE|\.gitignore|\.editorconfig|ReforgerMcp\.slnx)$',
    '^configuration\.example\.json$',
    '^THIRD-PARTY/[A-Za-z0-9-]+\.(md|txt)$',
    '^docs/[A-Za-z0-9-]+\.md$',
    '^scripts/[A-Za-z0-9-]+\.(ps1|mjs)$',
    '^tests/ToolActivity/(Program\.cs|ToolActivity\.csproj)$',
    '^tests/PakSearch/(Program\.cs|PakSearch\.csproj)$',
    '^tests/WorkbenchProfiles/(Program\.cs|WorkbenchProfiles\.csproj)$',
    '^src/ReforgerMcp/([A-Za-z0-9.]+\.(cs|csproj)|packages\.lock\.json|Ghidra/ReforgerBridge\.java)$',
    '^adapters/workbench/Scripts/WorkbenchGame/RST_(Runtime|Multiplayer|EditorChoices)\.c$',
    '^skills/reforger/(SKILL\.md|references/[a-z-]+\.md|agents/openai\.yaml)$'
)
$problems = [Collections.Generic.List[string]]::new()
$privatePrefix = [Environment]::GetFolderPath('UserProfile').Replace('\','/')
foreach ($relative in $files) {
    $relative = $relative.Replace('\','/')
    $path = Join-Path $root $relative
    # Deleted tracked files are not part of the proposed source tree.
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { continue }
    if (!($allowed | Where-Object { $relative -match $_ })) {
        $problems.Add("Unexpected public file (review before allowing): $relative")
        continue
    }
    if ((Get-Item -LiteralPath $path).Length -gt 2MB) { $problems.Add("Oversized source file: $relative"); continue }
    $text = Get-Content -LiteralPath $path -Raw -Encoding utf8
    $normalized = $text.Replace('\\','/').Replace('\','/')
    if ($privatePrefix -and $normalized.IndexOf($privatePrefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        $problems.Add("Builder's private home path in: $relative")
    }
    if ($text -match '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|\bgh[pousr]_[A-Za-z0-9]{30,}\b') {
        $problems.Add("Possible credential in: $relative")
    }
    if ($relative -match '\.md$') {
        foreach ($match in [regex]::Matches($text, '\]\(([^)]+)\)')) {
            $target = $match.Groups[1].Value.Split('#')[0]
            if (!$target -or $target -match '^[a-z]+:|^/') { continue }
            $linked = [IO.Path]::GetFullPath((Join-Path (Split-Path $path -Parent) $target))
            if (!(Test-Path -LiteralPath $linked)) { $problems.Add("Broken local link in ${relative}: $target") }
        }
    }
}
if ($problems.Count) { throw ($problems -join "`n") }
Write-Output "Source audit passed: $($files.Count) public candidates; local links, paths and file allowlist checked."

