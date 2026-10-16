#!/usr/bin/env bash
# 后台任务：推送到 GitHub 并监控 CI 结果。
#
# 为什么要独立脚本：HTTPS git 在本机被网络阻断，推送只能走 API；
# 而 Android 代码本地无法编译，验证只能靠 CI。把这段放后台，
# 避免阻塞本地对电脑端的验证工作。
#
# 用法：nohup bash ci-bg.sh > ci-bg.log 2>&1 &

set -u
# push_via_api.py 在仓库根目录，不在本脚本所在目录。
# 踩过一次：路径写错导致推送静默失败，CI 一直在看旧记录。
REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

REPO=usernameisnotalraedytaken/phone-audio-bridge
export GH_TOKEN="$(gh auth token 2>/dev/null)"

echo "=== [$(date +%H:%M:%S)] 提交本地改动 ==="
git add -A
if git diff --cached --quiet; then
  echo "无待提交改动"
else
  git commit -q -m "手机 UI：自动发现为主，手填地址退到折叠区

问题：既然宣称无感，界面上不该还摆着'电脑地址'和'端口'两个输入框。
那是信标功能之前的界面，没有同步修改。

改动：
- 顶部改为展示自动发现结果（正在搜索电脑… → 已找到电脑：ip:port）
- 未发现电脑前禁用开始串流，避免用户点了才开始想地址填什么
- 手填地址折叠到「手动指定地址」按钮后面，仅在广播不可达的网络才需要展开
- 去掉 ifEmpty { hint_host } 占位常量回退：没地址时明确报错，
  而不是悄悄连到某个固定 IP"
  echo "已提交：$(git log --oneline -1)"
fi

echo
echo "=== [$(date +%H:%M:%S)] 推送 ==="
python3 push_via_api.py 2>&1 | tail -6

echo
echo "=== [$(date +%H:%M:%S)] 等待 CI ==="
PREV=$(curl -sS --max-time 25 -H "Authorization: token $GH_TOKEN" \
  "https://api.github.com/repos/$REPO/actions/runs?per_page=1" 2>/dev/null \
  | python3 -c "import json,sys; d=json.load(sys.stdin); print(d['workflow_runs'][0]['run_number'] if d.get('workflow_runs') else 0)" 2>/dev/null)

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
  echo "  [$i] run #$NUM $ST $CC"
  if [ "$ST" = "completed" ] && [ "$NUM" != "$PREV" ]; then
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
      echo "=== 失败日志（错误行）==="
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
echo "=== [$(date +%H:%M:%S)] 后台任务结束 ==="
