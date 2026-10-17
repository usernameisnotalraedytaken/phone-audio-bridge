#!/bin/bash
# 构建包装：先结束我自己的检查工具，再调用 build.ps1。
#
# 为什么需要：检查工具偶尔会因上一次工具调用被中断而变成孤儿进程，
# 占住 exe 导致 build.ps1 拒绝替换（它按设计不强杀程序）。
# 症状是"改了代码但行为没变"——这次实测就在这上面浪费了两轮排查。
#
# SnadGui 是用户的应用，绝不在清理范围内。
set -u
cd "$(dirname "$0")" || exit 1

timeout 60 powershell.exe -NoProfile -Command "
Get-Process | Where-Object { \$_.ProcessName -like 'Snad*' -and \$_.ProcessName -ne 'SnadGui' } |
  ForEach-Object { Stop-Process -Id \$_.Id -Force -ErrorAction SilentlyContinue }
" >/dev/null 2>&1

timeout 320 powershell.exe -NoProfile -ExecutionPolicy Bypass \
  -File 'C:\Users\26347\Downloads\bluetooth\phone-audio-bridge\pc-receiver\build.ps1' > build.log 2>&1
rc=$?

echo "build.ps1 退出码：$rc（SnadGui 占用时非 0 属正常，其余产物仍已更新）"
echo "编译错误数：$(grep -c 'error CS' build.log)"
grep -n "无法替换" build.log | head -2 | tr -d '\r'
exit 0
