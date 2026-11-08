# Uploads SoundDeck's source code to GitHub as one commit, using the connection from connect-github.bat.
# Only source files go up: built files, downloads (RNNoise, VB-CABLE) and per-PC files are left out
# (same list as .gitignore). The signing key and GitHub token live outside this folder and never go up.
param(
    [string]$Message = "Update SoundDeck source",
    [string]$Repo = "Cryptkeeeper/SoundDeck",
    [string]$Branch = "main"
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($Message)) { $Message = "Update SoundDeck source" }
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$root = Split-Path $PSScriptRoot -Parent
$tokenFile = Join-Path $env:USERPROFILE ".sounddeck-signing\github-token.txt"
if (-not (Test-Path $tokenFile)) { throw "GitHub isn't connected yet. Double-click connect-github.bat first." }
$sec = Get-Content $tokenFile | ConvertTo-SecureString
$b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
$token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b)
$h = @{ Authorization = "Bearer $token"; 'User-Agent' = 'SoundDeck-publish'; Accept = 'application/vnd.github+json' }
$api = "https://api.github.com/repos/$Repo"

# Which files: everything except built/downloaded/per-PC files.
$skip = '^(dist|VBCABLE|StreamDeckPlugin\\build|Installer\\stage)\\|\.(exe|dll|log)$|sounddeck_path\.txt$'
$files = Get-ChildItem $root -Recurse -File -Force | Where-Object { $_.FullName.Substring($root.Length + 1) -notmatch $skip }

# Build one commit on top of the current branch (keeps files already on GitHub, like the README).
$ref = Invoke-RestMethod "$api/git/ref/heads/$Branch" -Headers $h
$parent = $ref.object.sha
$baseTree = (Invoke-RestMethod "$api/git/commits/$parent" -Headers $h).tree.sha
$tree = @()
foreach ($f in $files) {
    $path = $f.FullName.Substring($root.Length + 1).Replace('\', '/')
    $blob = Invoke-RestMethod -Method Post "$api/git/blobs" -Headers $h -ContentType 'application/json' `
        -Body (@{ content = [Convert]::ToBase64String([IO.File]::ReadAllBytes($f.FullName)); encoding = 'base64' } | ConvertTo-Json)
    $tree += @{ path = $path; mode = '100644'; type = 'blob'; sha = $blob.sha }
    Write-Host "  $path"
}
$newTree = Invoke-RestMethod -Method Post "$api/git/trees" -Headers $h -ContentType 'application/json' -Body (@{ base_tree = $baseTree; tree = $tree } | ConvertTo-Json -Depth 5)
if ($newTree.sha -eq $baseTree) { Write-Host "Nothing changed since the last upload." -ForegroundColor Yellow; return }
$commit = Invoke-RestMethod -Method Post "$api/git/commits" -Headers $h -ContentType 'application/json' `
    -Body (@{ message = $Message; tree = $newTree.sha; parents = @($parent) } | ConvertTo-Json)
Invoke-RestMethod -Method Patch "$api/git/refs/heads/$Branch" -Headers $h -ContentType 'application/json' -Body (@{ sha = $commit.sha } | ConvertTo-Json) | Out-Null
Write-Host ("Uploaded {0} files: https://github.com/{1}/commit/{2}" -f $files.Count, $Repo, $commit.sha) -ForegroundColor Green
