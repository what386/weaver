#!/bin/bash
set -euo pipefail

PROJECT_NAME="Weaver"
CONFIGURATION="Release"
OUTPUT_DIR="./publish"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

show_help() {
    echo "Usage: $0 <platform1> [platform2] ..."
    echo ""
    echo "Available platforms:"
    echo "  win-x64, win-x86, win-arm64"
    echo "  linux-x64, linux-arm64, linux-arm"
    echo "  osx-x64, osx-arm64"
    echo ""
    echo "Examples:"
    echo "  $0 win-x64"
    echo "  $0 win-x64 linux-x64"
    echo "  $0 osx-x64 osx-arm64"
    exit 0
}

# Help / validation
if [[ $# -eq 0 || "$1" == "-h" || "$1" == "--help" ]]; then
    show_help
fi

BUILD_PLATFORMS=("$@")

echo -e "${GREEN}Building ${PROJECT_NAME}${NC}"
echo -e "${BLUE}Platforms: ${BUILD_PLATFORMS[*]}${NC}"

# Clean output root
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

get_platform_description() {
    case "$1" in
    win-x64) echo "Windows (64-bit)" ;;
    win-x86) echo "Windows (32-bit)" ;;
    win-arm64) echo "Windows ARM64" ;;
    linux-x64) echo "Linux (64-bit)" ;;
    linux-arm64) echo "Linux ARM64" ;;
    linux-arm) echo "Linux ARM" ;;
    osx-x64) echo "macOS Intel" ;;
    osx-arm64) echo "macOS Apple Silicon" ;;
    *) echo "$1" ;;
    esac
}

build_platform() {
    local rid="$1"
    local description
    description=$(get_platform_description "$rid")

    local platform_dir="$OUTPUT_DIR/$rid"

    echo ""
    echo -e "${YELLOW}▶ Building $description ($rid)${NC}"

    rm -rf "$platform_dir"
    mkdir -p "$platform_dir"

    dotnet publish "./src/$PROJECT_NAME/$PROJECT_NAME.csproj" \
        -c "$CONFIGURATION" \
        -r "$rid" \
        --self-contained \
        -p:UseAppHost=true \
        -p:PublishReadyToRun=true \
        -p:PublishSingleFile=false \
        -p:PublishTrimmed=false \
        -o "$platform_dir"

    echo -e "${GREEN}✓ Output: $platform_dir${NC}"
}

for rid in "${BUILD_PLATFORMS[@]}"; do
    build_platform "$rid"
done

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Build Complete${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
echo "Artifacts:"
ls -lh "$OUTPUT_DIR"
