param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "",
    [string]$InstallerPlatform = "",
    [switch]$SkipPublish,
    [switch]$SkipZip,
    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ProjectPath = Join-Path $RepoRoot "AvaPlayer\AvaPlayer.csproj"
$BuildPropsPath = Join-Path $RepoRoot "Directory.Build.props"
$WixProjPath = Join-Path $RepoRoot "scripts\windows\AvaPlayer.wixproj"
$WindowsTargetFramework = "net10.0-windows10.0.19041.0"

if (-not $Version) {
    [xml]$BuildPropsXml = Get-Content -LiteralPath $BuildPropsPath
    $versionNode = $BuildPropsXml.Project.PropertyGroup.Version | Select-Object -First 1
    if ($null -ne $versionNode) {
        $Version = $versionNode.InnerText.Trim()
    }
}

if (-not $Version) {
    throw "Version is not defined. Pass -Version or define it in Directory.Build.props."
}

if (-not $InstallerPlatform) {
    $InstallerPlatform = switch ($Rid) {
        "win-x64" { "x64" }
        "win-arm64" { "arm64" }
        default { throw "Unsupported Windows RID for MSI packaging: $Rid" }
    }
}

$ArtifactRoot = Join-Path $RepoRoot "artifacts\package\$Rid\$Version"
$PublishDir = Join-Path $ArtifactRoot "publish"
$ZipPath = Join-Path $ArtifactRoot "AvaPlayer-$Version-$Rid.zip"
$MsiPath = Join-Path $ArtifactRoot "AvaPlayer-$Version-$Rid.msi"

function Invoke-Step {
    param(
        [string]$FilePath,
        [string[]]$Arguments
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed: $FilePath $($Arguments -join ' ')"
    }
}

if (-not $SkipPublish) {
    if (Test-Path -LiteralPath $PublishDir) {
        Remove-Item -LiteralPath $PublishDir -Recurse -Force
    }

    New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null

    Invoke-Step dotnet @(
        "restore",
        $ProjectPath,
        "-r", $Rid,
        "-p:EnableWindowsTargeting=true",
        "-p:TargetFramework=$WindowsTargetFramework"
    )

    Invoke-Step dotnet @(
        "publish",
        $ProjectPath,
        "-c", $Configuration,
        "-r", $Rid,
        "--self-contained", "true",
        "-p:EnableWindowsTargeting=true",
        "-p:TargetFramework=$WindowsTargetFramework",
        "-o", $PublishDir
    )
}

# Remove debug symbol files (.pdb) from publish output before packaging
if (Test-Path -LiteralPath $PublishDir) {
    Get-ChildItem -Path $PublishDir -Recurse -Filter '*.pdb' -File | Remove-Item -Force
}

New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force
}

if (-not $SkipMsi) {
    if (-not (Test-Path -LiteralPath $WixProjPath)) {
        Write-Warning "WiX project not found at $WixProjPath. Skipping MSI build."
    }
    else {
        Write-Host "Building MSI with WiX SDK (wixproj)..."
        $wixBuildDir = Join-Path $ArtifactRoot "wix-build"

        Invoke-Step dotnet @(
            "build",
            $WixProjPath,
            "-c", "Release",
            "-p:AppPublishDir=$PublishDir",
            "-p:AppVersion=$Version",
            "-p:InstallerPlatform=$InstallerPlatform",
            "-p:OutputPath=$wixBuildDir"
        )

        $builtMsi = Get-ChildItem -Path $wixBuildDir -Filter "*.msi" -Recurse | Select-Object -First 1
        if ($null -eq $builtMsi) {
            throw "MSI was not found in WiX build output: $wixBuildDir"
        }

        Copy-Item -LiteralPath $builtMsi.FullName -Destination $MsiPath -Force
        Remove-Item -LiteralPath $wixBuildDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "MSI built: $MsiPath"
    }
}

Write-Host ""
Write-Host "Artifacts written to: $ArtifactRoot"
if (Test-Path -LiteralPath $ZipPath) {
    Write-Host "  ZIP : $ZipPath"
}
if (Test-Path -LiteralPath $MsiPath) {
    Write-Host "  MSI : $MsiPath"
}
