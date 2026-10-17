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
        # blobs 接口在链路不稳时会返回 400 "malformed request"，
        # 但同一载荷过一会儿又能过——因此**必须重试**，不能当成永久失败。
        ok, blob = 0, {}
        for attempt in range(1, 6):
            ok, blob = call(token, "POST", f"{API}/git/blobs",
                            {"content": base64.b64encode(raw).decode(), "encoding": "base64"})
            if ok == 201:
                break
            print(f"  [{i}/{len(files)}] {path} 上传失败（{ok}），{attempt}/5 重试")
            time.sleep(3 * attempt)

        if ok != 201:
            # 关键：**绝不能把路径从 tree 里丢掉**。
            # GitHub 的建 tree 是"整棵树"语义，路径缺席等于删除该文件。
            # 这里曾真的删掉过 SnadProfileForm.cs（因为失败的 blob 被 continue 跳过）。
            # 正确做法：沿用远端已有的 blob（保留旧内容），把这次更新记为"未落地"。
            old_sha = remote_blobs.get(path)
            if not old_sha:
                sys.exit(f"  {path} 是新增文件且上传失败（{ok}），无法保留旧版本，中止推送")
            tree_items.append({"path": path, "mode": "100644", "type": "blob", "sha": old_sha})
            fallback.append(path)
            print(f"  [{i}/{len(files)}] {path} 上传失败，沿用远端旧版本（内容未更新，不会删除）")
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

    # ---- 上传失败的文件：远端保留的是旧版本，这里再用 Contents API 补一次 ----
    #
    # 两种通道都受链路影响，所以这里只承诺"不会把文件删掉"，
    # 补提交成不成功都要**明确列出**，不能让人以为全部落地了。
    if fallback:
        print(f"\n注意：{len(fallback)} 个文件本次未更新（远端仍是旧版本）：")
        for path in fallback:
            api_path = urllib.parse.quote(path)
            ok, cur = call(token, "GET", f"{API}/contents/{api_path}?ref={BRANCH}")
            if ok != 200 or "sha" not in cur:
                print(f"  {path}：读不到远端（{ok}），跳过")
                continue

            raw = open(path, "rb").read()
            ok2, res2 = 0, {}
            for attempt in range(1, 4):
                ok2, res2 = call(token, "PUT", f"{API}/contents/{api_path}", {
                    "message": f"{message}\n\n（blobs 接口上传失败，改用 Contents API 补交）",
                    "content": base64.b64encode(raw).decode(),
                    "sha": cur["sha"],
                    "branch": BRANCH,
                })
                if ok2 in (200, 201):
                    break
                time.sleep(3 * attempt)

            if ok2 in (200, 201):
                print(f"  {path}：已通过 Contents API 补上")
            else:
                print(f"  {path}：**仍然是旧版本**（Contents API 也失败 {ok2}），下次推送需重试")

    print("Actions 会自动触发构建。")


if __name__ == "__main__":
    main()
