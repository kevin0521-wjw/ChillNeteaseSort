#!/usr/bin/env python
# -*- coding: utf-8 -*-
"""
把「BepInEx 5 + 上游 ChillNetease + 本插件」打成一个 zip，新用户解压双击即可。

为什么自带 BepInEx：绝大多数人装 Mod 的第一道坎就是「BepInEx 怎么装」——
winhttp.dll 放错位置、Ddoorstop 没配好、Unity 版本不对，各种坑。
既然我们依赖它，就一起分发，省掉这一整段。

⚠️ 两个网络坑（都实测过）：
  1. BepInEx 的 release 资产是 win32/x64 两个 zip，**不能靠 URL 猜**，
     要先 GET release JSON 读 assets[].name（名字里含 win-x64）。
  2. 下载 GitHub 资产必须带 `Accept: application/octet-stream`，
     否则拿回来的是 1.5 KB 的 JSON 元数据。

用法：
  python tools/make_bundle.py                    # 打 zip 到 dist/
  python tools/make_bundle.py --bepinex <zip>    # 复用已下载的 BepInEx 压缩包
  GH_PROXY=http://127.0.0.1:7897 python tools/make_bundle.py   # 走代理
"""

import argparse
import hashlib
import io
import json
import os
import shutil
import sys
import tempfile
import urllib.request
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)

REPO = "kevin0521-wjw/ChillNeteaseSort"
BEPINEX_TAG = "v5.4.23.5"

# 上游 ChillNetease 的 release：只取它 3 个 dll，本插件自己编
UPSTREAM_REPO = "V2tin19/ChillNetease"
UPSTREAM_TAG = "0.3.1"

PROXY = os.environ.get("GH_PROXY", "").strip()
PX = ["-x", PROXY] if PROXY else []

UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"


def log(msg):
    print(msg, flush=True)


def api_get(url, accept="application/vnd.github+json"):
    """带 cookie/token 的 API GET。⚠️ 设了 HTTPS_PROXY 时 credential fill 会拿不到值，
    所以代理只挂 curl / 这里显式传，别 export。"""
    req = urllib.request.Request(url)
    req.add_header("Accept", accept)
    req.add_header("User-Agent", UA)
    with urllib.request.urlopen(req, timeout=60) as r:
        return r.read()


def gh_token():
    """从 Git 凭据里取 token（不设 HTTPS_PROXY，否则这一步会空 → 401）。"""
    if os.environ.get("GH_TOKEN"):
        return os.environ["GH_TOKEN"].strip()
    import subprocess
    p = subprocess.run(
        ["git", "-c", "credential.helper=manager", "credential", "fill"],
        input=b"protocol=https\nhost=github.com\n\n",
        capture_output=True,
    )
    for line in p.stdout.decode("utf-8", "ignore").splitlines():
        if line.startswith("password="):
            return line[9:].strip()
    return ""


def curl_download(url, dest, headers=None, expect_min=1024):
    """用 curl 下（GitHub 大文件 + 代理都靠它，比 urllib 稳）。

    ⚠️ `objects.githubusercontent.com` 在国内常被重置（curl 56），所以要多路回退：
      带认证 → 不带认证 → 走代理 → urllib 直连。四条都失败才算真失败。
    """
    import subprocess

    tok = gh_token()

    def run(u, hdrs, extra):
        cmd = ["curl", "-sSL", "--fail", "--retry", "3", "--retry-delay", "2",
               "--max-time", "300", "-o", dest]
        for h in hdrs:
            cmd += ["-H", h]
        cmd += extra
        cmd.append(u)
        return subprocess.run(cmd, capture_output=True, text=True)

    attempts = []
    if tok:
        attempts.append(([f"Authorization: Bearer {tok}"], [], "带 token 直连"))
    attempts.append(([], [], "裸直连"))
    attempts.append(([f"Authorization: Bearer {tok}"] if tok else [], PX, "走代理"))
    attempts.append(([], PX, "裸代理"))

    base_h = ["User-Agent: " + UA]
    last = ""
    for hdrs, extra, tag in attempts:
        r = run(url, base_h + hdrs, extra)
        if r.returncode == 0 and os.path.exists(dest) and os.path.getsize(dest) >= expect_min:
            log(f"   [{tag}] {os.path.getsize(dest)} B")
            return dest
        last = (r.stderr or "")[-300:]
        log(f"   [{tag}] 失败，换下一路")
    raise RuntimeError("下载失败（已试直连/代理/带认证）：" + last)


