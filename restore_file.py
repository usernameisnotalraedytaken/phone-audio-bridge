#!/usr/bin/env python3
# 把被误删的 pc-receiver/SnadProfileForm.cs 补回去。
#
# 起因：推送脚本在 blob 上传失败时 continue，那棵树里就没有这个路径，
# 而 GitHub 的建 tree 是"整棵树"语义 —— 路径缺席等于删除。
# 这里改用 base_tree 只补回这一项，且引用**仓库里已存在的 blob**，
# 不需要重新上传 70KB 内容（当前链路对大体积极不稳定）。
#
# 每个请求都重试：链路会间歇性超时，一次失败不代表这条路不通。

import json, os, subprocess, sys, time, urllib.error, urllib.request

API = "https://api.github.com/repos/usernameisnotalraedytaken/phone-audio-bridge"
TARGET = "pc-receiver/SnadProfileForm.cs"


def token():
    t = os.environ.get("GH_TOKEN")
    if not t:
        t = subprocess.check_output(["gh", "auth", "token"], text=True, timeout=30).strip()
    return t


def call(tok, method, url, payload=None, tries=6):
    h = {"Authorization": f"token {tok}", "Accept": "application/vnd.github+json",
         "Content-Type": "application/json", "User-Agent": "snad-fix"}
    last = None
    for i in range(1, tries + 1):
        data = json.dumps(payload).encode() if payload is not None else None
        req = urllib.request.Request(url, data=data, method=method, headers=h)
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                body = r.read()
                return r.status, (json.loads(body) if body else {})
        except urllib.error.HTTPError as e:
            last = f"HTTP {e.code}: {e.read()[:160]}"
            if e.code < 500:
                return e.code, {"error": last}
        except Exception as e:
            last = f"{type(e).__name__}: {e}"
        print(f"    重试 {i}/{tries}（{last}）", flush=True)
        time.sleep(3 * i)
    return 0, {"error": last}


def main():
    tok = token()

    ok, ref = call(tok, "GET", f"{API}/git/ref/heads/main")
    if ok != 200:
        print("读不到 main:", ref, flush=True); return 1
    head = ref["object"]["sha"]

    ok, cur = call(tok, "GET", f"{API}/git/commits/{head}")
    if ok != 200:
        print("读不到 HEAD 提交:", cur, flush=True); return 1
    base_tree = cur["tree"]["sha"]
    parent = cur["parents"][0]["sha"]
    print(f"HEAD {head[:8]} tree {base_tree[:8]}", flush=True)

    ok, prev = call(tok, "GET", f"{API}/git/commits/{parent}")
    ok, ptree = call(tok, "GET", f"{API}/git/trees/{prev['tree']['sha']}?recursive=1")
    blob = None
    for e in ptree.get("tree", []):
        if e["path"] == TARGET:
            blob = e["sha"]; break
    if not blob:
        print("历史里找不到该文件的 blob，放弃", flush=True); return 1
    print("历史 blob", blob[:12], flush=True)

    ok, tree = call(tok, "POST", f"{API}/git/trees", {
        "base_tree": base_tree,
        "tree": [{"path": TARGET, "mode": "100644", "type": "blob", "sha": blob}],
    })
    if ok != 201:
        print("建 tree 失败:", tree, flush=True); return 1
    print("新 tree", tree["sha"][:8], flush=True)

    ok, commit = call(tok, "POST", f"{API}/git/commits", {
        "message": "恢复 pc-receiver/SnadProfileForm.cs\n\n"
                   "推送脚本在 blob 上传失败时 continue，而建 tree 是整棵树语义，"
                   "路径缺席等于删除。此提交用已存在的 blob 把它补回。",
        "tree": tree["sha"], "parents": [head],
    })
    if ok != 201:
        print("建 commit 失败:", commit, flush=True); return 1

    ok, res = call(tok, "PATCH", f"{API}/git/refs/heads/main",
                   {"sha": commit["sha"], "force": False})
    if ok != 200:
        print("更新分支失败:", res, flush=True); return 1

    print("已恢复，提交", commit["sha"][:8], flush=True)
    return 0


if __name__ == "__main__":
    sys.exit(main())
