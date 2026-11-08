# One-time setup: lets publish-update upload releases to GitHub by itself.
# You create a GitHub access token that can only change the SoundDeck repository, paste it here, and it's
# saved encrypted with your Windows login (DPAPI): only your Windows account on this PC can read it.
param([string]$Repo = "Cryptkeeeper/SoundDeck")
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$dir = Join-Path $env:USERPROFILE ".sounddeck-signing"
$file = Join-Path $dir "github-token.txt"

Write-Host ""
Write-Host "Connect SoundDeck publishing to GitHub ($Repo)" -ForegroundColor Cyan
Write-Host "--------------------------------------------------"
Write-Host "Your browser is opening GitHub's 'new token' page. Fill it in like this:"
Write-Host "  1. Token name: SoundDeck publish"
Write-Host "  2. Expiration: pick how long it should work (you can redo this later)"
Write-Host "  3. Repository access: 'Only select repositories' -> SoundDeck"
Write-Host "  4. Permissions -> Repository permissions -> Contents: 'Read and write'"
Write-Host "  5. Click 'Generate token' and copy it (starts with github_pat_)"
Write-Host ""
Start-Process "https://github.com/settings/personal-access-tokens/new"

$sec = Read-Host "Paste the token here and press Enter (it won't show)" -AsSecureString
$b = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
$token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($b)
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($b)
$token = $token.Trim()
if (-not $token) { Write-Host "Nothing pasted. Run this again when you have the token." -ForegroundColor Yellow; Read-Host "Press Enter to close"; exit 1 }

# Check it works and can write to the repository.
try {
    $r = Invoke-RestMethod "https://api.github.com/repos/$Repo" -Headers @{ Authorization = "Bearer $token"; 'User-Agent' = 'SoundDeck-publish'; Accept = 'application/vnd.github+json' }
    if (-not $r.permissions.push) { throw "this token can see $Repo but can't write to it. Give it Contents: Read and write." }
} catch {
    Write-Host ("That token didn't work: " + $_.Exception.Message) -ForegroundColor Red
    Read-Host "Press Enter to close"; exit 1
}

New-Item -ItemType Directory -Force $dir | Out-Null
ConvertFrom-SecureString $sec | Set-Content $file -Encoding ascii
$token = $null
Write-Host ""
Write-Host "Connected. publish-update.bat will now upload releases to GitHub by itself." -ForegroundColor Green
Write-Host "To disconnect: delete $file and delete the token on GitHub."
Read-Host "Press Enter to close"
