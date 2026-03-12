#!/usr/bin/env pwsh

Write-Host "Setting up StashLock development environment..." -ForegroundColor Cyan

# # Install Claude Code CLI
# Write-Host "Installing Claude Code CLI..." -ForegroundColor Yellow
# npm install -g @anthropic-ai/claude-code
#curl -fsSL https://claude.ai/install.sh | bash

# # Restore NuGet packages
# Write-Host "Restoring NuGet packages..." -ForegroundColor Yellow
# dotnet restore src/StashLock.sln

# # Build solution to verify setup
# Write-Host "Building solution..." -ForegroundColor Yellow
# dotnet build src/StashLock.sln --no-restore

# Create dev directories (SimpleEnv dev mode expects these)
Write-Host "Creating dev directories..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path "dev/app.vs/config" -Force | Out-Null
New-Item -ItemType Directory -Path "dev/app.vs/log" -Force | Out-Null
New-Item -ItemType Directory -Path "dev/app.vs/work" -Force | Out-Null

Write-Host "Development environment ready!" -ForegroundColor Green
