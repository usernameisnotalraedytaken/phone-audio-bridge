#!/usr/bin/env python3
"""
把本地 git 工作区的文件通过 GitHub Git Data API 推送到远端。

为什么需要它：本机所在网络屏蔽了 github.com 的 HTTPS（git push 直接超时），
而 SSH 密钥属于另一个账号（dicaeopolis），对本仓库无权限。
唯一可用的通道是 api.github.com，因此用 API 代替 git push。

用法：
    python3 push_via_api.py
    python3 push_via_api.py --message "自定义提交信息"

依赖：本机已登录的 gh CLI（用于取 token），或环境变量 GH_TOKEN。
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import urllib.error
import urllib.request

REPO = "usernameisnotalraedytaken/phone-audio-bridge"
BRANCH = "main"
API = f"https://api.github.com/repos/{REPO}"


def get_token() -> str:
    tok = os.environ.get("GH_TOKEN")
    if tok:
        return tok.strip()
    try:
        return subprocess.run(["gh", "auth", "token"], capture_output=True,
                              text=True, check=True).stdout.strip()
    except Exception as e:
        sys.exit(f"无法获取 token（请先 gh auth login 或设置 GH_TOKEN）: {e}")


def call(token, method, url, payload=None):
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", f"token {token}")
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "snad-push")
    if data:
        req.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(req, timeout=60) as r:
            body = r.read().decode()
            return r.status, (json.loads(body) if body else {})
    except urllib.error.HTTPError as e:
        return e.code, {"error": e.read().decode()[:500]}
    except Exception as e:
        return 0, {"error": f"{type(e).__name__}: {e}"}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--message", "-m", default=None, help="提交信息（默认用最近一次 git commit 的信息）")
    args = ap.parse_args()

    token = get_token()

    # 收集纳入版本控制的文件（与 .gitignore 保持一致）
    tracked = subprocess.run(["git", "ls-files"], capture_output=True, text=True,
                             check=True).stdout.split()
    if not tracked:
        sys.exit("没有找到受版本控制的文件")

    # 同时纳入已暂存的新文件
    staged = subprocess.run(["git", "diff", "--cached", "--name-only"],
                            capture_output=True, text=True).stdout.split()
    files = sorted(set(tracked) | set(staged))
    print(f"准备推送 {len(files)} 个文件到 {REPO}@{BRANCH}")

    # 取当前分支头作为父提交
    ok, ref = call(token, "GET", f"{API}/git/ref/heads/{BRANCH}")
    if ok != 200:
        sys.exit(f"无法读取分支 {BRANCH}: {ref}")
    parent = ref["object"]["sha"]
    print(f"父提交 {parent[:8]}")

    # 逐个建 blob
    tree_items = []
    for i, path in enumerate(files, 1):
        if not os.path.isfile(path):
            print(f"  [{i}/{len(files)}] {path} 跳过（不是文件）")
            continue
        raw = open(path, "rb").read()
        ok, blob = call(token, "POST", f"{API}/git/blobs",
                        {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
        if ok != 201:
            sys.exit(f"  {path} 建 blob 失败: {blob}")
        tree_items.append({"path": path, "mode": "100644", "type": "blob", "sha": blob["sha"]})
        print(f"  [{i}/{len(files)}] {path}  {len(raw)}B")

    # 建 tree
    ok, tree = call(token, "POST", f"{API}/git/trees", {"tree": tree_items})
    if ok != 201:
        sys.exit(f"建 tree 失败: {tree}")
    print(f"tree {tree['sha'][:8]}")

    # 提交信息
    message = args.message
    if not message:
        message = subprocess.run(["git", "log", "-1", "--pretty=%B"],
                                 capture_output=True, text=True).stdout.strip()
    if not message:
        message = "update"

    ok, commit = call(token, "POST", f"{API}/git/commits",
                      {"message": message, "tree": tree["sha"], "parents": [parent]})
    if ok != 201:
        sys.exit(f"建 commit 失败: {commit}")
    print(f"commit {commit['sha'][:8]}")

    ok, res = call(token, "PATCH", f"{API}/git/refs/heads/{BRANCH}",
                   {"sha": commit["sha"], "force": False})
    if ok != 200:
        sys.exit(f"更新分支失败: {res}")

    print(f"\n推送完成 -> https://github.com/{REPO}/tree/{BRANCH}")
    print("Actions 会自动触发构建。")


if __name__ == "__main__":
    main()
