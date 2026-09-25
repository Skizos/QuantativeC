#!/usr/bin/env bash
# PostToolUse hook (matcher: Edit|Write). Formats the file Claude just changed:
#   *.cs                      -> dotnet format (scoped to that file) when a solution exists
#   *.h/*.hpp/*.c/*.cpp/...   -> clang-format -i (uses the repo .clang-format)
# Never fails the tool call: formatting problems are reported on stderr only.
set -u
input="$(cat)"

# Extract "file_path":"..." from the hook JSON without jq; unescape JSON backslashes (Windows paths).
file="$(printf '%s' "$input" | sed -n 's/.*"file_path"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
file="${file//\\\\/\\}"
[ -n "$file" ] && [ -f "$file" ] || exit 0

root="${CLAUDE_PROJECT_DIR:-$(pwd)}"

case "$file" in
  *.cs)
    command -v dotnet >/dev/null 2>&1 || exit 0
    target=""
    for candidate in "$root/QuantAnalyst.slnx" "$root/QuantAnalyst.sln"; do
      if [ -f "$candidate" ]; then target="$candidate"; break; fi
    done
    [ -n "$target" ] || exit 0
    (cd "$root" && dotnet format "$target" --include "$file" --verbosity quiet) \
      || echo "format-on-edit: dotnet format failed for $file (not blocking)" >&2
    ;;
  *.h|*.hh|*.hpp|*.hxx|*.c|*.cc|*.cpp|*.cxx|*.ipp|*.inl)
    command -v clang-format >/dev/null 2>&1 || exit 0
    clang-format -i --style=file "$file" \
      || echo "format-on-edit: clang-format failed for $file (not blocking)" >&2
    ;;
esac
exit 0
