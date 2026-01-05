#!/bin/bash
set -euo pipefail

PROJECT_NAME="Weaver"
PUBLISH_DIR="./publish"
DIST_DIR="./dist"

# Colors
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m'

show_help() {
    echo "Usage: $0 <platform1> [platform2] ..."
    echo ""
    echo "Packages existing publish outputs into archives."
    echo ""
    echo "Available platforms:"
    echo "  win-x64, win-x86, win-arm64"
    echo "  linux-x64, linux-arm64, linux-arm"
    echo "  osx-x64, osx-arm64"
    echo ""
    echo "Examples:"
    echo "  $0 win-x64"
    echo "  $0 win-x64 linux-x64"
    exit 0
}

# Help / validation
if [[ $# -eq 0 || "$1" == "-h" || "$1" == "--help" ]]; then
    show_help
fi

PLATFORMS=("$@")

mkdir -p "$DIST_DIR"

is_windows() {
    [[ "$1" == win-* ]]
}

package_platform() {
    local rid="$1"
    local src_dir="$PUBLISH_DIR/$rid"
    local base_name="${PROJECT_NAME}-${rid}"
    local output_file

    if [[ ! -d "$src_dir" ]]; then
        echo -e "${RED}✗ Missing build output: $src_dir${NC}"
        exit 1
    fi

    echo ""
    echo -e "${YELLOW}▶ Packaging $rid${NC}"

    if is_windows "$rid"; then
        output_file="$DIST_DIR/${base_name}.zip"
        (
            cd "$src_dir"
            zip -r "$output_file" . >/dev/null
        )
    else
        output_file="$DIST_DIR/${base_name}.tar.gz"
        (
            cd "$src_dir"
            tar -czf "$output_file" .
        )
    fi

    echo -e "${GREEN}✓ Created: $output_file${NC}"
}

echo -e "${BLUE}Packaging releases:${NC} ${PLATFORMS[*]}"

for rid in "${PLATFORMS[@]}"; do
    package_platform "$rid"
done

echo ""
echo -e "${GREEN}========================================${NC}"
echo -e "${GREEN}  Packaging Complete${NC}"
echo -e "${GREEN}========================================${NC}"
echo ""
ls -lh "$DIST_DIR"