def collect_bepinex_local(game_dir, stage):
    """★ 默认路径：从本机已装好的游戏目录收集 BepInEx，不下载。

    两个理由：
      1. `github.com` 这个域名在境内常被阻断（`api.github.com` 通，但 release 资产会
         302 到 github.com 然后被重置），下载十有八九失败；
      2. 更要紧的是 —— **不能把本机的 config/ 一起发出去**。那里面有
         `ChillNetease.playlists.json`（我自己的歌单）、`com.kevin.chillneteasesort.cfg`
         （个人配置）、BepInEx.cfg 等。往干净目录拷天然避免这些。
    """
    src = os.path.join(game_dir, "BepInEx")
    if not os.path.isdir(src):
        raise RuntimeError(f"本机游戏目录里没有 BepInEx：{src}")
    log(f"从本机收集 BepInEx：{src}")

    dst = os.path.join(stage, "BepInEx")
    shutil.copytree(src, dst, ignore=shutil.ignore_patterns(
        "cache", "config", "LogOutput.log*", "*.log", "doorstop_config.ini"))
    # plugins/ 与 patchers/ 下面统一重放，别把别的 mod 混进来
    for sub in ("plugins", "patchers"):
        p = os.path.join(dst, sub)
        if os.path.isdir(p):
            shutil.rmtree(p)

    n = sum(len(f) for _, _, f in os.walk(dst))
    log(f"   收集到 {n} 个文件")
    return dst


def download_bepinex(dest_zip, tag=BEPINEX_TAG):
    """下 BepInEx 的 win-x64 发行包（备用路径，见 collect_bepinex_local）。"""
    if os.path.exists(dest_zip) and os.path.getsize(dest_zip) > 3 * 1024 * 1024:
        log(f"BepInEx 压缩包已存在，复用: {dest_zip} ({os.path.getsize(dest_zip)} B)")
        return dest_zip

    meta = json.loads(api_get(
        f"https://api.github.com/repos/BepInEx/BepInEx/releases/tags/{tag}").decode("utf-8"))
    assets = meta.get("assets", [])
    log(f"BepInEx {tag} 资产 {len(assets)} 个：")
    for a in assets:
        log(f"   {a['name']}  {a['size']} B")

    # ⚠️ 不猜 URL，从名字里挑出 x64 的那个
    pick = None
    for a in assets:
        n = a["name"].lower()
        if n.endswith(".zip") and ("x64" in n or "64" in n) and "win" in n:
            pick = a
            break
    if pick is None:
        for a in assets:
            if a["name"].lower().endswith(".zip"):
                pick = a
                break
    if pick is None:
        raise RuntimeError("没找到 BepInEx 发行 zip")

    url = pick["browser_download_url"]
    log(f"下载 {pick['name']} ({pick['size']} B)")
    # 资产大小已知，用它当下限校验：拿到 HTML 错误页或被截断都能立刻发现
    curl_download(url, dest_zip, expect_min=int(pick["size"] * 0.98))
    got = os.path.getsize(dest_zip)
    log(f"拿到 {got} B")
    if got < 3 * 1024 * 1024:
        raise RuntimeError(f"下载的文件太小（{got} B），多半拿到的是错误页面")
    return dest_zip


UPSTREAM_FILES = ("ChillNetease.Plugin.dll", "ChillNetease.dll", "QRCoder.dll")


def collect_upstream_local(game_dir, dest_dir):
    """★ 优先从本机游戏目录拿上游的 3 个 dll —— 理由同 collect_bepinex_local：
    github.com 域名在境内基本下不动，而且本机那份本来就是跑通过的正版。"""
    os.makedirs(dest_dir, exist_ok=True)
    src = os.path.join(game_dir, "BepInEx", "plugins", "ChillNetease")
    out = {}
    for n in UPSTREAM_FILES:
        p = os.path.join(src, n)
        if os.path.exists(p) and os.path.getsize(p) > 1000:
            dst = os.path.join(dest_dir, n)
            shutil.copy2(p, dst)
            out[n] = dst
    if len(out) == len(UPSTREAM_FILES):
        log(f"从本机取上游 dll：{src}")
        return out
    missing = [n for n in UPSTREAM_FILES if n not in out]
    log(f"   本机只找到 {len(out)}/{len(UPSTREAM_FILES)}，改从 GitHub 补 {missing}")
    return None


