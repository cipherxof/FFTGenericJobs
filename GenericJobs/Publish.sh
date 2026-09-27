#!/usr/bin/env bash
#
# Builds and publishes a Generic (GitHub Releases) package of this Reloaded-II mod on Linux.
# Produces the same files as Reloaded-II's publish:
#   Publish/ToUpload/Generic/<PackageName><Version>.7z
#   Publish/ToUpload/Generic/<ReleaseMetadataFileName>.br
#
# The mod's game data (FFTIVC/...) is not built by dotnet and is taken from, in order:
#   --assets DIR, ./FFTIVC, the installed mod in $RELOADEDIIMODS (matched by ModId),
#   or --assets-from-release TAG
#
# Requires: dotnet, 7z, brotli, xxhsum, python3 (curl for --assets-from-release)

set -euo pipefail

usage() {
    cat <<EOF
Usage: $(basename "$0") [options]

Options:
  --assets DIR                 Directory containing the FFTIVC folder to package
  --assets-from-release TAG    Take FFTIVC from an existing GitHub release (e.g. 0.0.10)
  --no-assets                  Package without an FFTIVC folder
  --no-build                   Package the existing build in Publish/Builds/CurrentVersion
  --output DIR                 Output directory (default: Publish/ToUpload/Generic)
  -h, --help                   Show this help
EOF
}

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

## => User Config <= ##
PROJECT_PATH="GenericJobs.csproj"
PACKAGE_NAME="GenericJobs"
GITHUB_REPO="cipherxof/FFTGenericJobs"

BUILD_DIR="Publish/Builds/CurrentVersion"
OUTPUT_DIR="Publish/ToUpload/Generic"
ASSETS_DIR=""
ASSETS_RELEASE=""
NO_ASSETS=0
BUILD=1

while [[ $# -gt 0 ]]; do
    case "$1" in
        --assets) ASSETS_DIR="$(realpath "$2")"; shift 2 ;;
        --assets-from-release) ASSETS_RELEASE="$2"; shift 2 ;;
        --no-assets) NO_ASSETS=1; shift ;;
        --no-build) BUILD=0; shift ;;
        --output) OUTPUT_DIR="$2"; shift 2 ;;
        -h|--help) usage; exit 0 ;;
        *) echo "Unknown option: $1" >&2; usage >&2; exit 1 ;;
    esac
done

for tool in dotnet 7z brotli xxhsum python3; do
    command -v "$tool" >/dev/null || { echo "Missing required tool: $tool" >&2; exit 1; }
done

modconfig() { python3 -c "import json,sys; print(json.load(open('ModConfig.json', encoding='utf-8-sig'))[sys.argv[1]])" "$1"; }
MOD_ID="$(modconfig ModId)"
MOD_VERSION="$(modconfig ModVersion)"
METADATA_FILE_NAME="$(modconfig ReleaseMetadataFileName)"
ARCHIVE_NAME="${PACKAGE_NAME}${MOD_VERSION}.7z"

TEMP_DIR="$(mktemp -d)"
trap 'rm -rf "$TEMP_DIR"' EXIT

echo "Publishing $MOD_ID $MOD_VERSION"

## => Build <= ##
if [[ $BUILD -eq 1 ]]; then
    echo "Building Mod"
    rm -rf "$BUILD_DIR"
    # Reloaded.Checks.targets requires RELOADEDIIMODS; the output path is overridden anyway.
    export RELOADEDIIMODS="${RELOADEDIIMODS:-$TEMP_DIR/mods}"
    dotnet restore "$PROJECT_PATH"
    dotnet publish "$PROJECT_PATH" -c Release --self-contained false -o "$BUILD_DIR" /p:OutputPath="$TEMP_DIR/build/"
    find "$BUILD_DIR" -type f \( -name '*.exe' -o -name '*.pdb' -o -name '*.xml' \) -delete
fi

[[ -f "$BUILD_DIR/$PACKAGE_NAME.dll" ]] || { echo "No build found in $BUILD_DIR" >&2; exit 1; }

