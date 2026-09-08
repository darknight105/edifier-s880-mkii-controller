param(
    [Parameter(Mandatory = $true)]
    [string]$SpeakerAddress,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',
    [switch]$SkipUsbShortcut
)

$ErrorActionPreference = 'Stop'

function ConvertTo-NormalizedSpeakerAddress {
    param([Parameter(Mandatory = $true)][string]$Value)

    $candidate = $Value.Trim()
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        throw 'SpeakerAddress is required.'
    }

    $compact = ($candidate -replace '[:-]', '').ToUpperInvariant()
    if ($compact -notmatch '^[0-9A-F]{12}$') {
        throw 'SpeakerAddress must be a 48-bit Bluetooth MAC address using hex digits with optional colon or dash separators.'
    }
    if ($compact -eq '000000000000') {
        throw 'SpeakerAddress cannot be the all-zero address.'
    }

    # The first octet is checked in printed MAC order. Bit 0 set means multicast;
    # the package always binds to a public, unicast address.
    $firstOctet = [Convert]::ToByte($compact.Substring(0, 2), 16)
    if (($firstOctet -band 1) -ne 0) {
        throw 'SpeakerAddress must be a unicast address (the first octet multicast bit must be 0).'
    }

    return (($compact -split '(.{2})' | Where-Object { $_ -ne '' }) -join ':')
}

function Get-FullPath {
    param([Parameter(Mandatory = $true)][string]$Path)
    return [IO.Path]::GetFullPath($Path)
}

function Test-StrictChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Candidate,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $candidateFull = Get-FullPath $Candidate
    $parentFull = (Get-FullPath $Parent).TrimEnd('\', '/')
    $prefix = $parentFull + [IO.Path]::DirectorySeparatorChar
    return $candidateFull.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Remove-ArtifactDirectory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ArtifactsRoot
    )

    $resolved = Get-FullPath $Path
    if (-not (Test-StrictChildPath -Candidate $resolved -Parent $ArtifactsRoot)) {
        throw "Refusing to recursively delete a path outside the repository artifacts folder: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Write-Utf8Json {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][object]$Value,
        [int]$Depth = 20
    )

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function New-PublishProfile {
    param(
        [Parameter(Mandatory = $true)][string]$TemplatePath,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$Address
    )

    $profileScript = Join-Path $PSScriptRoot 'New-SpeakerProfile.ps1'
    if (-not (Test-Path -LiteralPath $profileScript)) {
        throw "Speaker profile materializer is missing: $profileScript"
    }

    & $profileScript -TemplatePath $TemplatePath -OutputPath $OutputPath -SpeakerAddress $Address
    if ($LASTEXITCODE -ne 0) {
        throw "Speaker profile materialization failed with exit code $LASTEXITCODE"
    }

    $profile = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
    if ($null -eq $profile.boundDevice) {
        throw 'The speaker profile is missing boundDevice.'
    }
    $profile.boundDevice.address = $Address
    $profile.boundDevice.addressType = 'public'
    Write-Utf8Json -Path $OutputPath -Value $profile

    $verified = Get-Content -LiteralPath $OutputPath -Raw | ConvertFrom-Json
    if ($verified.boundDevice.address -ne $Address -or $verified.boundDevice.addressType -ne 'public') {
        throw 'The generated speaker profile did not contain the requested public address binding.'
    }
}

function Add-UsbShortcut {
    param(
        [Parameter(Mandatory = $true)][string]$OutputDirectory,
        [Parameter(Mandatory = $true)][string]$PublishedExecutable
    )

    $helperSource = Join-Path $PSScriptRoot 'Switch to USB.vbs'
    if (-not (Test-Path -LiteralPath $helperSource)) {
        throw "USB helper is missing: $helperSource"
    }

    $helperPath = Join-Path $OutputDirectory 'Switch to USB.vbs'
    Copy-Item -LiteralPath $helperSource -Destination $helperPath -Force
    $shortcutPath = Join-Path $OutputDirectory 'Switch to USB.lnk'
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($shortcutPath)
        $shortcut.TargetPath = $helperPath
        $shortcut.WorkingDirectory = $OutputDirectory
        $shortcut.IconLocation = "$PublishedExecutable,0"
        $shortcut.Description = 'Switch the verified S880 MKII speaker to USB.'
        $shortcut.Save()
    }
    finally {
        if ($null -ne $shortcut) { [Runtime.InteropServices.Marshal]::ReleaseComObject($shortcut) | Out-Null }
        if ($null -ne $shell) { [Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null }
    }

    return [ordered]@{
        shortcut = $shortcutPath
        shortcutHelper = $helperPath
    }
}