def download_upstream(dest_dir, tag=UPSTREAM_TAG):
    """从上游 release 取 ChillNetease 的 3 个 dll。"""
    os.makedirs(dest_dir, exist_ok=True)
    have = {n: os.path.join(dest_dir, n) for n in UPSTREAM_FILES}
    if all(os.path.exists(p) and os.path.getsize(p) > 1000 for p in have.values()):
        log("上游 dll 已存在，复用")
        return have

    meta = json.loads(api_get(
        f"https://api.github.com/repos/{UPSTREAM_REPO}/releases/tags/{tag}").decode("utf-8"))
    assets = meta.get("assets", [])
    log(f"上游 {UPSTREAM_REPO} {tag} 资产 {len(assets)} 个")
    for a in assets:
        log(f"   {a['name']}  {a['size']} B")

    by_name = {a["name"]: a for a in assets}
    out = {}
    for n in UPSTREAM_FILES:
        dst = os.path.join(dest_dir, n)
        if os.path.exists(dst) and os.path.getsize(dst) > 1000:
            out[n] = dst
            continue
        a = by_name.get(n)
        if a is None:
            raise RuntimeError(f"上游 release 里没有 {n}；可用资产: {list(by_name)}")
        log(f"下载 {n} ({a['size']} B)")
        curl_download(a["browser_download_url"], dst,
                      expect_min=int(a["size"] * 0.98))
        out[n] = dst
    return out


def get_upstream(game_dir, dest_dir):
    """优先本机，其次网络。"""
    if game_dir:
        got = collect_upstream_local(game_dir, dest_dir)
        if got:
            return got
    return download_upstream(dest_dir)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for blk in iter(lambda: f.read(1 << 20), b""):
            h.update(blk)
    return h.hexdigest()


INSTALL_BAT = r"""@echo off
rem ChillNeteaseSort one-click installer
rem Copies BepInEx + both plugins into the Chill with You install folder
rem
rem NOTE: keep this file pure ASCII + CRLF, cmd's GBK codepage mangles anything else.
rem    The Python side writes it as ASCII + CRLF (see write below).
setlocal enabledelayedexpansion

rem CRITICAL: cmd runs a .bat with the CALLER's working directory, not the
rem script's own folder. Without this line every ".\BepInEx" below resolves
rem against wherever the user happened to double-click from, and the copy
rem silently does nothing.
cd /d "%~dp0"

echo ============================================================
echo   ChillNeteaseSort installer
echo   NetEase Cloud Music mod for Chill with You: Lo-Fi Story
echo ============================================================
echo.

rem ---- 1. locate game folder ----
set "GAMEDIR=%~1"
if "%GAMEDIR%"=="" (
  for %%D in (
    "%ProgramFiles(x86)%\Steam\steamapps\common\Chill with You Lo-Fi Story"
    "%ProgramFiles%\Steam\steamapps\common\Chill with You Lo-Fi Story"
    "D:\Steam\steamapps\common\Chill with You Lo-Fi Story"
    "D:\SteamLibrary\steamapps\common\Chill with You Lo-Fi Story"
    "E:\Steam\steamapps\common\Chill with You Lo-Fi Story"
    "E:\SteamLibrary\steamapps\common\Chill with You Lo-Fi Story"
    "F:\Steam\steamapps\common\Chill with You Lo-Fi Story"
  ) do (
    if exist "%%~D\Chill With You_Data" if not defined GAMEDIR set "GAMEDIR=%%~D"
  )
)

if "%GAMEDIR%"=="" (
  set /p "GAMEDIR=Game folder not found. Please type the path: "
)
echo Game folder: %GAMEDIR%

if not exist "%GAMEDIR%\Chill With You_Data" (
  echo.
  echo [ERROR] No "Chill With You_Data" here -- this is not the game folder.
  echo         Check the real install path in your Steam library.
  echo.
  pause
  exit /b 1
)

rem ---- 2. copy files ----
echo.
echo Installing...
rem Do NOT trust xcopy's exit code: it returns 1 when 0 files were copied,
rem and non-zero when some files are locked, even if the copy mostly worked.
robocopy ".\BepInEx" "%GAMEDIR%\BepInEx" /E /NFL /NDL /NJH /NJS /NS /NC /NP >nul
rem robocopy exit codes 0..7 are all success; only >=8 is a real error

copy /Y ".\doorstop_config.ini" "%GAMEDIR%\doorstop_config.ini" >nul
copy /Y ".\winhttp.dll" "%GAMEDIR%\winhttp.dll" >nul

rem ---- 3. verify the critical files landed ----
set "FAILED="
if not exist "%GAMEDIR%\BepInEx\core\BepInEx.dll" set "FAILED=!FAILED! BepInEx.dll"
if not exist "%GAMEDIR%\winhttp.dll" set "FAILED=!FAILED! winhttp.dll"
if not exist "%GAMEDIR%\doorstop_config.ini" set "FAILED=!FAILED! doorstop_config.ini"
if not exist "%GAMEDIR%\BepInEx\plugins\ChillNetease\ChillNetease.dll" set "FAILED=!FAILED! ChillNetease.dll"
if not exist "%GAMEDIR%\BepInEx\plugins\ChillNeteaseSort\ChillNeteaseSort.dll" set "FAILED=!FAILED! ChillNeteaseSort.dll"

if not "!FAILED!"=="" (
  echo.
  echo [ERROR] These files are missing:!FAILED!
  echo         Try running this installer as Administrator.
  echo.
  pause
  exit /b 1
)

echo.
echo ============================================================
echo   DONE
echo ============================================================
echo.
echo Next:
echo   1. Launch the game from Steam (first run lets BepInEx install Mono, can be slow)
echo   2. In game press F6, scan the QR code with the NetEase Cloud Music app
echo   3. Press F4 to import your library (liked / daily / charts / own playlists)
echo.
echo Full guide: README.md in this folder
echo.
pause
"""


