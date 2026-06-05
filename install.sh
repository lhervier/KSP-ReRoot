#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

die() {
    echo "ERREUR: $*" >&2
    exit 1
}

require_command() {
    command -v "$1" >/dev/null 2>&1 || die "commande introuvable : $1"
}

check_kspdir() {
    if [[ -z "${KSPDIR:-}" ]]; then
        die "la variable d'environnement KSPDIR n'est pas définie (répertoire d'installation de KSP)"
    fi
    if [[ ! -d "$KSPDIR/GameData" ]]; then
        die "KSPDIR ne pointe pas vers une installation KSP valide : $KSPDIR"
    fi
}

require_command unzip
check_kspdir

ZIP_FILE="Release/ReRootMod.zip"
[[ -f "$ZIP_FILE" ]] || die "archive introuvable : $ZIP_FILE (lancez ./build.sh d'abord)"

MOD_DIR="$KSPDIR/GameData/ReRootMod"

echo "====================================="
echo "Suppression de l'installation existante"
echo "====================================="
rm -rf "$MOD_DIR"

echo
echo "====================================="
echo "Extraction du mod"
echo "====================================="
mkdir -p "$MOD_DIR"
unzip -oq "$ZIP_FILE" -d "$MOD_DIR"

echo
echo "Mod installé dans : $MOD_DIR"
echo "Exécuté le : $(date)"
