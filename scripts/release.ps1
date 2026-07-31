# Kroste-Release: prueft den Git-Zustand, erstellt einen annotierten Tag vX.Y.Z
# und pusht ihn (loest die Release-Action aus).
# Versionsquelle: <Version> aus Directory.Build.props/csproj falls vorhanden
# (NetScanner-Stil), sonst MinVer-Stil: letzter Tag + Bump-Abfrage.
# Pure ASCII wegen Windows PowerShell 5.1 ANSI-Decoding; Aufruf bevorzugt via pwsh.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false

$version = $null
$files = @('Directory.Build.props') + (Get-ChildItem -Path . -Recurse -Depth 2 -Filter '*.csproj' | ForEach-Object FullName)
foreach ($f in $files) {
    if (Test-Path $f) {
        $content = Get-Content $f -Raw
        if ($content -match '<Version>([^<]+)</Version>') {
            $version = $Matches[1]
            break
        }
    }
}

if (-not $version) {
    $last = git describe --tags --abbrev=0 --match 'v*' 2>$null
    if (-not $last) { $last = 'v0.0.0' }
    $parts = $last.TrimStart('v').Split('.')
    $suggest = '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
    $version = Read-Host "Neue Version [$suggest]"
    if (-not $version) { $version = $suggest }
}

if ($version -notmatch '^\d+\.\d+\.\d+$') {
    Write-Error "'$version' ist keine gueltige SemVer-Version (X.Y.Z)."
    exit 1
}
$tag = "v$version"

if (git status --porcelain) {
    Write-Error 'Es gibt uncommittete Aenderungen. Erst committen.'
    exit 1
}
if (git log --branches --not --remotes --oneline) {
    Write-Error 'Es gibt ungepushte Commits. Erst pushen.'
    exit 1
}
git rev-parse $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    $answer = Read-Host "Tag $tag existiert bereits. Loeschen und neu setzen? [j/N]"
    if ($answer -ne 'j') {
        Write-Host 'Abgebrochen.'
        exit 0
    }
    git tag -d $tag
    git push origin ":refs/tags/$tag"
}

git tag -a $tag -m "Release $tag"
git push origin $tag
Write-Host "Tag $tag gepusht - die Release-Action baut jetzt die Pakete."
