# syntax=docker/dockerfile:1
#
# The LarisVMS Docker images, built from a release's own .deb packages, so a container runs exactly
# the files that were published. .github/workflows/docker.yml builds them on every published release:
#
#   KIND=web                      ghcr.io/billacuda/larisvms-web
#   KIND=proxy                    ghcr.io/billacuda/larisvms-proxy
#   KIND=node VARIANT=base        ghcr.io/billacuda/larisvms-node            (CPU, Intel, VAAPI)
#   KIND=node VARIANT=cuda        ghcr.io/billacuda/larisvms-node:<v>-cuda   (+ CUDA 12, cuDNN 9)
#   KIND=node VARIANT=tensorrt    ghcr.io/billacuda/larisvms-node:<v>-tensorrt (+ TensorRT 10)
#
# The build context holds this folder's files plus the release's larisvms-<kind>_<version>-1_amd64.deb
# and, for the cuda and tensorrt variants, cuda-provider/ (the ONNX Runtime CUDA provider taken from
# the web .deb, so a container never downloads it).

ARG DEBIAN_IMAGE=debian:13-slim@sha256:a72b4433de1cf7d6bae0c4cee74d0547f58601a643b3375f070c51600d63b6b2
ARG PYTHON_IMAGE=python:3.12-slim@sha256:5e2bdb1b0d542d5039cffb8181c65930093949194a65c365dfb1b0fe13a4fa5b

# NVIDIA's CUDA, cuDNN, and TensorRT libraries, from their pip wheels: only the .so files are kept.
# For the base variant this stage leaves an empty folder, so the final COPY works either way.
FROM ${PYTHON_IMAGE} AS nvidia
ARG VARIANT=base
COPY nvidia-requirements.txt tensorrt-requirements.txt /tmp/
RUN set -eux; \
    mkdir -p /opt/nvidia/lib; \
    if [ "$VARIANT" = cuda ] || [ "$VARIANT" = tensorrt ]; then \
        pip install --no-cache-dir --target /tmp/wheels -r /tmp/nvidia-requirements.txt; \
        if [ "$VARIANT" = tensorrt ]; then \
            pip install --no-cache-dir --target /tmp/wheels --extra-index-url https://pypi.nvidia.com -r /tmp/tensorrt-requirements.txt; \
        fi; \
        find /tmp/wheels -name '*.so*' -type f -exec cp -n {} /opt/nvidia/lib/ \; ; \
        rm -rf /tmp/wheels; \
    fi

FROM ${DEBIAN_IMAGE}
ARG KIND

# The service account, with the same ID in every image: recordings on the host keep their owner's ID,
# so a new image with another one couldn't write to them. Never change it. The package's own install
# script keeps an account that already exists. The ID is high enough not to be one of the host's accounts.
RUN groupadd --gid 8444 larisvms \
    && useradd --uid 8444 --gid larisvms --home-dir /var/lib/larisvms --no-create-home \
        --shell /usr/sbin/nologin --comment "LarisVMS services" larisvms

