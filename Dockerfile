# syntax=docker/dockerfile:1

# SweetHide web server (src/SweetHide.Server) for linux/amd64 and linux/arm64. See docs/SERVER.md.
#
#   docker build -t sweethide .
#   docker buildx build --platform linux/amd64,linux/arm64 -t sweethide .
#
# The build stage runs on the build machine's own architecture and cross-publishes for the target, and the final
# stage only copies files, so building for arm64 on an x64 machine needs no emulation.

ARG DOTNET_VERSION=10.0

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION}-noble AS build
ARG TARGETARCH
# Version stamped into the binaries, e.g. 1.2.0. Empty keeps the version in Directory.Build.props.
ARG VERSION=
WORKDIR /src

# Both stages are Ubuntu (glibc), so publish for linux-x64/linux-arm64 and get SkiaSharp's glibc native library.
# An Alpine base would need the linux-musl-* builds instead.
RUN case "$TARGETARCH" in \
      amd64) echo linux-x64 ;; \
      arm64) echo linux-arm64 ;; \
      *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac > /tmp/rid

# Restore in its own layer, cached until a project file changes.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./
COPY build/Licenses.targets build/
COPY src/SweetHide.Core/SweetHide.Core.csproj src/SweetHide.Core/
COPY src/SweetHide.Server/SweetHide.Server.csproj src/SweetHide.Server/
RUN dotnet restore src/SweetHide.Server/SweetHide.Server.csproj -r "$(cat /tmp/rid)" -p:UseAppHost=false

COPY LICENSE THIRD-PARTY-NOTICES.txt ./
COPY src/SweetHide.Core/ src/SweetHide.Core/
COPY src/SweetHide.Server/ src/SweetHide.Server/
RUN dotnet publish src/SweetHide.Server/SweetHide.Server.csproj \
      -c Release -r "$(cat /tmp/rid)" --self-contained false --no-restore \
      -p:UseAppHost=false ${VERSION:+-p:Version=$VERSION} \
      -o /app \
 && test -f /app/libSkiaSharp.so

# Chiseled = distroless Ubuntu: no shell, no package manager, non-root by default. The "-extra" variant adds ICU,
# which the server needs: passwords are Unicode-normalized (NFC) before key derivation, and without ICU .NET skips
# that silently, so Thai or accented passwords would not match the other SweetHide apps. The app refuses to start
# in that case rather than produce incompatible images.
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS final

LABEL org.opencontainers.image.title="SweetHide" \
      org.opencontainers.image.description="Hide encrypted text and files in images from your browser. Meant for trusted home networks." \
      org.opencontainers.image.licenses="MIT"

WORKDIR /app
# Owned by root: the server can read its files but not change them.
COPY --from=build /app ./

# The chiseled images already run as the unprivileged "app" user (UID 1654); stated so it can't change unnoticed.
USER $APP_UID

# Nothing is written to disk, so the container runs fine with a read-only root filesystem. Diagnostics are off
# because the diagnostic server would otherwise try to create a socket under /tmp.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_EnableDiagnostics=0

EXPOSE 8080

# No shell or curl in a chiseled image: the app probes its own /healthz.
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "SweetHide.Server.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "SweetHide.Server.dll"]
