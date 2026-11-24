#!/bin/sh
# Entrypoint of the LarisVMS Docker images (docker/Dockerfile). Runs as root to prepare the folders,
# then starts the service as the larisvms account, the same account the .deb and .rpm services use.
set -eu

KIND="${LARISVMS_KIND:?LARISVMS_KIND is set by the image}"

# Volumes and bind mounts arrive owned by root: hand them to the service account. Recordings can
# be large, so only their top folder is changed, not everything already in them.
for dir in /var/lib/larisvms /var/log/larisvms /etc/larisvms; do
    mkdir -p "$dir"
    chown -R larisvms:larisvms "$dir"
done
for dir in "${LARISVMS_STORAGE_ROOT:-}" "${LARISVMS_ARCHIVE_ROOT:-}"; do
    if [ -n "$dir" ]; then
        mkdir -p "$dir"
        chown larisvms:larisvms "$dir"
    fi
done

# Saved secrets (the database password, a node's registration) are encrypted with a key tied to the
# machine ID. A recreated container gets a new /etc/machine-id, so keep one in the data volume
# instead; LarisVMS reads this file before /etc/machine-id.
if [ ! -s /var/lib/larisvms/machine-id ]; then
    tr -dc 'a-f0-9' < /dev/urandom | head -c 32 > /var/lib/larisvms/machine-id
    chown larisvms:larisvms /var/lib/larisvms/machine-id
    chmod 0600 /var/lib/larisvms/machine-id
fi

# The GPU images' defaults (where cuDNN and TensorRT are, TensorRT on), unless the compose file
# sets the same variable.
while IFS='=' read -r key value; do
    [ -n "$key" ] || continue
    if [ -z "$(printenv "$key" 2>/dev/null || true)" ]; then
        export "$key=$value"
    fi
done < /etc/larisvms-variant.env

case "$KIND" in
    web) program=/opt/larisvms/web/LarisVMS.Web ;;
    node) program=/opt/larisvms/node/LarisVMS.Node ;;
    proxy) program=/opt/larisvms/proxy/LarisVMS.Proxy ;;
    *) echo "Unknown LARISVMS_KIND: $KIND" >&2; exit 1 ;;
esac

# A GPU passed in with "devices: /dev/dri" (Intel OpenVINO, VAAPI) or NVIDIA's runtime is owned by a
# group whose number comes from the host: give the service account that group too.
groups=$(id -G larisvms | tr ' ' ',')
for dev in /dev/dri/renderD* /dev/dri/card* /dev/nvidia0; do
    if [ -e "$dev" ]; then
        gid=$(stat -c %g "$dev")
        case ",$groups," in *",$gid,"*) ;; *) groups="$groups,$gid" ;; esac
    fi
done

cd "$(dirname "$program")"
exec setpriv --reuid=larisvms --regid=larisvms --groups "$groups" "$program" "$@"
