#!/usr/bin/env bash
#
# Rebuilds USERGUIDE.pdf from USERGUIDE.md.
#
# USERGUIDE.md is the thing to edit; the PDF is only ever generated from it. Both are kept in
# the repository, so someone who wants the guide does not have to build it, and someone who
# changes the program has one file to update.
#
# Needs nothing but the .NET SDK. The pictures in docs/images/guide come from the window tests:
#   AVAPAGESTREAM_SHOTS=/some/folder dotnet test AVAPageStream.slnx --filter "FullyQualifiedName~Guide_"
# then copy each guide-NAME.png there to docs/images/guide/NAME.png.

set -euo pipefail
cd "$(dirname "$0")"

VERSION="${1:-$(git describe --tags --abbrev=0 2>/dev/null | sed 's/^v//' || echo '')}"

dotnet run --project Tools/GuideBuilder -v q -- USERGUIDE.md USERGUIDE.pdf "$VERSION"
