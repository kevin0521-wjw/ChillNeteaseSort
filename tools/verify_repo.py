# 逐文件核对：远端 blob sha == 本地 git hash-object
import json, subprocess, sys, os

REPO = "kevin0521-wjw/ChillNeteaseSort"
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PROXY = "http://127.0.0.1:7897"


def token():
    p = subprocess.run(["git", "-c", "credential.helper=manager", "credential", "fill"],
                       input="protocol=https\nhost=github.com\n\n", capture_output=True, text=True,
                       env={k: v for k, v in os.environ.items() if "proxy" not in k.lower()})
    for line in p.stdout.splitlines():
        if line.startswith("password="):
            return line[9:].strip()
    return ""


TOKEN = token()
p = subprocess.run(["curl", "-s", "-x", PROXY, "-H", "Authorization: token " + TOKEN,
                    f"https://api.github.com/repos/{REPO}/git/trees/main?recursive=1"],
                   capture_output=True, text=True, encoding="utf-8")
tree = json.loads(p.stdout).get("tree", [])

bad = 0
for item in tree:
    if item["type"] != "blob":
        continue
    path = item["path"]
    local_path = os.path.join(ROOT, path.replace("/", os.sep))
    if not os.path.exists(local_path):
        print(f"❌ 远端有、本地无: {path}")
        bad += 1
        continue
    lp = subprocess.run(["git", "hash-object", path], cwd=ROOT, capture_output=True, text=True)
    local_sha = lp.stdout.strip()
    ok = local_sha == item["sha"]
    print(f"{'✅' if ok else '❌'} {item['size']:>6}B  {path}")
    if not ok:
        print(f"    remote={item['sha'][:12]}  local={local_sha[:12]}")
        bad += 1

print()
print("全部一致 ✅" if bad == 0 else f"{bad} 个文件不一致 ❌")
sys.exit(1 if bad else 0)
