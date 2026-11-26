#!/bin/bash
# NinjaScript sanity check: compile, then flag CS0246 errors naming a type the FILE ITSELF declares
# (those are real), and simulate NinjaTrader's generated wrapper region, which emits enum-typed
# [NinjaScriptProperty] params into the Strategies / MarketAnalyzerColumns namespaces too.
F="$1"
OUT=$(mcs -t:library -nowarn:0168,0219,0414,0649 "$F" -out:/tmp/_c.dll 2>&1)
# types declared in the file
DECL=$(grep -oE '^\s*(public |internal )?(sealed )?(partial )?(class|enum|struct|interface) [A-Za-z_][A-Za-z0-9_]*' "$F" \
       | awk '{print $NF}' | sort -u)
BAD=0
for t in $DECL; do
  if echo "$OUT" | grep -q "name \`$t' could not be found"; then
    echo "REAL ERROR: '$t' is declared in this file but the compiler cannot resolve it (namespace/scope problem)"
    BAD=1
  fi
done
# simulate NinjaTrader's generated region for enum-typed NinjaScriptProperty params
ENUMS=$(grep -oE 'public enum [A-Za-z_][A-Za-z0-9_]*' "$F" | awk '{print $3}')
for e in $ENUMS; do
  if grep -q "NinjaScriptProperty.*public $e " "$F"; then
    if grep -qE "^\s*public enum $e" "$F" && ! awk -v e="$e" '/^namespace /{ns=1} $0 ~ "public enum "e { print (ns?"IN":"OUT") }' "$F" | grep -q OUT; then
      echo "REAL ERROR: enum '$e' is used by a [NinjaScriptProperty] but is declared INSIDE a namespace."
      echo "            NinjaTrader's generated wrappers also emit into .Strategies and"
      echo "            .MarketAnalyzerColumns, which cannot resolve it -> CS0246 at compile in NT."
      BAD=1
    fi
  fi
done
OTHER=$(echo "$OUT" | grep -c 'error')
echo "(compiler reported $OTHER lines; SDK-reference errors for NinjaTrader/SharpDX/WPF types are expected here)"
[ $BAD -eq 0 ] && echo "PASS: no file-declared type fails to resolve" || echo "FAIL"
