#!/bin/bash

# Find container ID based on the image name
CONTAINER_ID=$(docker ps -q --filter "label=devcontainer.local_folder=$PWD")

if [ -z "$CONTAINER_ID" ]; then
    echo "ℹ️ No running Dev Container found."
else
    echo "🛑 Stopping container $CONTAINER_ID..."
    docker stop $CONTAINER_ID
    echo "✅ Container stopped."
fi
