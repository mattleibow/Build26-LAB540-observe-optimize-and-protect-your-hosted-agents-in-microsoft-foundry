#!/bin/bash
set -e

echo "Upgrading Azure Developer CLI to latest version..."
curl -fsSL https://aka.ms/install-azd.sh | bash

echo "Installing azd hosted-agent extension (azure.ai.agents) ..."
# Idempotent: 'azd extension install' upgrades if already present.
azd extension install azure.ai.agents || \
    echo "WARN: failed to install azure.ai.agents extension; install manually with 'azd extension install azure.ai.agents'."

echo "Restoring .NET dependencies for the Zava Concierge agent ..."
dotnet restore zava/src/zava-travel-concierge/Program.cs

echo "Verifying .NET SDK ..."
dotnet --info | head -5

echo "Post-create setup complete."
