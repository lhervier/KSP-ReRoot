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

detect_ksp_data_dir() {
    if [[ -z "${KSPDIR:-}" ]]; then
        die "la variable d'environnement KSPDIR n'est pas définie (répertoire d'installation de KSP)"
    fi

    if [[ -f "$KSPDIR/KSP_x64_Data/Managed/Assembly-CSharp.dll" ]]; then
        echo "Structure Windows détectée (KSP_x64_Data)"
        KSP_DATA_DIR="$KSPDIR/KSP_x64_Data"
    elif [[ -f "$KSPDIR/KSP_Data/Managed/Assembly-CSharp.dll" ]]; then
        echo "Structure Linux détectée (KSP_Data)"
        KSP_DATA_DIR="$KSPDIR/KSP_Data"
    else
        die "Assembly-CSharp.dll introuvable dans $KSPDIR/KSP_x64_Data/Managed/ ou $KSPDIR/KSP_Data/Managed/"
    fi

    echo "Utilisation de KSPDIR: $KSPDIR"
    echo "Utilisation de KSP_DATA_DIR: $KSP_DATA_DIR"
}

echo "==============================="
echo "Building ReRootMod"
echo "==============================="

require_command dotnet
require_command zip
detect_ksp_data_dir

MSBUILD_PROPS=(-p:KSPDIR="$KSPDIR" -p:KSP_DATA_DIR="$KSP_DATA_DIR")

echo "Suppression du dossier Release"
rm -rf Release

echo "Création du dossier Release"
mkdir -p Release/ReRootMod

echo "Restauration des packages NuGet"
dotnet restore ReRootMod.csproj "${MSBUILD_PROPS[@]}"

echo "Compilation de la DLL du mod (.NET Framework 4.7.2)"
dotnet build ReRootMod.csproj "${MSBUILD_PROPS[@]}" --no-restore

echo "Copie de la DLL du mod"
cp -v Output/bin/ReRootMod.dll Release/ReRootMod/

echo "Copie du patch ModuleManager"
cp -v GameData/ReRootMod/ReRootMod.cfg Release/ReRootMod/

echo "Copie de la localisation"
mkdir -p Release/ReRootMod/Localization
cp -v GameData/ReRootMod/Localization/*.cfg Release/ReRootMod/Localization/

echo "Création de l'archive"
(
    cd Release/ReRootMod
    zip -qr ../ReRootMod.zip .
)

echo "Suppression du dossier intermédiaire"
rm -rf Release/ReRootMod

echo
echo "Build terminé : Release/ReRootMod.zip"
echo "Exécuté le : $(date)"
