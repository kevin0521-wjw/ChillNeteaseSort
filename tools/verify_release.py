# 校验 GitHub Release 附件能否正常下载，并与本地构建产物比对 sha256
import json, subprocess, time, hashlib, os, sys

REPO = "kevin0521-wjw/ChillNeteaseSort"
PROXY = "http://127.0.0.1:7897"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
LOCAL = os.path.join(ROOT, "bin", "Release", "ChillNeteaseSort.dll")
OUT = os.path.join(os.environ.get("TEMP", "/tmp"), "cnsort_dl.dll")


def token():
    p = subprocess.run(
        ["git", "-c", "credential.helper=manager", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n", capture_output=True, text=True,
        env={k: v for k, v in os.environ.items() if "proxy" not in k.lower()})
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[9:].strip()
    return ""


def api(path):
    p = subprocess.run(["curl", "-s", "-x", PROXY, "-H", "Authorization: token " + TOKEN,
                        "https://api.github.com" + path], capture_output=True, text=True, encoding="utf-8")
    try:
        return json.loads(p.stdout)
    except Exception:
        return {}


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


TOKEN = token()
print("token_len =", len(TOKEN))

rel = api(f"/repos/{REPO}/releases/tags/v1.0.0")
assets = rel.get("assets") or []
if not assets:
    print("❌ 没找到附件:", rel.get("message"))
    sys.exit(1)
a = assets[0]
print(f"asset: {a['name']}  size={a['size']}  state={a['state']}")

# 用 API 的 application/octet-stream 端点下载（走 api.github.com，比 releases/download 稳）
for i in range(6):
    p = subprocess.run(["curl", "-sL", "-x", PROXY, "--max-time", "60",
                        "-H", "Authorization: token " + TOKEN,
                        "-H", "Accept: application/octet-stream",
                        f"https://api.github.com/repos/{REPO}/releases/assets/{a['id']}",
                        "-o", OUT], capture_output=True)
    size = os.path.getsize(OUT) if os.path.exists(OUT) else 0
    print(f"  下载尝试 {i+1}: {size} 字节")
    if size == a["size"]:
        break
    time.sleep(2)

if not os.path.exists(OUT) or os.path.getsize(OUT) != a["size"]:
    print("❌ 下载不完整")
    sys.exit(1)

h_local, h_remote = sha256(LOCAL), sha256(OUT)
print("本地 sha256 =", h_local)
print("远端 sha256 =", h_remote)
print("✅ 附件与本地构建产物完全一致" if h_local == h_remote else "❌ 哈希不一致")
