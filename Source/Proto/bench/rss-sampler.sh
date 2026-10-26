#!/usr/bin/env bash
# rss-sampler.sh <roots-file> <out.tsv>: every 0.5 s, the RSS (KiB) of each root pid's process tree (children such as
# MSBuild's build host included). Roots are re-read each tick. Writes "epoch total root1 root2 ...".
roots_file=$1; out=$2
: > "$out"
while true; do
  roots=$(cat "$roots_file" 2>/dev/null)
  [ -z "$roots" ] && { sleep 0.5; continue; }
  line=$(ps -eo pid=,ppid=,rss= | awk -v roots="$roots" '
    BEGIN { n=split(roots, r, " "); for (i=1;i<=n;i++) owner[r[i]]=r[i] }
    { pid[NR]=$1; ppid[NR]=$2; rss[NR]=$3 }
    END { changed=1; while (changed) { changed=0; for (i=1;i<=NR;i++) if (!(pid[i] in owner) && (ppid[i] in owner)) { owner[pid[i]]=owner[ppid[i]]; changed=1 } }
          for (i=1;i<=NR;i++) if (pid[i] in owner) { s[owner[pid[i]]]+=rss[i]; t+=rss[i] }
          printf "%d", t; for (i=1;i<=n;i++) printf " %d", s[r[i]]; print "" }')
  echo "$(date +%s.%N | cut -c1-14) $line" >> "$out"
  sleep 0.5
done
