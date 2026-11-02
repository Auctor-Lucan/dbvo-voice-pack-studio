#!/bin/sh
# Copy the repo's backend into the demo (keeping the demo's own host files), then rebuild the demo host.
set -e
D=${DEMO:-/tmp/dbvo-work/demo}
R=${REPO:-/home/claude/dbvo-voice-pack-studio}/src
(cd "$R" && find . -type f -not -path '*/bin/*' -not -path '*/obj/*' -not -path '*/wwwroot/*' -not -name App.xaml.cs -not -name MainWindow.xaml.cs -not -name ElevenLabsClient.cs) | while read f; do mkdir -p "$D/src/$(dirname "$f")"; cp -p "$R/$f" "$D/src/$f"; done
# The demo's own versions of a few files (fake ElevenLabs client, no WPF window), kept in overrides/.
(cd "$D/overrides" && find . -type f) | while read f; do mkdir -p "$D/src/$(dirname "$f")"; [ -f "$D/src/$f" ] || cp -p "$D/overrides/$f" "$D/src/$f"; done
mkdir -p "$D/out"
mkdir -p "$D/tests/DbvoStudio.Core.Tests" && cp -p "$R/../tests/DbvoStudio.Core.Tests/TestPluginBuilder.cs" "$R/../tests/DbvoStudio.Core.Tests/BookPluginBuilder.cs" "$D/tests/DbvoStudio.Core.Tests/"
sed -i 's|public string FuzTemplate(DbvoPatch p, VoicePack v) => ResolveFuzTemplate(p, v);|public string FuzTemplate(DbvoPatch p, VoicePack v) => ResolveFuzTemplate(p, v).Replace('"'\\\\\\\\'"', '"'/'"'); // demo: Linux separators|' "$D/src/DbvoStudio.Core/Projects/Phase2Service.cs"
grep -n "public string FuzTemplate" "$D/src/DbvoStudio.Core/Projects/Phase2Service.cs"
cd "$D/host" && PATH=/opt/dotnet:$PATH dotnet build 2>&1 | grep -E " error |Build succeeded" | head
