#!/usr/bin/env bash
# TST-019, part 1: session hosted inside game instance c1; c1 and c2 play under it.
# Each instance gets its own data folder (-savedatafolder); the user's real config is not touched.
set -e
REPO=/e/claude/RWMLWC
DATA="/c/Users/igram/AppData/LocalLow/Ludeon Studios/RimWorld by Ludeon Studios"
WORK="$(cygpath "$LOCALAPPDATA")/RWMLWC-test/tst019"
GAME="D:/SteamLibrary/steamapps/common/RimWorld"
SAVE="$DATA/Saves/Поселение5.rws"
PORT=47811
rm -rf "$WORK"; mkdir -p "$WORK"

for c in c1 c2; do
  mkdir -p "$WORK/$c/Saves" "$WORK/$c/RWMLWC"
  cp -r "$DATA/Config" "$WORK/$c/Config"
  python -I - "$WORK/$c/Config/ModsConfig.xml" <<'EOF'
import sys
p=sys.argv[1]; s=open(p,encoding='utf-8-sig').read()
s=s.replace('    <li>ebsg.framework</li>\n','').replace('    <li>psizmo.xenotype.vaelkhurmodded</li>\n','    <li>mikisnikirs.rwmlwc</li>\n')
assert 'mikisnikirs.rwmlwc' in s and 'vaelkhur' not in s
open(p,'w',encoding='utf-8-sig').write(s)
EOF
  cp "$SAVE" "$WORK/$c/Saves/tst019.rws"
done

# Certificate of c1's hosted session created in advance to know its thumbprint.
SRVLOG="$WORK/precert.txt"
timeout 4 dotnet "$REPO/Source/Server/bin/Release/net10.0/RWMLWC.Server.dll" --port 0 --data "$WORK/c1/RWMLWC/server" > "$SRVLOG" 2>&1 || true
THUMB=$(grep LISTENING "$SRVLOG" | cut -d' ' -f3)
echo "thumbprint $THUMB"

printf 'armed=1\nrole=hostsession\nport=%s\nstarttick=14998\nseed=7\nsteam=1\n' "$PORT" > "$WORK/c1/RWMLWC/net-test.txt"
for c in c1 c2; do
  printf 'armed=1\nsave=tst019\nscenarios=none\nticks=3000\ntrace=0\nquit=1\nholdsec=60\nserver=127.0.0.1:%s\nthumbprint=%s\nname=%s\n' "$PORT" "$THUMB" "$c" > "$WORK/$c/RWMLWC/arm-determinism-run.txt"
  printf '0' > "$WORK/$c/RWMLWC/automation-progress.txt"
done
echo "$WORK"
