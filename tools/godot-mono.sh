#!/bin/zsh
# Always launch Godot-mono through this wrapper. It fixes the two traps that make
# a spawned/GUI Godot fail for .NET (which doesn't read ~/.zshrc):
#   - DOTNET_ROOT + PATH so Godot (and the `dotnet` build it spawns) find the SDK.
#   - arch -arm64 so the universal Godot binary loads the arm64 hostfxr (never let
#     an Intel /usr/local/bin/timeout wrap it — that flips it to x86_64).
# Usage: tools/godot-mono.sh --path . res://tools/shoot.tscn -- res://scenes/NN.tscn 3
export DOTNET_ROOT="$HOME/.dotnet"
export PATH="$HOME/.dotnet:$PATH"
exec arch -arm64 /Applications/Godot-mono.app/Contents/MacOS/Godot "$@"
