#!/bin/sh
# LarisVMS: before install or upgrade (deb preinst, rpm %pre). Creates the larisvms service account
# the Web server, node, and proxy run as. It's shared by all three and kept on removal, like the data.
set -e

if ! getent group larisvms >/dev/null 2>&1; then
    groupadd --system larisvms
fi
if ! getent passwd larisvms >/dev/null 2>&1; then
    nologin=/usr/sbin/nologin
    [ -x "$nologin" ] || nologin=/sbin/nologin
    useradd --system --gid larisvms --home-dir /var/lib/larisvms --no-create-home \
        --shell "$nologin" --comment "LarisVMS services" larisvms
fi

# GPU devices (VAAPI, OpenVINO, CUDA) are group-owned by video or render; the node needs them.
for group in video render; do
    if getent group "$group" >/dev/null 2>&1; then
        usermod -a -G "$group" larisvms
    fi
done

exit 0