## => Assets <= ##
rm -rf "$BUILD_DIR/FFTIVC"
if [[ $NO_ASSETS -eq 0 ]]; then
    if [[ -n "$ASSETS_RELEASE" ]]; then
        echo "Downloading assets from release $ASSETS_RELEASE"
        base_url="https://github.com/$GITHUB_REPO/releases/download/$ASSETS_RELEASE"
        curl -fsSL -o "$TEMP_DIR/metadata.json.br" "$base_url/$METADATA_FILE_NAME.br"
        release_file="$(brotli -dc "$TEMP_DIR/metadata.json.br" | python3 -c "import json,sys; print(json.load(sys.stdin)['Releases'][0]['FileName'])")"
        curl -fsSL -o "$TEMP_DIR/release.7z" "$base_url/$release_file"
        7z x -y -o"$TEMP_DIR/release" "$TEMP_DIR/release.7z" 'FFTIVC/*' >/dev/null
        ASSETS_DIR="$TEMP_DIR/release"
    elif [[ -z "$ASSETS_DIR" ]]; then
        # Installed mods can live in any folder name (e.g. "GenericJobs 34 0.0.10 ..."), so match on ModId.
        candidates=("$SCRIPT_DIR")
        if [[ -n "${RELOADEDIIMODS:-}" && -d "$RELOADEDIIMODS" ]]; then
            while IFS= read -r -d '' config; do
                if grep -q "\"ModId\": *\"$MOD_ID\"" "$config"; then
                    candidates+=("$(dirname "$config")")
                fi
            done < <(find "$RELOADEDIIMODS" -mindepth 2 -maxdepth 2 -name ModConfig.json -print0)
        fi

        for candidate in "${candidates[@]}"; do
            if [[ -d "$candidate/FFTIVC" ]]; then
                ASSETS_DIR="$candidate"
                break
            fi
        done
    fi

    if [[ -z "$ASSETS_DIR" || ! -d "$ASSETS_DIR/FFTIVC" ]]; then
        echo "No FFTIVC assets found. Use --assets DIR, --assets-from-release TAG or --no-assets." >&2
        exit 1
    fi

    echo "Using assets from $ASSETS_DIR/FFTIVC"
    cp -r "$ASSETS_DIR/FFTIVC" "$BUILD_DIR/FFTIVC"
fi

## => Update Metadata <= ##
# Sewer56.Update.Metadata.json: XXH64 of every file, used by Reloaded-II to apply updates.
echo "Writing Sewer56.Update.Metadata.json"
rm -f "$BUILD_DIR/Sewer56.Update.Metadata.json"
(cd "$BUILD_DIR" && find . -type f -printf '%P\0' | sort -z | xargs -0 xxhsum -H1) > "$TEMP_DIR/hashes.txt"
python3 - "$TEMP_DIR/hashes.txt" "$BUILD_DIR/Sewer56.Update.Metadata.json" "$MOD_VERSION" "$METADATA_FILE_NAME" <<'EOF'
import json, re, sys
hashes, out, version, metadata_file_name = sys.argv[1:]
files = []
for line in open(hashes, encoding="utf-8"):
    digest, path = line.rstrip("\n").split("  ", 1)
    files.append({"RelativePath": path.replace("/", "\\"), "Hash": int(digest, 16)})
metadata = {
    "ExtraData": None,
    "Type": 0,
    "Version": version,
    "Hashes": {"Files": files},
    "IgnoreRegexes": [r".*\.json", r".*\.nuspec", re.escape(metadata_file_name)],
    "IncludeRegexes": [r"ModConfig\.json", r"\.deps\.json", r"\.runtimeconfig\.json"],
    "DeltaData": None,
}
with open(out, "w", encoding="utf-8") as f:
    json.dump(metadata, f, separators=(",", ":"))
EOF

## => Package <= ##
echo "Packaging $ARCHIVE_NAME"
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"
OUTPUT_DIR="$(realpath "$OUTPUT_DIR")"
(cd "$BUILD_DIR" && 7z a -t7z -mx=9 "$OUTPUT_DIR/$ARCHIVE_NAME" . >/dev/null)

## => Release Metadata <= ##
# <ReleaseMetadataFileName>.br: tells Reloaded-II's GitHub update resolver which asset to download.
echo "Writing $METADATA_FILE_NAME.br"
python3 - "$ARCHIVE_NAME" "$MOD_VERSION" <<'EOF' | brotli -c > "$OUTPUT_DIR/$METADATA_FILE_NAME.br"
import json, sys
archive, version = sys.argv[1:]
config = json.load(open("ModConfig.json", encoding="utf-8-sig"))
metadata = {
    "Releases": [{"ReleaseType": 0, "FileName": archive, "Version": version, "Delta": None}],
    "ExtraData": {
        "ModId": config["ModId"],
        "ModName": config["ModName"],
        "ModDescription": config["ModDescription"],
        "Changelog": None,
        "Readme": None,
    },
}
json.dump(metadata, sys.stdout, separators=(",", ":"))
EOF

echo "Done."
echo "Upload the files in \"$OUTPUT_DIR\" to a GitHub release tagged $MOD_VERSION:"
ls -l "$OUTPUT_DIR"
