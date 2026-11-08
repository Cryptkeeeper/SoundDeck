# Builds and signs a SoundDeck release, ready to upload to GitHub.
#   publish-update.bat 1.1.0 "What changed"
# Output: dist\release-<version>\ with 4 files. Upload all 4 to a new GitHub release tagged v<version>.
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [string]$Repo = "Cryptkeeeper/SoundDeck",
    [string]$ZipLocation = "",  # for testing only: a local folder instead of the GitHub download URL
    [switch]$NoOpen,            # don't open Explorer and the GitHub page afterwards
    [switch]$UploadOnly         # upload an already-built dist\release-<version> without rebuilding
)

# Uploads dist\release-<version> to GitHub as a release, if connect-github.bat was run. Returns $true when done.
function Publish-ToGitHub([string]$ver, [string]$notes, [string]$folder) {
    $tokenFile = Join-Path $env:USERPROFILE ".sounddeck-signing\github-token.txt"
    if (-not (Test-Path $tokenFile)) { return $false }
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    $sec = Get-Content $tokenFile | ConvertTo-SecureString
    $b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
    $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b)
    [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b)
    $h = @{ Authorization = "Bearer $token"; 'User-Agent' = 'SoundDeck-publish'; Accept = 'application/vnd.github+json' }
    $api = "https://api.github.com/repos/$Repo"

    $exists = $null
    try { $exists = Invoke-RestMethod "$api/releases/tags/v$ver" -Headers $h } catch { }
    if ($exists) { throw "GitHub already has a release tagged v$ver. Use a higher version number." }

    Write-Host "Uploading release v$ver to GitHub..."
    # Create it as a draft, upload the files, then publish, so nobody ever downloads a half-uploaded release.
    $body = @{ tag_name = "v$ver"; name = "SoundDeck $ver"; body = $notes; draft = $true; prerelease = $false } | ConvertTo-Json
    $rel = Invoke-RestMethod -Method Post "$api/releases" -Headers $h -Body $body -ContentType 'application/json'
    try {
        foreach ($f in Get-ChildItem $folder -File) {
            Write-Host ("  uploading {0}" -f $f.Name)
            $up = "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=$([Uri]::EscapeDataString($f.Name))"
            Invoke-RestMethod -Method Post $up -Headers $h -InFile $f.FullName -ContentType 'application/octet-stream' | Out-Null
        }
        $pub = @{ draft = $false; make_latest = "true" } | ConvertTo-Json
        $done = Invoke-RestMethod -Method Patch "$api/releases/$($rel.id)" -Headers $h -Body $pub -ContentType 'application/json'
    }
    catch {
        # Don't leave a broken draft behind.
        try { Invoke-RestMethod -Method Delete "$api/releases/$($rel.id)" -Headers $h | Out-Null } catch { }
        throw "Upload failed, nothing was published: $($_.Exception.Message)"
    }
    Write-Host ("Published: " + $done.html_url) -ForegroundColor Green
    return $true
}

if ($UploadOnly) {
    $folder = Join-Path (Split-Path $PSScriptRoot -Parent) "dist\release-$Version"
    if (-not (Test-Path $folder)) { throw "No built release at $folder" }
    if (-not (Publish-ToGitHub $Version $Notes $folder)) { throw "GitHub isn't connected yet. Run connect-github.bat first." }
    return
}
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$keyFile = Join-Path $env:USERPROFILE ".sounddeck-signing\update-signing-key.xml"

if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3" }
if (-not (Test-Path $keyFile)) { throw "Signing key not found at $keyFile. Without it, installed copies won't accept updates." }