README_TXT = r"""ChillNeteaseSort 安装包
=========================

本包包含：
  BepInEx\core\               插件加载器 5.4.23.5（win-x64）
  BepInEx\plugins\ChillNetease\     上游网易云桥（V2tin19/ChillNetease 0.3.1）
  BepInEx\plugins\ChillNeteaseSort\  本插件（排序 + 我的音乐库 + 日推）
  winhttp.dll / doorstop_config.ini  注入用，要放在游戏目录根下（install.bat 会拷）

安装：右键 install.bat → 以管理员身份运行 → 确认游戏目录 → 等它说装完
详细用法见 GitHub README：https://github.com/kevin0521-wjw/ChillNeteaseSort

许可：本插件 MIT；BepInEx LGPL-3.0；ChillNetease 见其仓库。版权归各自作者。
"""


def build(out_zip, version, game_dir="", bepinex_zip=""):
    tmp = tempfile.mkdtemp(prefix="chillbundle_")
    stage = os.path.join(tmp, "ChillNeteaseSort-" + version)
    os.makedirs(stage, exist_ok=True)
    try:
        # --- BepInEx ---
        if bepinex_zip:
            log("从压缩包取 BepInEx…")
            bex = os.path.join(tmp, "bepinex")
            with zipfile.ZipFile(bepinex_zip) as z:
                z.extractall(bex)
            root = None
            for dirpath, dirnames, filenames in os.walk(bex):
                if "winhttp.dll" in filenames and "doorstop_config.ini" in filenames:
                    root = dirpath
                    break
            if root is None:
                raise RuntimeError("BepInEx 压缩包里没找到 winhttp.dll")
            log(f"BepInEx 根目录: {root}")
            dst = os.path.join(stage, "BepInEx")
            shutil.copytree(root, dst)
            for fn in ("winhttp.dll", "doorstop_config.ini"):
                src = os.path.join(root, fn)
                if os.path.exists(src):
                    shutil.copy2(src, os.path.join(stage, fn))
            for junk in ("LogOutput.log", "LogOutput.log.1", "LogOutput.log.2"):
                p = os.path.join(dst, junk)
                if os.path.exists(p):
                    os.remove(p)
            for sub in ("config", "cache"):
                p = os.path.join(dst, sub)
                if os.path.isdir(p):
                    shutil.rmtree(p)
            for sub in ("plugins", "patchers"):
                p = os.path.join(dst, sub)
                if os.path.isdir(p):
                    shutil.rmtree(p)
        else:
            collect_bepinex_local(game_dir, stage)
            # doorstop 是注入关键，从本机拷一份原样的
            for fn in ("winhttp.dll", "doorstop_config.ini"):
                src = os.path.join(game_dir, fn)
                if os.path.exists(src):
                    shutil.copy2(src, os.path.join(stage, fn))
                else:
                    log(f"   ⚠️ 本机没有 {fn}，包会缺注入文件")

        # --- 上游插件 ---
        log("取上游 ChillNetease…")
        up = get_upstream(game_dir, os.path.join(tmp, "upstream"))
        dstup = os.path.join(stage, "BepInEx", "plugins", "ChillNetease")
        os.makedirs(dstup, exist_ok=True)
        for n, p in up.items():
            shutil.copy2(p, os.path.join(dstup, n))
            log(f"   {n}  {os.path.getsize(p)} B")

        # --- 本插件 ---
        mine = os.path.join(ROOT, "bin", "Release", "ChillNeteaseSort.dll")
        if not os.path.exists(mine):
            mine = os.path.join(ROOT, "dist", "ChillNeteaseSort.dll")
        if not os.path.exists(mine):
            raise RuntimeError("找不到编译好的 ChillNeteaseSort.dll，先跑 build.ps1")
        dstmine = os.path.join(stage, "BepInEx", "plugins", "ChillNeteaseSort")
        os.makedirs(dstmine, exist_ok=True)
        shutil.copy2(mine, os.path.join(dstmine, "ChillNeteaseSort.dll"))
        log(f"   ChillNeteaseSort.dll  {os.path.getsize(mine)} B")

        # --- 文档 ---
        # ⚠️ install.bat 存成 UTF-8 的话，cmd（默认 GBK 代码页）里中文全是乱码。
        # 现在 bat 正文已改成纯英文，但注释里还有中文，所以仍然按 GBK 写 + CRLF。
        bat = os.path.join(stage, "install.bat")
        try:
            body = INSTALL_BAT.encode("gbk")
        except UnicodeEncodeError:
            body = INSTALL_BAT.encode("utf-8-sig")   # 实在有字符没法转就退回 UTF-8 BOM
            log("   ⚠️ 有字符无法转 GBK，install.bat 改用 UTF-8 BOM")
        with open(bat, "wb") as f:
            f.write(body.replace(b"\r\n", b"\n").replace(b"\n", b"\r\n"))
        with open(os.path.join(stage, "README.txt"), "w", encoding="utf-8",
                  newline="\r\n") as f:
            f.write(README_TXT)
        src_readme = os.path.join(ROOT, "README.md")
        if os.path.exists(src_readme):
            shutil.copy2(src_readme, os.path.join(stage, "README.md"))

        # --- 打 zip ---
        os.makedirs(os.path.dirname(out_zip) or ".", exist_ok=True)
        if os.path.exists(out_zip):
            os.remove(out_zip)
        log(f"打包 -> {out_zip}")
        with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
            for dirpath, dirnames, filenames in os.walk(stage):
                for fn in filenames:
                    full = os.path.join(dirpath, fn)
                    rel = os.path.relpath(full, os.path.dirname(stage))
                    z.write(full, rel.replace("\\", "/"))
        size = os.path.getsize(out_zip)
        log(f"完成：{size:,} B（{size/1024/1024:.2f} MB）")
        log(f"sha256 = {sha256(out_zip)}")
        return out_zip
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--version", default="1.3.0")
    ap.add_argument("--gamedir", default=DEFAULT_GAME_DIR,
                    help="从本机这个已装好的游戏目录收集 BepInEx（默认自动探测 Steam 路径）")
    ap.add_argument("--download", action="store_true",
                    help="改成从 GitHub 下 BepInEx 官方包（境内 github.com 常被阻断，会失败）")
    ap.add_argument("--bepinex", default="", help="配合 --download：本地已有的 BepInEx 压缩包")
    ap.add_argument("--out", default="")
    args = ap.parse_args()

    out = args.out or os.path.join(ROOT, "dist",
                                   f"ChillNeteaseSort-v{args.version}-bundle.zip")

    bex = ""
    if args.download or args.bepinex:
        bex = args.bepinex or os.path.join(
            tempfile.gettempdir(), f"bepinex_{BEPINEX_TAG}_win_x64.zip")
        bex = download_bepinex(bex)
        gamedir = ""
    else:
        gamedir = detect_game_dir(args.gamedir)
        if not gamedir:
            raise SystemExit(
                "没找到游戏目录。装过 ChillNetease 的话游戏里应该有 BepInEx 目录，"
                "用 --gamedir 指定，或改用 --download 从网上下 BepInEx。")

    build(out, args.version, game_dir=gamedir, bepinex_zip=bex)
    log("\n下一步：tools/make_release.py 上传两个产物到 GitHub Release")


def detect_game_dir(hint=""):
    """在常见 Steam 库里找已装好的游戏（含 BepInEx 就更说明能用）。"""
    cands = []
    if hint:
        cands.append(hint)
    cands += [
        r"C:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story",
        r"C:\Program Files\Steam\steamapps\common\Chill with You Lo-Fi Story",
    ]
    for d in "DEFGH":
        cands.append(d + r":\Steam\steamapps\common\Chill with You Lo-Fi Story")
        cands.append(d + r":\SteamLibrary\steamapps\common\Chill with You Lo-Fi Story")
    cands.append(r"D:\SteamLibrary\steamapps\common\Chill with You Lo-Fi Story")
    for c in cands:
        if os.path.isdir(os.path.join(c, "Chill With You_Data")):
            return c
    return ""


DEFAULT_GAME_DIR = ""


if __name__ == "__main__":
    sys.exit(main())