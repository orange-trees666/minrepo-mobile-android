#!/usr/bin/env bash
# ビルドしたAPKそのものを起動し、実WebViewのJS・Cookie・全台照合を検証します。
set -euo pipefail
sdk_tools="$ANDROID_HOME/cmdline-tools/latest/bin"
adb_cmd="$ANDROID_HOME/platform-tools/adb"
test_dir="$RUNNER_TEMP/minrepo-browser-test"
mkdir -p "$test_dir"
# 新しいavdmanagerとemulatorで既定の保存先が異なるため、両方を同じ作業領域へ固定します。
export ANDROID_USER_HOME="$test_dir/android"
export ANDROID_AVD_HOME="$ANDROID_USER_HOME/avd"
mkdir -p "$ANDROID_AVD_HOME"
# headlessでもemulator実行ファイルがリンクするLinuxライブラリーを用意します。
sudo apt-get update -qq
sudo apt-get install -y libpulse0 libglu1-mesa libxcb-cursor0
"$sdk_tools/sdkmanager" "emulator" "system-images;android-35;google_apis;x86_64"
printf 'no\n' | "$sdk_tools/avdmanager" create avd --force --name minrepo-browser \
  --package "system-images;android-35;google_apis;x86_64" --device pixel_7 \
  --path "$ANDROID_AVD_HOME/minrepo-browser.avd"
# avdmanagerの版によらず、emulatorが参照する登録ファイルも明示します。
printf 'avd.ini.encoding=UTF-8\npath=%s\ntarget=android-35\n' \
  "$ANDROID_AVD_HOME/minrepo-browser.avd" > "$ANDROID_AVD_HOME/minrepo-browser.ini"
sudo chmod a+rw /dev/kvm
"$ANDROID_HOME/emulator/emulator" -avd minrepo-browser -no-window -no-audio \
  -no-boot-anim -no-snapshot -gpu software -memory 2048 \
  > "$test_dir/emulator.log" 2>&1 &
emulator_pid=$!
trap 'kill "$emulator_pid" 2>/dev/null || true' EXIT
booted=false
for attempt in {1..90}; do
  if ! kill -0 "$emulator_pid" 2>/dev/null; then
    tail -n 100 "$test_dir/emulator.log"
    exit 1
  fi
  if [[ $("$adb_cmd" shell getprop sys.boot_completed | tr -d '\r') == 1 ]]; then
    booted=true
    break
  fi
  sleep 2
done
if [[ "$booted" != true ]]; then
  tail -n 80 "$test_dir/emulator.log"
  exit 1
fi
"$adb_cmd" shell input keyevent 82
"$adb_cmd" shell settings put system screen_off_timeout 600000
apk_path=$(python3 - <<'PY'
from pathlib import Path
paths = list(Path('MinRepoMobile/bin/Debug/net10.0-android').rglob('*-Signed.apk'))
assert len(paths) == 1, paths
print(paths[0])
PY
)
"$adb_cmd" install -r "$apk_path"
component=$("$adb_cmd" shell cmd package resolve-activity --brief jp.minrepo.mobileextractor | tail -n 1 | tr -d '\r')
"$adb_cmd" shell am start -W -n "$component" --ez minrepo_smoke_test true
for attempt in {1..72}; do
  if "$adb_cmd" shell run-as jp.minrepo.mobileextractor cat files/browser-smoke.json \
      > "$test_dir/result.json" 2>/dev/null; then
    if python3 - "$test_dir/result.json" <<'PY'
import json,sys
try:
    result=json.load(open(sys.argv[1]))
except (ValueError, OSError):
    sys.exit(2)
print(json.dumps(result,ensure_ascii=False,indent=2))
sys.exit(0 if result['success'] else 1)
PY
    then
      exit 0
    else
      result_code=$?
      if [[ "$result_code" == 1 ]]; then
        "$adb_cmd" logcat -d -s MinRepoBrowserTest AndroidRuntime chromium
        exit 1
      fi
    fi
  fi
  sleep 5
done
"$adb_cmd" logcat -d -s MinRepoBrowserTest AndroidRuntime chromium
echo "Android browser smoke test did not finish within six minutes."
exit 1
