param([string]$Dotnet = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'check-release.ps1')
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$destination = Join-Path $root ".temp/artifacts/package-$stamp"
& $Dotnet publish (Join-Path $root 'src/ReforgerMcp/ReforgerMcp.csproj') -c Release -r win-x64 --self-contained true -o $destination -p:UseAppHost=true -p:DebugType=None -p:DebugSymbols=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath (Join-Path $root 'configuration.example.json') -Destination $destination
Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $destination
Copy-Item -LiteralPath (Join-Path $root 'skills') -Destination $destination -Recurse
if (Test-Path -LiteralPath (Join-Path $root 'LICENSE')) {
    Copy-Item -LiteralPath (Join-Path $root 'LICENSE') -Destination $destination
}
New-Item -ItemType Directory -Path (Join-Path $destination 'docs') | Out-Null
Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -File |
    Copy-Item -Destination (Join-Path $destination 'docs')
$adapterDirectory = Join-Path $destination 'adapters/workbench/Scripts/WorkbenchGame'
New-Item -ItemType Directory -Path $adapterDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'adapters/workbench/Scripts/WorkbenchGame/RST_Runtime.c') -Destination $adapterDirectory
New-Item -ItemType Directory -Path (Join-Path $destination 'scripts') | Out-Null
Copy-Item -LiteralPath (Join-Path $root 'scripts/setup-workbench.ps1') -Destination (Join-Path $destination 'scripts')
New-Item -ItemType Directory -Path (Join-Path $destination 'THIRD-PARTY') | Out-Null
foreach ($notice in @('README.md','CSWINRT-LICENSE.txt','DOTNET-LICENSE.txt','MCP-SDK-LICENSE.txt','PAKINSPECTOR-LICENSE.txt')) {
    Copy-Item -LiteralPath (Join-Path $root "THIRD-PARTY/$notice") -Destination (Join-Path $destination 'THIRD-PARTY')
}
$runtimeConfig = Get-Content (Join-Path $destination 'ReforgerMcp.runtimeconfig.json') -Raw | ConvertFrom-Json
$runtimeVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').version
$assets = Get-Content (Join-Path $root 'src/ReforgerMcp/obj/project.assets.json') -Raw | ConvertFrom-Json
$runtimeFolder = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    $candidate = Join-Path $_ "microsoft.netcore.app.runtime.win-x64/$runtimeVersion"
    if (Test-Path (Join-Path $candidate 'THIRD-PARTY-NOTICES.TXT')) { $candidate }
}) | Select-Object -First 1
if (!$runtimeFolder) { throw 'Cannot locate the exact published runtime notices' }
Copy-Item -LiteralPath (Join-Path $runtimeFolder 'THIRD-PARTY-NOTICES.TXT') -Destination (Join-Path $destination 'THIRD-PARTY/DOTNET-NOTICES.txt')
$desktopVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').version
$desktopFolder = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    $candidate = Join-Path $_ "microsoft.windowsdesktop.app.runtime.win-x64/$desktopVersion"
    if (Test-Path (Join-Path $candidate 'LICENSE')) { $candidate }
}) | Select-Object -First 1
if (!$desktopVersion -or !$desktopFolder) { throw 'Cannot locate the exact published Windows Desktop runtime license' }
Copy-Item -LiteralPath (Join-Path $desktopFolder 'LICENSE') -Destination (Join-Path $destination 'THIRD-PARTY/WINDOWSDESKTOP-LICENSE.txt')
$aspnetVersion = ($runtimeConfig.runtimeOptions.includedFrameworks | Where-Object name -eq 'Microsoft.AspNetCore.App').version
$aspnetFolder = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    $candidate = Join-Path $_ "microsoft.aspnetcore.app.runtime.win-x64/$aspnetVersion"
    if (Test-Path (Join-Path $candidate 'LICENSE.txt')) { $candidate }
}) | Select-Object -First 1
if (!$aspnetVersion -or !$aspnetFolder) { throw 'Cannot locate the exact published ASP.NET Core runtime license' }
Copy-Item -LiteralPath (Join-Path $aspnetFolder 'LICENSE.txt') -Destination (Join-Path $destination 'THIRD-PARTY/ASPNETCORE-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $aspnetFolder 'THIRD-PARTY-NOTICES.txt') -Destination (Join-Path $destination 'THIRD-PARTY/ASPNETCORE-NOTICES.txt')
$packageFiles = @(Get-ChildItem -LiteralPath $destination -File -Recurse)
$forbidden = @($packageFiles | Where-Object {
    $_.Extension -in @('.pdb','.pak','.wav','.snd','.gpr','.gproj') -or
    ($_.Extension -eq '.c' -and $_.FullName -ne (Join-Path $destination 'adapters/workbench/Scripts/WorkbenchGame/RST_Runtime.c')) -or
    $_.Name -eq 'configuration.json' -or $_.FullName.Substring($destination.Length) -match '[\\/](\.temp|\.local|tests|evidence|store|exports)[\\/]'
})
if ($forbidden.Count) { throw "Package contains local/debug/game data: $($forbidden.FullName -join ', ')" }
$privatePrefixes = @($root, [Environment]::GetFolderPath('UserProfile')) |
    Where-Object { $_ } | ForEach-Object { $_.Replace('\','/') }
foreach ($file in $packageFiles | Where-Object Extension -In @('.json','.md','.txt','.java','.ps1')) {
    $content = (Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8).Replace('\\','/').Replace('\','/')
    foreach ($prefix in $privatePrefixes) {
        if ($content.IndexOf($prefix, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Package includes this builder's private path in $($file.FullName)"
        }
    }
}
foreach ($file in $packageFiles | Where-Object Extension -EQ '.md') {
    foreach ($match in [regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw -Encoding utf8), '\]\(([^)]+)\)')) {
        $target = $match.Groups[1].Value.Split('#')[0]
        if (!$target -or $target -match '^[a-z]+:|^/') { continue }
        if (!(Test-Path -LiteralPath (Join-Path $file.DirectoryName $target))) {
            throw "Broken package documentation link in $($file.Name): $target"
        }
    }
}
$archive = "$destination.zip"
Compress-Archive -LiteralPath $destination -DestinationPath $archive
$checksum = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath "$archive.sha256" -Value "$checksum  $(Split-Path $archive -Leaf)" -Encoding ascii
Write-Output $archive