# 1. Version number. Installed copies only update to a higher version.
$verFile = Join-Path $root "Version.cs"
$current = ([regex]::Match((Get-Content $verFile -Raw), 'Value = "([\d.]+)"')).Groups[1].Value
if ([Version]$Version -lt [Version]$current) { throw "Version $Version is lower than the current $current" }
$src = Get-Content $verFile -Raw
$src = [regex]::Replace($src, 'Version\("[\d.]+"\)', "Version(""$Version.0"")")
$src = [regex]::Replace($src, 'Value = "[\d.]+"', "Value = ""$Version""")
[IO.File]::WriteAllText($verFile, $src, (New-Object Text.UTF8Encoding $false))
Write-Host "Version set to $Version"

# 2. Build SoundDeck, the Stream Deck plugin and the installer.
# Close SoundDeck so its exe can be rebuilt (a running exe can't be overwritten).
Get-Process SoundDeck -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep 2
cmd /c "`"$root\Installer\build-installer.bat`" < nul"
if ($LASTEXITCODE -ne 0) { throw "Build failed, nothing was published." }
# Never ship an exe that doesn't say the version it's released as (it would be offered the same update forever).
$built = (Get-Item (Join-Path $root "SoundDeck.exe")).VersionInfo.FileVersion
if ($built -ne "$Version.0") { throw "Built SoundDeck.exe reports version $built, not $Version. Nothing was published." }

# 3. Package.
$out = Join-Path $root "dist\release-$Version"
if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory $out | Out-Null
$zipName = "SoundDeck-$Version.zip"
$zip = Join-Path $out $zipName
Compress-Archive -Path (Join-Path $root "SoundDeck.exe"), (Join-Path $root "dist\plugin.zip") -DestinationPath $zip
Copy-Item (Join-Path $root "dist\SoundDeckSetup.exe") $out
$sha = (Get-FileHash $zip -Algorithm SHA256).Hash
$pluginVersion = ((Get-Content (Join-Path $root "StreamDeckPlugin\manifest.json") -Raw) | ConvertFrom-Json).Version
# Relative to the feed (".../releases/latest/download/"), so the download works whatever the release tag is called.
$zipRef = if ($ZipLocation) { Join-Path $ZipLocation $zipName } else { $zipName }
$feed = "https://github.com/$Repo/releases/latest/download/"
$lines = @("version=$Version", "zip=$zipRef", "sha256=$sha", "plugin=$pluginVersion", "feed=$feed", "notes=$($Notes -replace "`r?`n", '|')")
$text = [Text.Encoding]::UTF8.GetBytes(($lines -join "`n") + "`n")
[IO.File]::WriteAllBytes((Join-Path $out "update.txt"), $text)

# 4. Sign update.txt with the private key, then check the signature with the public key SoundDeck uses.
$rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
$rsa.FromXmlString([IO.File]::ReadAllText($keyFile))
$sig = $rsa.SignData($text, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
[IO.File]::WriteAllText((Join-Path $out "update.txt.sig"), [Convert]::ToBase64String($sig))
$pub = New-Object Security.Cryptography.RSACryptoServiceProvider
$pub.FromXmlString([IO.File]::ReadAllText((Join-Path $root "Installer\update-public-key.xml")))
if (-not $pub.VerifyData($text, $sig, [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) { throw "Signature check failed" }

Write-Host ""
Write-Host "Release $Version is ready in $out" -ForegroundColor Green
Get-ChildItem $out | ForEach-Object { Write-Host ("  {0,-28} {1,8:N0} KB" -f $_.Name, ($_.Length / 1KB)) }
if (-not $ZipLocation -and -not $NoOpen -and (Publish-ToGitHub $Version $Notes $out)) { return }
if (-not $ZipLocation -and -not $NoOpen) {
    Write-Host ""
    Write-Host "Tip: run connect-github.bat once and future releases upload by themselves." -ForegroundColor Yellow
    Write-Host ""
    Write-Host "Next: on the GitHub page that just opened, drag in all 4 files and click 'Publish release'."
    Write-Host "Keep the tag exactly v$Version."
    Start-Process explorer.exe $out
    Start-Process ("https://github.com/$Repo/releases/new?tag=v$Version&title=SoundDeck%20$Version")
}
