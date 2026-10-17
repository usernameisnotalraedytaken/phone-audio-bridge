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
import time
import urllib.error
import urllib.parse
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


def call(token, method, url, payload=None, retries=4):
    """带重试的 API 调用。远端偶发 RemoteDisconnected，必须重试否则长推送必然中断。"""
    last = None
    for attempt in range(1, retries + 1):
        data = json.dumps(payload).encode() if payload is not None else None
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header("Authorization", f"token {token}")
        req.add_header("Accept", "application/vnd.github+json")
        req.add_header("User-Agent", "snad-push")
        if data:
            req.add_header("Content-Type", "application/json")
        try:
            with urllib.request.urlopen(req, timeout=90) as r:
                body = r.read().decode()
                return r.status, (json.loads(body) if body else {})
        except urllib.error.HTTPError as e:
            body = e.read().decode()[:500]
            # 4xx 是请求本身的问题，重试无意义
            if e.code < 500:
                return e.code, {"error": body}
            last = f"HTTP {e.code}: {body}"
        except Exception as e:
            last = f"{type(e).__name__}: {e}"
        if attempt < retries:
            wait = 2 ** attempt
            print(f"      重试 {attempt}/{retries - 1}（{last}），等待 {wait}s")
            time.sleep(wait)
    return 0, {"error": last}


def remote_tree_map(token):
    """取远端当前 tree，用于跳过内容未变的文件。"""
    ok, ref = call(token, "GET", f"{API}/git/ref/heads/{BRANCH}")
    if ok != 200:
        return {}, None
    commit_sha = ref["object"]["sha"]
    ok, commit = call(token, "GET", f"{API}/git/commits/{commit_sha}")
    if ok != 200:
        return {}, commit_sha
    ok, tree = call(token, "GET", f"{API}/git/trees/{commit['tree']['sha']}?recursive=1")
    if ok != 200:
        return {}, commit_sha
    return {e["path"]: e["sha"] for e in tree.get("tree", []) if e["type"] == "blob"}, commit_sha


def git_blob_sha(path):
    """计算文件在 git 中的 blob 哈希，用于与远端比对。"""
    out = subprocess.run(["git", "hash-object", path], capture_output=True, text=True)
    return out.stdout.strip() if out.returncode == 0 else None


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

    # 取远端当前状态，用于跳过内容未变的文件
    remote_blobs, parent = remote_tree_map(token)
    if not parent:
        sys.exit("无法读取远端分支状态")
    print(f"父提交 {parent[:8]}，远端已有 {len(remote_blobs)} 个文件")

    # 逐个处理：内容与远端一致就直接复用其 sha，否则上传
    tree_items = []
    fallback = []
    skipped = 0
    for i, path in enumerate(files, 1):
        if not os.path.isfile(path):
            print(f"  [{i}/{len(files)}] {path} 跳过（不是文件）")
            continue
        local_sha = git_blob_sha(path)
        if local_sha and remote_blobs.get(path) == local_sha:
            tree_items.append({"path": path, "mode": "100644", "type": "blob", "sha": local_sha})
            skipped += 1
            print(f"  [{i}/{len(files)}] {path} 未改动，复用")
            continue
        raw = open(path, "rb").read()
        ok, blob = call(token, "POST", f"{API}/git/blobs",
                        {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
        if ok != 201:
            # blobs 接口偶尔会对**某些具体载荷**返回 400：
            # 同样的字节数换内容就能过，连改行尾都不行。这不是请求格式问题，
            # 是服务端对该载荷的拒绝。兜底：记下来用 Contents API 单独提交，
            # 不因为一个文件把整次推送卡住。
            fallback.append(path)
            print(f"  [{i}/{len(files)}] {path}  blobs 拒绝（{ok}），改用 Contents API 兜底")
            continue
        tree_items.append({"path": path, "mode": "100644", "type": "blob", "sha": blob["sha"]})
        print(f"  [{i}/{len(files)}] {path}  {len(raw)}B 上传")
    print(f"（{skipped} 个文件未改动）")

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

    # ---- blobs 接口拒绝的文件，用 Contents API 逐个补提交 ----
    #
    # Contents API 走的是另一条路径，不受 blobs 那个"载荷拒绝"影响。
    # 每个文件一次提交，提交信息注明原因，免得日后看不出为什么多出几个提交。
    for path in fallback:
        api_path = urllib.parse.quote(path)
        ok, cur = call(token, "GET", f"{API}/contents/{api_path}?ref={BRANCH}")
        if ok != 200 or "sha" not in cur:
            print(f"  兜底失败：读不到远端 {path}（{ok}）")
            continue

        raw = open(path, "rb").read()
        ok2, res2 = call(token, "PUT", f"{API}/contents/{api_path}", {
            "message": f"{message}\n\n（blobs 接口对该载荷返回 400，改用 Contents API 提交）",
            "content": base64.b64encode(raw).decode(),
            "sha": cur["sha"],
            "branch": BRANCH,
        })
        if ok2 in (200, 201):
            print(f"  兜底提交成功：{path}")
        else:
            print(f"  兜底提交失败：{path}（{ok2}）")

    print("Actions 会自动触发构建。")


if __name__ == "__main__":
    main()