$normalizedAddress = ConvertTo-NormalizedSpeakerAddress $SpeakerAddress
$repoRoot = Get-FullPath (Join-Path $PSScriptRoot '..')
$artifactsRoot = Get-FullPath (Join-Path $repoRoot 'artifacts')
$outputDirectory = Get-FullPath (Join-Path $artifactsRoot 'portable')
$workRoot = Get-FullPath (Join-Path $artifactsRoot '.publish-work\portable')

New-Item -ItemType Directory -Force -Path $artifactsRoot | Out-Null
Remove-ArtifactDirectory -Path $workRoot -ArtifactsRoot $artifactsRoot
Remove-ArtifactDirectory -Path $outputDirectory -ArtifactsRoot $artifactsRoot

$payloadDirectory = Join-Path $workRoot 'payload'
$publishDirectory = Join-Path $workRoot 'publish'
$cliProject = Join-Path $repoRoot 'src\S880Ctl\S880Ctl.csproj'
$guiProject = Join-Path $repoRoot 'src\S880Tray\S880Tray.csproj'
$sourceProfile = Join-Path $repoRoot 'profiles\EdifierS880MK2CN.json'
$profileScript = Join-Path $PSScriptRoot 'New-SpeakerProfile.ps1'

foreach ($requiredPath in @($cliProject, $guiProject, $sourceProfile, $profileScript)) {
    if (-not (Test-Path -LiteralPath $requiredPath)) {
        throw "Required publish input is missing: $requiredPath"
    }
}

New-Item -ItemType Directory -Force -Path $payloadDirectory, $publishDirectory, $outputDirectory | Out-Null
$commonPublishArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    "-p:SpeakerAddress=$normalizedAddress",
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    '-p:RestoreIgnoreFailedSources=true',
    '-m:1',
    '--disable-build-servers'
)

try {
    & dotnet publish $cliProject @commonPublishArgs '-o' $payloadDirectory
    if ($LASTEXITCODE -ne 0) { throw "Self-contained CLI publish failed with exit code $LASTEXITCODE" }

    $backend = Join-Path $payloadDirectory 's880ctl.exe'
    if (-not (Test-Path -LiteralPath $backend)) { throw 'Self-contained CLI publish did not produce s880ctl.exe.' }

    $payloadProfile = Join-Path $payloadDirectory 'EdifierS880MK2CN.json'
    New-PublishProfile -TemplatePath $sourceProfile -OutputPath $payloadProfile -Address $normalizedAddress
    $backendHash = (Get-FileHash -LiteralPath $backend -Algorithm SHA256).Hash
    $profileHash = (Get-FileHash -LiteralPath $payloadProfile -Algorithm SHA256).Hash
    $manifest = [ordered]@{
        formatVersion = 1
        backendSha256 = $backendHash
        profileSha256 = $profileHash
    }
    Write-Utf8Json -Path (Join-Path $payloadDirectory 'payload-manifest.json') -Value $manifest -Depth 5

    & dotnet publish $guiProject @commonPublishArgs `
        '-p:EnableCompressionInSingleFile=true' `
        "-p:PortablePayloadDir=$payloadDirectory" `
        '-o' $publishDirectory
    if ($LASTEXITCODE -ne 0) { throw "Self-contained GUI publish failed with exit code $LASTEXITCODE" }

    $publishedSourceExecutable = Join-Path $publishDirectory 'S880Controller.exe'
    if (-not (Test-Path -LiteralPath $publishedSourceExecutable)) { throw 'Portable GUI publish did not produce S880Controller.exe.' }
    $publishedExecutable = Join-Path $outputDirectory 'S880Controller.exe'
    Copy-Item -LiteralPath $publishedSourceExecutable -Destination $publishedExecutable -Force

    $shortcut = [ordered]@{}
    if (-not $SkipUsbShortcut) {
        $shortcut = Add-UsbShortcut -OutputDirectory $outputDirectory -PublishedExecutable $publishedExecutable
    }

    $report = [ordered]@{
        formatVersion = 1
        speakerAddress = $normalizedAddress
        deliverableDirectory = $outputDirectory
        executable = $publishedExecutable
        executableSha256 = (Get-FileHash -LiteralPath $publishedExecutable -Algorithm SHA256).Hash
        backendSha256 = $backendHash
        profileSha256 = $profileHash
        runtime = $Runtime
        selfContained = $true
        frameworkDependent = $false
        singleFileGui = $true
        singleFileCliPayload = $true
        shortcut = $shortcut.shortcut
        shortcutHelper = $shortcut.shortcutHelper
    }
    Write-Utf8Json -Path (Join-Path $outputDirectory 'publish-report.json') -Value $report -Depth 5
    [pscustomobject]$report
}
finally {
    Remove-ArtifactDirectory -Path $workRoot -ArtifactsRoot $artifactsRoot
}
