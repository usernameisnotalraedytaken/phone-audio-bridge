#!/usr/bin/env bash
# 推送 Android 侧改动并监控 CI。
#
# 什么时候才需要它：只有 app/ 或 .github/ 下的改动需要。
# Android 代码本地没有 SDK 无法编译，验证只能靠 CI。
#
# 电脑端（pc-receiver/）不需要走这里：本地就能编译运行，改完直接 build.ps1 验证。
# 之前用 git add -A 无脑推送，把电脑端改动也带上去触发 CI，纯属浪费。
#
# 用法：bash ci-bg.sh   （或放后台）

set -u
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

REPO=usernameisnotalraedytaken/phone-audio-bridge
export GH_TOKEN="$(gh auth token 2>/dev/null)"

# ---- 判断是否有需要 CI 的改动 ----
NEED_CI=0
for p in app .github gradle.properties settings.gradle.kts build.gradle.kts; do
  if ! git diff --quiet -- "$p" 2>/dev/null || ! git diff --cached --quiet -- "$p" 2>/dev/null; then
    NEED_CI=1
    echo "检测到需 CI 的改动：$p"
  fi
done

if [ "$NEED_CI" -eq 0 ]; then
  echo "app/ 与 .github/ 下无改动，无需触发 CI。"
  echo "（电脑端改动请用 pc-receiver/build.ps1 在本地验证）"
  exit 0
fi

echo
echo "=== [$(date +%H:%M:%S)] 提交 ==="
git add app .github gradle.properties settings.gradle.kts build.gradle.kts 2>/dev/null
if git diff --cached --quiet; then
  echo "无待提交的 Android 改动"
else
  git commit -q -m "${COMMIT_MSG:-手机端改动：详见 git log}"
  echo "已提交：$(git log --oneline -1)"
fi

echo
echo "=== [$(date +%H:%M:%S)] 推送 ==="
# push_via_api.py 在仓库根目录；它会把工作区所有受版本控制的文件同步过去
python3 push_via_api.py 2>&1 | tail -5

echo
echo "=== [$(date +%H:%M:%S)] 等待 CI ==="
PREV=$(curl -sS --max-time 25 -H "Authorization: token $GH_TOKEN" \
  "https://api.github.com/repos/$REPO/actions/runs?per_page=1" 2>/dev/null \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['workflow_runs'][0]['run_number'] if d.get('workflow_runs') else 0)" 2>/dev/null)
echo "推送前最新 run = #$PREV"

for i in $(seq 1 40); do
  sleep 20
  INFO=$(curl -sS --max-time 25 -H "Authorization: token $GH_TOKEN" \
    "https://api.github.com/repos/$REPO/actions/runs?per_page=2" 2>/dev/null | python3 -c "
import json,sys
try: d=json.load(sys.stdin)
except Exception: print('||'); raise SystemExit
runs=d.get('workflow_runs',[])
if not runs: print('none||'); raise SystemExit
r=runs[0]
print(f\"{r['run_number']}|{r['status']}|{r['conclusion']}|{r['id']}\")
" 2>/dev/null)
  NUM=$(echo "$INFO"|cut -d'|' -f1); ST=$(echo "$INFO"|cut -d'|' -f2)
  CC=$(echo "$INFO"|cut -d'|' -f3); ID=$(echo "$INFO"|cut -d'|' -f4)

  if [ "$NUM" = "$PREV" ]; then
    echo "  [$i] 仍未见新运行（最新 #$NUM $ST）"
    continue
  fi
  echo "  [$i] run #$NUM $ST $CC"
  if [ "$ST" = "completed" ]; then
    echo
    echo "=== 结果 ==="
    curl -sS --max-time 25 -H "Authorization: token $GH_TOKEN" \
      "https://api.github.com/repos/$REPO/actions/runs/$ID/jobs" 2>/dev/null | python3 -c "
import json,sys
d=json.load(sys.stdin)
for j in d.get('jobs', []):
    print(f\"JOB {j['name']}: {j['conclusion']}\")
    for s in j.get('steps', []):
        mark = 'OK ' if s['conclusion']=='success' else ('FAIL' if s['conclusion']=='failure' else '--  ')
        print(f\"   {mark} {s['number']:2d}. {s['name']}\")
    print('   jobid:', j['id'])
"
    if [ "$CC" = "failure" ]; then
      echo
      echo "=== 失败原因 ==="
      JOBID=$(curl -sS --max-time 25 -H "Authorization: token $GH_TOKEN" \
        "https://api.github.com/repos/$REPO/actions/runs/$ID/jobs" 2>/dev/null \
        | python3 -c "import json,sys; print(json.load(sys.stdin)['jobs'][0]['id'])")
      gh api "repos/$REPO/actions/jobs/$JOBID/logs" 2>/dev/null \
        | tr -d '\r' | sed 's/\x1b\[[0-9;]*m//g' \
        | grep -nE "^e: |error:|FAILURE:|What went wrong|Caused by|Unresolved|> Task .* FAILED" | head -25
    fi
    break
  fi
done
echo
echo "=== [$(date +%H:%M:%S)] 结束 ==="
