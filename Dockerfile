# syntax=docker/dockerfile:1
# Payload is prepared once from the Release Mod. Never rebuild it in this image.
FROM ubuntu:24.04

ARG TARGETARCH
ARG DEBIAN_FRONTEND=noninteractive
# Supplied by the release pipeline so the image records the version it actually contains.
ARG TURBOTICK_MOD_VERSION
RUN test "$TARGETARCH" = amd64 \
    && apt-get update \
    && apt-get install -y --no-install-recommends \
       bash ca-certificates curl tar gzip coreutils util-linux python3 \
       libc6-i386 lib32gcc-s1 lib32stdc++6 \
       libc6 libgcc-s1 libstdc++6 libicu74 libssl3t64 zlib1g libgssapi-krb5-2 liblttng-ust1t64 \
       libvulkan1 mesa-vulkan-drivers libgl1 libegl1 libglfw3 \
       libx11-6 libxext6 libxrandr2 libxinerama1 libxcursor1 libxi6 libxrender1 \
       libxkbcommon0 libwayland-client0 libasound2t64 libsdl2-2.0-0 \
       libfreetype6 fontconfig fonts-dejavu-core \
    && rm -rf /var/lib/apt/lists/* \
    && groupadd --gid 10001 turbotick \
    && useradd --uid 10001 --gid 10001 --home-dir /profile --no-create-home --shell /bin/bash turbotick \
    && mkdir -p /opt/steamcmd-seed /opt/turbotick /game /profile/.config /state /steam \
    && curl --fail --show-error --location --retry 3 \
       https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz \
       -o /tmp/steamcmd_linux.tar.gz \
    && tar -xzf /tmp/steamcmd_linux.tar.gz -C /opt/steamcmd-seed \
    && rm /tmp/steamcmd_linux.tar.gz \
    && test -x /opt/steamcmd-seed/steamcmd.sh \
    && test -x /opt/steamcmd-seed/linux32/steamcmd \
    && chown -R 10001:10001 /game /profile /state /steam

COPY artifacts/container-mod/TurboTick/ /opt/turbotick/mod/TurboTick/
COPY scripts/steamcmd/install-server.sh /opt/turbotick/install-server.sh
COPY deploy/container/entrypoint.sh deploy/container/steamcmd.sh deploy/container/container.py /opt/turbotick/
COPY LICENSE /opt/turbotick/LICENSE
ENV TURBOTICK_MOD_VERSION=${TURBOTICK_MOD_VERSION}
RUN python3 /opt/turbotick/container.py manifest /opt/turbotick/mod/TurboTick /opt/turbotick/payload.sha256.json \
    && chmod -R a=rX /opt/turbotick /opt/steamcmd-seed \
    && chmod a+rx /opt/turbotick/entrypoint.sh /opt/turbotick/steamcmd.sh /opt/turbotick/install-server.sh

LABEL org.opencontainers.image.title="TurboTick headless RimWorld host" \
      org.opencontainers.image.version="${TURBOTICK_MOD_VERSION}" \
      org.opencontainers.image.source="https://github.com/LumiDust/TurboTick" \
      org.opencontainers.image.licenses="MIT" \
      org.opencontainers.image.description="Linux amd64 native RimWorld host; game and Harmony installed separately"
ENV HOME=/profile \
    XDG_CONFIG_HOME=/profile/.config \
    TURBOTICK_HOST=0.0.0.0 \
    TURBOTICK_PORT=30572 \
    TURBOTICK_DATA=/state \
    TURBOTICK_CONTROLLER_ONLY=false \
    PYTHONDONTWRITEBYTECODE=1
WORKDIR /game
USER 10001:10001
EXPOSE 30572/tcp
STOPSIGNAL SIGTERM
HEALTHCHECK --interval=15s --timeout=5s --start-period=120s --retries=3 \
    CMD ["python3", "/opt/turbotick/container.py", "health", "/state/headless-status.json"]
ENTRYPOINT ["/opt/turbotick/entrypoint.sh"]
CMD ["run"]
