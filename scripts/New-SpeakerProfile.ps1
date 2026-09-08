param(
    [Parameter(Mandatory)]
    [string]$TemplatePath,
    [Parameter(Mandatory)]
    [string]$OutputPath,
    [AllowEmptyString()]
    [string]$SpeakerAddress = ''
)

$ErrorActionPreference = 'Stop'

if ($SpeakerAddress -ne '' -and $SpeakerAddress -notmatch '^(?:[0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$') {
    throw 'SpeakerAddress must be empty or a normalized 48-bit Bluetooth address.'
}
if ($SpeakerAddress -eq '00:00:00:00:00:00') {
    throw 'SpeakerAddress cannot be the all-zero address.'
}

$profile = Get-Content -LiteralPath $TemplatePath -Raw | ConvertFrom-Json
$profile.boundDevice.address = $SpeakerAddress

$parent = Split-Path -Parent ([IO.Path]::GetFullPath($OutputPath))
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$json = $profile | ConvertTo-Json -Depth 20
[IO.File]::WriteAllText($OutputPath, $json, [Text.UTF8Encoding]::new($false))
