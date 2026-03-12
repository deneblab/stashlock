#!/bin/bash

# Configuration
WORKSPACE_FOLDER="."

echo "🚀 Starting Dev Container ..."

# 1. Start the container
# This command returns a JSON with container details
RESULT=$(devcontainer up --workspace-folder $WORKSPACE_FOLDER)

if [ $? -ne 0 ]; then
    echo "❌ Error: Failed to start the container."
    exit 1
fi

echo "✅ Container is up and running."

# 2. Enter the interactive shell
echo "Terminal session starting..."
devcontainer exec --workspace-folder $WORKSPACE_FOLDER /bin/bash
