#!/usr/bin/env bash
set -Eeuo pipefail

source "$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)/common.sh"

configuration="$DOTNET_CONFIGURATION"
restore_args=()

while (($#)); do
    case "$1" in
        -c|--configuration)
            configuration="${2:?missing configuration}"
            shift 2
            ;;
        --no-restore)
            restore_args+=(--no-restore)
            shift
            ;;
        -h|--help)
            cat <<'USAGE'
Usage: scripts/linux/build-dotnet.sh [--configuration Debug|Release] [--no-restore]
Build Hartonomous.slnx with dotnet on Linux. No PowerShell.
USAGE
            exit 0
            ;;
        *)
            die "unknown argument: $1"
            ;;
    esac
done

require_cmd dotnet
info "Building Hartonomous.slnx ($configuration)"
dotnet build Hartonomous.slnx -c "$configuration" --nologo "${restore_args[@]}"