# The node's Intel GPU drivers (OpenVINO): Debian 13 has no intel-opencl-icd, so they come from Intel's
# GitHub releases. 26.27 covers 11th-gen (Tiger Lake), Arc, and newer, and is the last release Intel
# built on Ubuntu 24.04; legacy1 covers 5th-gen (Broadwell) to Ice Lake and Elkhart Lake, and both can
# be installed together. Intel's compiler packages install to /usr/local/lib without running ldconfig,
# and the legacy one has the same 137 MB library twice. To update, use the package versions and
# checksums the release's page lists, and check that they run on this image's glibc. This comes before
# the arguments and files that change with every release, so a rebuild on the same machine reuses it.
RUN set -eux; \
    if [ "$KIND" = node ]; then \
        apt-get update; \
        apt-get install -y --no-install-recommends ca-certificates curl; \
        mkdir /tmp/intel; \
        cd /tmp/intel; \
        neo=https://github.com/intel/compute-runtime/releases/download; \
        igc=https://github.com/intel/intel-graphics-compiler/releases/download; \
        for url in \
            "$neo/26.27.39122.11/intel-opencl-icd_26.27.39122.11-0_amd64.deb" \
            "$neo/26.27.39122.11/libigdgmm12_22.10.0_amd64.deb" \
            "$igc/v2.38.2/intel-igc-core-2_2.38.2+22051_amd64.deb" \
            "$igc/v2.38.2/intel-igc-opencl-2_2.38.2+22051_amd64.deb" \
            "$neo/24.35.30872.36/intel-opencl-icd-legacy1_24.35.30872.36_amd64.deb" \
            "$igc/igc-1.0.17537.24/intel-igc-core_1.0.17537.24_amd64.deb" \
            "$igc/igc-1.0.17537.24/intel-igc-opencl_1.0.17537.24_amd64.deb" \
        ; do \
            curl -fsSL --retry 3 -O "$url"; \
        done; \
        printf '%s  %s\n' \
            6e447a783c99fb5634df298c135a81165be07db98672df96cdf413d22f3e6ac4 intel-opencl-icd_26.27.39122.11-0_amd64.deb \
            6031a63d6e8a12ce61c14efc15f2c8e727061286e3820b8594e6d00615e04d54 libigdgmm12_22.10.0_amd64.deb \
            3dbcbe4e716d62e9bd43a4a476d724cf772b4581dbcdd096d70df382e7ccad7e intel-igc-core-2_2.38.2+22051_amd64.deb \
            e265d191590efd5491bfbbd148c144fdd40aea51e0b57f8651130d2da20b8186 intel-igc-opencl-2_2.38.2+22051_amd64.deb \
            bbe71e4f414259e06a10cde72c29a2bd78d41b2bb2f6f8463b1806797fe66e85 intel-opencl-icd-legacy1_24.35.30872.36_amd64.deb \
            c1e1ecdfe2064c047c552651cfdcdafc504f2033afafba65654338b880048b67 intel-igc-core_1.0.17537.24_amd64.deb \
            dd016400f87fa2b6a9fa9fbcca7eb4a2629174a29de679709f9bec5cede88b0e intel-igc-opencl_1.0.17537.24_amd64.deb \
            > SHA256SUMS; \
        sha256sum -c SHA256SUMS; \
        apt-get install -y --no-install-recommends ./*.deb; \
        ln -sf libopencl-clang.so.14 /usr/local/lib/libopencl-clang.so; \
        ldconfig; \
        cd /; \
        rm -rf /tmp/intel /var/lib/apt/lists/*; \
    fi

ARG VARIANT=base
ARG VERSION=unknown

LABEL org.opencontainers.image.source="https://github.com/billacuda/LarisVMS" \
      org.opencontainers.image.url="https://www.larisvms.com/" \
      org.opencontainers.image.documentation="https://www.larisvms.com/docs/installation/" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="larisvms-${KIND}" \
      org.opencontainers.image.description="LarisVMS ${KIND} (open-source video management for IP cameras)"

# The .deb and the CUDA provider are read through bind mounts of the build context, never COPY'd:
# a copied file stays in its image layer even after it's deleted.

# tini reaps the node's Vision Service and ffmpeg children, which a program running as PID 1
# otherwise leaves as zombies. curl is for HEALTHCHECK. The web image adds the MariaDB client for
# built-in MariaDB backups; the node image adds the VAAPI drivers (hardware video encoding).
RUN --mount=type=bind,target=/ctx \
    set -eux; \
    apt-get update; \
    extra=""; \
    case "$KIND" in \
        web) extra="mariadb-client" ;; \
        node) extra="intel-media-va-driver mesa-va-drivers" ;; \
    esac; \
    cp /ctx/larisvms-${KIND}_*_amd64.deb /tmp/; \
    apt-get install -y --no-install-recommends tini curl /tmp/larisvms-${KIND}_*_amd64.deb $extra; \
    rm -rf /tmp/*.deb /var/lib/apt/lists/*

COPY --from=nvidia /opt/nvidia/lib /opt/nvidia/lib
RUN --mount=type=bind,target=/ctx \
    set -eux; \
    if [ -n "$(ls -A /opt/nvidia/lib)" ]; then \
        echo /opt/nvidia/lib > /etc/ld.so.conf.d/nvidia.conf; \
        ldconfig; \
        mkdir -p /opt/larisvms/node/onnxruntime-backends/cuda; \
        cp /ctx/cuda-provider/* /opt/larisvms/node/onnxruntime-backends/cuda/; \
        chown -R larisvms:larisvms /opt/larisvms/node/onnxruntime-backends/cuda; \
    else \
        rm -rf /opt/nvidia; \
    fi

COPY entrypoint.sh /usr/local/bin/larisvms-entrypoint
RUN chmod 0755 /usr/local/bin/larisvms-entrypoint

ENV LARISVMS_CONTAINER=1 \
    LARISVMS_KIND=${KIND} \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_CLI_TELEMETRY_OPTOUT=1

# The GPU variants: where the node finds cuDNN (and TensorRT), and TensorRT on by default, with its
# built engines kept in the data volume so they survive an image update.
RUN set -eux; \
    case "$VARIANT" in \
        cuda) printf 'Vision__CudnnPath=/opt/nvidia/lib\n' > /etc/larisvms-variant.env ;; \
        tensorrt) printf 'Vision__CudnnPath=/opt/nvidia/lib\nVision__TensorRtLibPath=/opt/nvidia/lib\nVision__EnableTensorRt=true\nVision__TensorRtEngineCachePath=/var/lib/larisvms/tensorrt-cache\n' > /etc/larisvms-variant.env ;; \
        *) : > /etc/larisvms-variant.env ;; \
    esac

VOLUME ["/var/lib/larisvms", "/etc/larisvms", "/var/log/larisvms"]
EXPOSE 8444 8554 4443

HEALTHCHECK --interval=60s --timeout=10s --start-period=120s --retries=3 \
    CMD case "$LARISVMS_KIND" in \
        web) curl -fsk "https://localhost:${Kestrel__HttpsPort:-8444}/health" ;; \
        node) curl -fs "http://localhost:${LARISVMS_LIVE_PORT:-8554}/health" ;; \
        proxy) curl -fsk "https://localhost:${LARISVMS_CLIENT_PORT:-4443}/health" || curl -fs "http://localhost:${LARISVMS_CLIENT_PORT:-4443}/health" ;; \
    esac > /dev/null || exit 1

ENTRYPOINT ["/usr/bin/tini", "--", "/usr/local/bin/larisvms-entrypoint"]
