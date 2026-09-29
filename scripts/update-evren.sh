#!/usr/bin/env bash
# scripts/update-evren.sh
# ---------------------------------------------------------------------------
# evren-cli'ı kaynak koddan yeniden derleyip PATH'teki kurulumu günceller.
# (PowerShell sürümü: scripts/update-evren.ps1 — aynı parametreler.)
#
# Varsayılan hedef: ~/.evren-cli/bin/evren-cli (PATH'te olmalı)
#
# Kullanım:
#   bash scripts/update-evren.sh
#   bash scripts/update-evren.sh --target-dir "~/bin" --osx
#   bash scripts/update-evren.sh --clean --aot
# ---------------------------------------------------------------------------

set -euo pipefail

TARGET_DIR="${TARGET_DIR:-$HOME/.evren-cli/bin}"
RID="${RID:-}"
CLEAN=0
SELF_CONTAINED=1
AOT=0

usage() {
    cat <<EOF
Kullanim: $0 [--target-dir DIR] [--rid RID] [--clean] [--self-contained] [--no-self-contained] [--aot]

  --target-dir DIR        Guncellenecek klasor (varsayilan: ~/.evren-cli/bin)
  --rid RID               Hedef runtime id, or. win-x64, linux-x64, osx-x64/arm64
  --clean                 Yayin oncesi bin/obj/artifacts temizler
  --self-contained        (varsayilan) Bagimliliklari exe icine gomer
  --no-self-contained     Framework-bagimli cikti
  --aot                   NativeAOT ile derle
EOF
}

# --- Basit arguman ayristirma -----------------------------------------------
while [[ $# -gt 0 ]]; do
    case "$1" in
        --target-dir)     TARGET_DIR="$2"; shift 2 ;;
        --rid)            RID="$2"; shift 2 ;;
        --clean)          CLEAN=1; shift ;;
        --self-contained) SELF_CONTAINED=1; shift ;;
        --no-self-contained) SELF_CONTAINED=0; shift ;;
        --aot)            AOT=1; shift ;;
        -h|--help)        usage; exit 0 ;;
        *)                echo "Bilinmeyen arguman: $1" >&2; usage; exit 1 ;;
    esac
done

if [[ -z "$RID" ]]; then
    case "$(uname -s)-$(uname -m)" in
        Linux-x86_64)  RID="linux-x64" ;;
        Linux-aarch64) RID="linux-arm64" ;;
        Darwin-x86_64) RID="osx-x64" ;;
        Darwin-arm64)  RID="osx-arm64" ;;
        *) echo "RID otomatik tespit edilemedi; --rid ile belirtin." >&2; exit 1 ;;
    esac
fi

# Repo kokunu script konumundan bul.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
PROJECT="$REPO_ROOT/evren-cli.csproj"
STAGE_DIR="$REPO_ROOT/artifacts/publish/$RID"
EXE_NAME="evren-cli"
TARGET="$TARGET_DIR/$EXE_NAME"

echo "==> evren-cli guncelleme basliyor"
echo "    Kaynak   : $REPO_ROOT"
echo "    Hedef    : $TARGET"
echo "    Platform : $RID"

# --- Gereksinimler -----------------------------------------------------------
[[ -f "$PROJECT" ]] || { echo "HATA: Proje dosyasi yok: $PROJECT" >&2; exit 1; }
command -v dotnet >/dev/null 2>&1 || { echo "HATA: 'dotnet' bulunamadi (.NET SDK gerekli)." >&2; exit 1; }

# --- Yayin ---------------------------------------------------------------------
if [[ "$CLEAN" -eq 1 ]]; then
    echo "==> Temizleniyor (bin/obj/artifacts)..."
    rm -rf "$REPO_ROOT/bin" "$REPO_ROOT/obj" "$REPO_ROOT/artifacts"
fi

rm -rf "$STAGE_DIR"
mkdir -p "$STAGE_DIR"

PUBLISH_ARGS=(publish "$PROJECT" -c Release -r "$RID" -o "$STAGE_DIR" --nologo
              -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true)
if [[ "$SELF_CONTAINED" -eq 1 ]]; then PUBLISH_ARGS+=(--self-contained=true); else PUBLISH_ARGS+=(--self-contained=false); fi
if [[ "$AOT" -eq 0 ]]; then PUBLISH_ARGS+=(-p:PublishAot=false -p:PublishTrimmed=false); else PUBLISH_ARGS+=(-p:PublishAot=true); fi

echo "==> Publish ediliyor..."
echo "    dotnet ${PUBLISH_ARGS[*]}"
dotnet "${PUBLISH_ARGS[@]}" || { echo "HATA: dotnet publish basarisiz." >&2; exit 1; }

[[ -f "$STAGE_DIR/$EXE_NAME" ]] || [[ -f "$STAGE_DIR/$EXE_NAME.exe" ]] || { echo "HATA: yayin ciktisi yok." >&2; exit 1; }

# --- Yedekle + degistir ----------------------------------------------------------
mkdir -p "$TARGET_DIR"
if [[ -e "$TARGET" ]]; then
    cp -f "$TARGET" "$TARGET.bak"
    echo "==> Mevcut surum yedeklendi -> $TARGET.bak"
fi

BUILT=""
if [[ -f "$STAGE_DIR/$EXE_NAME" ]]; then BUILT="$STAGE_DIR/$EXE_NAME"; fi
if [[ -f "$STAGE_DIR/$EXE_NAME.exe" ]]; then BUILT="$STAGE_DIR/$EXE_NAME.exe"; fi

cp -f "$BUILT" "$TARGET"
chmod +x "$TARGET"

# --- Dogrula -----------------------------------------------------------------------
echo "==> Dogrulaniyor..."
VERSION_OUTPUT=$("$TARGET" --version 2>&1) || { echo "HATA: guncellenen binary calismadi." >&2; exit 1; }
echo ""
echo "  ✔ Guncellendi : $TARGET"
echo "  ✔ Surum       : $VERSION_OUTPUT"
echo ""

if ! command -v "$EXE_NAME" >/dev/null 2>&1; then
    echo "  ! Not: '$TARGET_DIR' PATH'te degil. Eklemek icin:"
    echo "      export PATH=\"\$HOME/.evren-cli/bin:\$PATH\""
fi
