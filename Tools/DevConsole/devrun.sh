#!/usr/bin/env bash
# Compile a C# snippet against the game's assemblies and run it inside the running game.
#
#   devrun.sh [-p PORT] 'C# code'
#   devrun.sh [-p PORT] -f snippet.cs
#
# One expression (no ';') is returned as the value. Otherwise the code is a method body: use
# 'return x;' to return something. PORT is 7778 for the first game copy, 7779 for the second.
# SAILWIND_DIR overrides the game folder.
set -euo pipefail

GAME="${SAILWIND_DIR:-D:/SteamLibrary/steamapps/common/Sailwind}"
PORT=7778
FILE=""
while getopts "p:f:" opt; do
  case "$opt" in
    p) PORT="$OPTARG" ;;
    f) FILE="$OPTARG" ;;
    *) echo "usage: devrun.sh [-p PORT] 'code' | -f file" >&2; exit 2 ;;
  esac
done
shift $((OPTIND - 1))
if [ -n "$FILE" ]; then CODE="$(cat "$FILE")"; else CODE="${1:?usage: devrun.sh [-p PORT] 'code' | -f file}"; fi

SDK="$(dotnet --list-sdks | tail -1 | sed -E 's/^([^ ]+) \[(.*)\]$/\2\/\1/')"
CSC="$SDK/Roslyn/bincore/csc.dll"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

# Each snippet needs its own assembly name: the runtime may hand back an already loaded
# assembly for a name it has seen.
NAME="snippet_$(date +%s)_$$"

if [[ "$CODE" == *";"* ]]; then BODY="$CODE
return null;"; else BODY="return (object)(
$CODE
);"; fi

cat > "$WORK/$NAME.cs" <<EOF
using System; using System.Linq; using System.Collections.Generic; using UnityEngine;
using SailwindCoop; using SailwindCoop.Net; using SailwindCoop.Sync; using SailwindCoop.Runtime;
using SailwindCoop.DevConsole;
public static class Snippet { public static object Run() {
#line 1 "snippet"
$BODY
} }
EOF

for dll in "$GAME"/Sailwind_Data/Managed/*.dll "$GAME/BepInEx/core/BepInEx.dll" \
           "$GAME"/BepInEx/plugins/SailwindCoop/*.dll \
           "$GAME/BepInEx/plugins/SailwindCoopDevConsole/SailwindCoopDevConsole.dll"; do
  echo "-r:\"$dll\""
done > "$WORK/refs.rsp"

# Compiled against the game's own mscorlib (-nostdlib), so the snippet sees exactly the runtime it runs on.
dotnet "$CSC" -nologo -noconfig -nostdlib+ -target:library -debug- -utf8output -preferreduilang:en-US \
  -nowarn:CS0162,CS1701,CS1702 -out:"$WORK/$NAME.dll" "@$WORK/refs.rsp" "$WORK/$NAME.cs" >&2

curl -s --max-time 30 -H "X-DevConsole: 1" -H "Content-Type: application/octet-stream" \
  --data-binary "@$WORK/$NAME.dll" "http://127.0.0.1:$PORT/run"
echo
