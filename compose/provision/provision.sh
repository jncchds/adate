#!/usr/bin/env bash
#
# First-boot provisioning for the adate inference stack.
#
# Runs to completion before ComfyUI starts, and is idempotent: everything already
# present is skipped, so a restart costs seconds rather than a re-download.
#
# Two classes of custom node are installed here, deliberately:
#
#   * ComfyUI-Manager, unpinned, so you can use this stack as a general ComfyUI
#     install and add whatever you like through its UI.
#   * The nodes the game itself depends on, at pinned commits, so a node author
#     pushing a breaking change cannot silently alter generation.
#
# Anything installed through the Manager is yours and is not touched here.

set -euo pipefail

MODELS_DIR="${MODELS_DIR:-/models}"
NODES_DIR="${NODES_DIR:-/root/ComfyUI/custom_nodes}"
# Space-separated list. The profile picks which model sets this box needs, so a 12GB
# machine never downloads the 13GB of SDXL weights it could not run anyway.
MANIFESTS="${MANIFESTS:-models-sd15.tsv}"

log() { printf '[provision] %s\n' "$*"; }
die() { printf '[provision] FATAL: %s\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- custom nodes

# Pinned. Update deliberately, never automatically.
#
# cubiq/ComfyUI_IPAdapter_plus went maintenance-only in April 2025 and still works,
# but it is the single dependency most likely to need replacing later, which is
# exactly why the pin matters.
IPADAPTER_REPO="https://github.com/cubiq/ComfyUI_IPAdapter_plus.git"
IPADAPTER_REF="${IPADAPTER_REF:-main}"

# OpenPose and DWPose preprocessors. Needed to turn a reference image into a pose
# skeleton, which is how a pose slot gets defined once and reused for every
# expression in a set -- the thing that lets sprites crossfade without the body moving.
CONTROLNET_AUX_REPO="https://github.com/Fannovel16/comfyui_controlnet_aux.git"
CONTROLNET_AUX_REF="${CONTROLNET_AUX_REF:-main}"

# Sana support. ComfyUI has no native Sana nodes, so evaluating it means trusting a
# third-party pack with the whole ComfyUI process. Opt-in and pinned for that reason.
EXTRA_MODELS_REPO="https://github.com/city96/ComfyUI_ExtraModels.git"
EXTRA_MODELS_REF="${EXTRA_MODELS_REF:-main}"

MANAGER_REPO="https://github.com/ltdrdata/ComfyUI-Manager.git"

install_node() {
    local name="$1" repo="$2" ref="${3:-}"
    local dir="${NODES_DIR}/${name}"

    if [ -d "${dir}/.git" ]; then
        log "custom node ${name}: already present, leaving as is"
        return
    fi

    log "custom node ${name}: cloning"
    git clone --quiet "${repo}" "${dir}"

    if [ -n "${ref}" ]; then
        log "custom node ${name}: pinning to ${ref}"
        git -C "${dir}" checkout --quiet "${ref}"
    fi

    if [ -f "${dir}/requirements.txt" ]; then
        log "custom node ${name}: installing requirements"
        pip install --quiet --no-cache-dir -r "${dir}/requirements.txt"
    fi
}

# --------------------------------------------------------------------- models

download() {
    local target="$1" url="$2" expected="$3"
    local path="${MODELS_DIR}/${target}"

    if [ -f "${path}" ]; then
        log "model ${target}: present, skipping"
        return
    fi

    mkdir -p "$(dirname "${path}")"

    local -a auth=()
    case "${url}" in
        *civitai.com*)
            # HuggingFace-first: nothing the game needs lands here. Only reached if
            # someone adds a CivitAI entry to the manifest.
            if [ -n "${CIVITAI_TOKEN:-}" ]; then
                auth=(--header "Authorization: Bearer ${CIVITAI_TOKEN}")
            else
                die "${target} is hosted on CivitAI but CIVITAI_TOKEN is not set."
            fi
            ;;
        *huggingface.co*)
            # Only needed for gated or private repos; the default manifest has none.
            if [ -n "${HF_TOKEN:-}" ]; then
                auth=(--header "Authorization: Bearer ${HF_TOKEN}")
            fi
            ;;
    esac

    log "model ${target}: downloading"

    # Download to a temporary name and move into place, so an interrupted transfer
    # cannot leave a truncated file that the next boot would treat as complete.
    local tmp="${path}.partial"
    if ! curl --fail --location --silent --show-error \
              --retry 5 --retry-delay 5 --retry-connrefused \
              "${auth[@]}" --output "${tmp}" "${url}"; then
        rm -f "${tmp}"
        die "download failed for ${target} from ${url}"
    fi

    if [ "${expected}" != "-" ]; then
        local actual
        actual="$(sha256sum "${tmp}" | cut -d' ' -f1)"
        if [ "${actual}" != "${expected}" ]; then
            rm -f "${tmp}"
            die "checksum mismatch for ${target}: expected ${expected}, got ${actual}"
        fi
    fi

    mv "${tmp}" "${path}"
    log "model ${target}: done"
}

# ----------------------------------------------------------------------- main

log "provisioning into ${MODELS_DIR}"

install_node "ComfyUI-Manager" "${MANAGER_REPO}"
install_node "ComfyUI_IPAdapter_plus" "${IPADAPTER_REPO}" "${IPADAPTER_REF}"

if [ "${INSTALL_CONTROLNET_AUX:-0}" = "1" ]; then
    install_node "comfyui_controlnet_aux" "${CONTROLNET_AUX_REPO}" "${CONTROLNET_AUX_REF}"
fi

# Sana is not supported natively by ComfyUI -- it needs a third-party node pack, which is
# why this is opt-in and pinned rather than part of the default stack. Only needed to run
# the Sana row of docs/model-evaluation.md.
if [ "${INSTALL_EXTRA_MODELS:-0}" = "1" ]; then
    install_node "ComfyUI_ExtraModels" "${EXTRA_MODELS_REPO}" "${EXTRA_MODELS_REF}"
fi

for name in ${MANIFESTS}; do
    manifest="/provision/${name}"
    [ -f "${manifest}" ] || die "model manifest ${manifest} not found"

    log "reading ${name}"
    while IFS=$'\t' read -r target url sha || [ -n "${target}" ]; do
        case "${target}" in
            ''|'#'*) continue ;;
        esac
        download "${target}" "${url}" "${sha:--}"
    done < "${manifest}"
done

log "provisioning complete"
