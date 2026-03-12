# syntax=docker/dockerfile:1.7
FROM mcr.microsoft.com/devcontainers/dotnet:10.0

SHELL ["/bin/bash", "-lc"]

# Use the devcontainers dotnet layout consistently.
ENV DOTNET_ROOT=/usr/local/dotnet
ENV PATH="/usr/local/dotnet:${PATH}"

# Reduce noise / nondeterminism in containers.
ENV DOTNET_SKIP_WORKLOAD_MANIFEST_UPDATE=1
ENV DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE=1

# Install .NET SDK 10 (explicitly) + .NET 8 runtime into the SAME DOTNET_ROOT.
ARG DOTNET_SDK_CHANNEL=10.0
ARG DOTNET_RUNTIME8_VERSION=8.0.24

RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh \
 && chmod +x /tmp/dotnet-install.sh \
 && /tmp/dotnet-install.sh --channel "${DOTNET_SDK_CHANNEL}" --install-dir "${DOTNET_ROOT}" \
 && /tmp/dotnet-install.sh --runtime dotnet --version "${DOTNET_RUNTIME8_VERSION}" --install-dir "${DOTNET_ROOT}" \
 && rm -f /tmp/dotnet-install.sh

# Remove any preinstalled/preview PowerShell binaries that may depend on preview runtimes.
RUN rm -f /usr/share/powershell/pwsh || true \
 && rm -rf /usr/share/powershell || true \
 && rm -f /usr/local/bin/pwsh || true \
 && rm -f /usr/bin/pwsh || true

# Install stable PowerShell from Microsoft package repository (Debian 12).
RUN apt-get update \
 && apt-get install -y --no-install-recommends wget ca-certificates gnupg \
 && wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb \
 && dpkg -i packages-microsoft-prod.deb \
 && rm packages-microsoft-prod.deb \
 && apt-get update \
 && apt-get install -y --no-install-recommends powershell \
 && rm -rf /var/lib/apt/lists/*

# Install Claude Code CLI (per-user) for vscode.
# Put it into /home/vscode/.local/bin and make sure it's on PATH.
ENV PATH="/home/vscode/.local/bin:${PATH}"
RUN mkdir -p /home/vscode/.local/bin \
 && chown -R vscode:vscode /home/vscode/.local \
 && su - vscode -c 'curl -fsSL https://claude.ai/install.sh | bash'

# Sanity check during image build.
RUN echo "dotnet path: $(which dotnet)" \
 && echo "DOTNET_ROOT=${DOTNET_ROOT}" \
 && dotnet --list-sdks \
 && dotnet --list-runtimes \
 && which pwsh \
 && pwsh --version \
 && su - vscode -c 'command -v claude && claude --version'
