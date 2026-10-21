# 发布 GitHub Release 并上传构建产物，最后校验远端哈希
# 用法：python make_release.py v1.3.0 [说明文件.md] [附件路径 ...]
#   不给附件路径时，默认传 bin/Release/ChillNeteaseSort.dll + dist/*-bundle.zip
#   · tag 已存在会复用那个 release（幂等，重跑不会重复建）
#   · 同名附件已存在会先删再传，避免 asset 堆积
#   · 代理默认不用（本机 Clash 常挂，直连反而稳）；需要时设 GH_PROXY=http://127.0.0.1:7897
import json
import hashlib
import os
import subprocess
import sys
import time
import urllib.request
import urllib.error

REPO = "kevin0521-wjw/ChillNeteaseSort"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_ASSETS = ["bin/Release/ChillNeteaseSort.dll", "dist/ChillNeteaseSort-v1.3.0-bundle.zip"]
PROXY = os.environ.get("GH_PROXY", "").strip()


def token():
    p = subprocess.run(
        ["git", "-c", "credential.helper=manager", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n", capture_output=True, text=True,
        env={k: v for k, v in os.environ.items() if "proxy" not in k.lower()})
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line.split("=", 1)[1].strip()
    raise SystemExit("取不到 GitHub token（credential.helper=manager 没返回 password）")


TOKEN = token()


def opener():
    handlers = []
    if PROXY:
        handlers.append(urllib.request.ProxyHandler({"http": PROXY, "https": PROXY}))
    else:
        handlers.append(urllib.request.ProxyHandler({}))   # 明确不走系统代理
    return urllib.request.build_opener(*handlers)


def api(method, url, body=None, raw=False, ctype="application/json", timeout=90):
    data = None
    if body is not None:
        data = body if isinstance(body, bytes) else json.dumps(body).encode()
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/vnd.github+json")
    req.add_header("User-Agent", "cnsort-release")
    if data is not None:
        req.add_header("Content-Type", ctype)
    try:
        with opener().open(req, timeout=timeout) as r:
            payload = r.read()
            if raw:
                return payload, r.status
            # DELETE 之类成功时是 204 + 空 body，别硬解 JSON
            if not payload:
                return {}, r.status
            return json.loads(payload.decode()), r.status
    except urllib.error.HTTPError as e:
        return {"_error": e.code, "_body": e.read().decode("utf-8", "replace")[:400]}, e.code


def download_asset(asset_id):
    """
    下载附件本体。
    ⚠️ 必须把 Accept 明确设成 application/octet-stream —— 默认是 JSON，
       拿到的会是 1.5 KB 的元数据（第一版就踩了这个，校验报「哈希不一致」）。
       接口会 302 到 objects.githubusercontent.com，urllib 会自动跟随。
    """
    req = urllib.request.Request(
        f"https://api.github.com/repos/{REPO}/releases/assets/{asset_id}")
    req.add_header("Authorization", "Bearer " + TOKEN)
    req.add_header("Accept", "application/octet-stream")
    req.add_header("User-Agent", "cnsort-release")
    with opener().open(req, timeout=120) as r:
        return r.read()


def upload_asset(rid, path):
    """上传一个附件；同名旧附件先删再传（幂等，重跑不会堆一串同名资产）。"""
    name = os.path.basename(path)
    data = open(path, "rb").read()
    sha = hashlib.sha256(data).hexdigest()
    print("")
    print(f"[{name}]  {len(data):,} B  sha256={sha[:16]}…")

    rel, _ = api("GET", f"https://api.github.com/repos/{REPO}/releases/{rid}")
    for a in rel.get("assets") or []:
        if a["name"] == name:
            api("DELETE", f"https://api.github.com/repos/{REPO}/releases/assets/{a['id']}")
            print(f"  删掉同名旧附件 {a['id']}")

    url = (f"https://uploads.github.com/repos/{REPO}/releases/{rid}/assets"
           f"?name={name}")
    up, code = api("POST", url, data, ctype="application/octet-stream", timeout=300)
    if code not in (200, 201):
        raise SystemExit(f"上传 {name} 失败: {code} {up}")
    print(f"  上传成功 asset id={up['id']}  size={up['size']}  state={up['state']}")

    time.sleep(2)
    got = download_asset(up["id"])
    rsha = hashlib.sha256(got).hexdigest()
    ok = (len(got) == len(data)) and (rsha == sha)
    print(f"  回下载 {len(got):,} B  {'✅ 哈希一致' if ok else '❌ 哈希不一致'}")
    if not ok:
        raise SystemExit(f"{name} 校验失败")
    return up["browser_download_url"]


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else "v1.3.0"
    body_file = sys.argv[2] if len(sys.argv) > 2 and sys.argv[2].endswith(".md") else None
    extra = sys.argv[3:] if body_file else sys.argv[2:]

    assets = [os.path.join(ROOT, x) for x in (extra or DEFAULT_ASSETS)]
    assets = [a for a in assets if os.path.exists(a)]
    if not assets:
        raise SystemExit("没有可传的产物。先跑 build.ps1 与 tools/make_bundle.py")

    if body_file:
        note = open(body_file, encoding="utf-8").read()
    else:
        note = "\n".join([
            "见 commit 说明。",
            "",
            "**新用户下 `-bundle.zip`** —— 解压后右键 `install.bat`（管理员）即可，",
            "里面已含 BepInEx + 上游 ChillNetease + 本插件。",
            "",
            "已装好 BepInEx + ChillNetease 的，下载 `ChillNeteaseSort.dll` 覆盖到",
            "`BepInEx/plugins/ChillNeteaseSort/` 即可。",
        ])

    # 1) 建 / 复用 release
    rel, code = api("POST", f"https://api.github.com/repos/{REPO}/releases", {
        "tag_name": tag, "name": tag, "body": note, "draft": False, "prerelease": False,
    })
    if code == 422:      # 已存在 → 取回来
        print("release 已存在，复用")
        rel, code = api("GET", f"https://api.github.com/repos/{REPO}/releases/tags/{tag}")
    if code not in (200, 201):
        raise SystemExit(f"建 release 失败: {code} {rel}")
    rid = rel["id"]
    print(f"release id={rid}  url={rel.get('html_url')}")

    urls = [upload_asset(rid, a) for a in assets]

    print("")
    print("=" * 56)
    print("全部上传并校验通过：")
    for u in urls:
        print("  " + u)
    print("Release 页面: " + str(rel.get("html_url")))
    print("=" * 56)


if __name__ == "__main__":
    main()
