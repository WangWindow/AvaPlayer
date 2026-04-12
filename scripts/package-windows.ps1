param(
    [string]$Rid = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$SkipPublish,
    [switch]$SkipZip,
    [switch]$SkipExe,
    [switch]$SkipMsi
)

$ErrorActionPreference = "Stop"

$RepoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$ProjectPath = Join-Path $RepoRoot "AvaPlayer.csproj"
$IssPath = Join-Path $RepoRoot "scripts\windows\AvaPlayer.iss"
$WxsPath = Join-Path $RepoRoot "scripts\windows\AvaPlayer.wxs"

if (-not $Version) {
    [xml]$ProjectXml = Get-Content -LiteralPath $ProjectPath
    $Version = ($ProjectXml.Project.PropertyGroup | Where-Object { $_.AssemblyVersion } | Select-Object -First 1).AssemblyVersion
}

if (-not $Version) {
    $Version = "1.0.0"
}

$ArtifactRoot = Join-Path $RepoRoot "artifacts\package\$Rid\$Version"
$PublishDir = Join-Path $ArtifactRoot "publish"
$ZipPath = Join-Path $ArtifactRoot "AvaPlayer-$Version-$Rid.zip"
$ExeOutputPath = Join-Path $ArtifactRoot "AvaPlayer-$Version-$Rid-setup.exe"
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
        "-p:TargetFramework=net10.0-windows"
    )

    Invoke-Step dotnet @(
        "publish",
        $ProjectPath,
        "-c", $Configuration,
        "-r", $Rid,
        "--self-contained", "true",
        "-p:EnableWindowsTargeting=true",
        "-p:TargetFramework=net10.0-windows",
        "-o", $PublishDir
    )
}

New-Item -ItemType Directory -Path $ArtifactRoot -Force | Out-Null

if (-not $SkipZip) {
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }

    Compress-Archive -Path (Join-Path $PublishDir "*") -DestinationPath $ZipPath -Force
}

if (-not $SkipExe) {
    $iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
    if ($null -eq $iscc) {
        Write-Warning "Inno Setup not found. Install it from https://jrsoftware.org/isinfo.php, then rerun this script to build the EXE installer."
    }
    else {
        Invoke-Step $iscc.Source @(
            "/DMyAppVersion=$Version",
            "/DMyPublishDir=$PublishDir",
            "/DMyOutputDir=$ArtifactRoot",
            "/DMyRepoRoot=$RepoRoot",
            $IssPath
        )
    }
}

if (-not $SkipMsi) {
    $wix = Get-Command "wix" -ErrorAction SilentlyContinue
    if ($null -eq $wix) {
        Write-Warning "WiX CLI not found. Install it with: dotnet tool install --global wix"
    }
    else {
        Invoke-Step $wix.Source @(
            "build",
            $WxsPath,
            "-arch", "x64",
            "-d", "PublishDir=$PublishDir",
            "-d", "Version=$Version",
            "-out", $MsiPath
        )
    }
}

Write-Host ""
Write-Host "Artifacts written to: $ArtifactRoot"
if (Test-Path -LiteralPath $ZipPath) {
    Write-Host "  ZIP : $ZipPath"
}
if (Test-Path -LiteralPath $ExeOutputPath) {
    Write-Host "  EXE : $ExeOutputPath"
}
if (Test-Path -LiteralPath $MsiPath) {
    Write-Host "  MSI : $MsiPath"
}
