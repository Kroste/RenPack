#!/usr/bin/env bash
# Kroste-Release: prueft den Git-Zustand, erstellt einen annotierten Tag vX.Y.Z
# und pusht ihn (loest die Release-Action aus).
# Versionsquelle: <Version> aus Directory.Build.props/csproj falls vorhanden
# (NetScanner-Stil), sonst MinVer-Stil: letzter Tag + Bump-Abfrage.
set -euo pipefail

VERSION=$(grep -rhoP '(?<=<Version>)[^<]+' Directory.Build.props */*.csproj 2>/dev/null | head -1 || true)

if [ -z "$VERSION" ]; then
  LAST=$(git describe --tags --abbrev=0 --match 'v*' 2>/dev/null || echo v0.0.0)
  LAST=${LAST#v}
  IFS=. read -r MA MI PA <<< "$LAST"
  SUGGEST="${MA}.${MI}.$((PA+1))"
  read -r -p "Neue Version [${SUGGEST}]: " VERSION
  VERSION=${VERSION:-$SUGGEST}
fi

if ! [[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
  echo "FEHLER: '$VERSION' ist keine gueltige SemVer-Version (X.Y.Z)." >&2
  exit 1
fi
TAG="v${VERSION}"

if [ -n "$(git status --porcelain)" ]; then
  echo "FEHLER: Es gibt uncommittete Aenderungen. Erst committen." >&2
  exit 1
fi
if [ -n "$(git log --branches --not --remotes --oneline 2>/dev/null)" ]; then
  echo "FEHLER: Es gibt ungepushte Commits. Erst pushen." >&2
  exit 1
fi
if git rev-parse "$TAG" >/dev/null 2>&1; then
  read -r -p "Tag $TAG existiert bereits. Loeschen und neu setzen? [j/N] " answer
  if [ "${answer,,}" != "j" ]; then
    echo "Abgebrochen."
    exit 0
  fi
  git tag -d "$TAG"
  git push origin ":refs/tags/$TAG" || true
fi

git tag -a "$TAG" -m "Release $TAG"
git push origin "$TAG"
echo "Tag $TAG gepusht - die Release-Action baut jetzt die Pakete."
