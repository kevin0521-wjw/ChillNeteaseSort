# ChillNetease Sort

给 [ChillNetease](https://github.com/V2tin19/ChillNetease)（《Chill with You: Lo-Fi Story》的网易云音乐插件）加两个能力：

1. **歌单按口味排序** —— 按「听歌次数」或「最近收藏」重排注入到游戏原生播放列表的歌曲
2. **每次开游戏自动联网刷新** —— 启动时重新拉一遍听歌排行/收藏顺序，并把上次恢复的歌单同步成云端最新版本

> 独立插件，**不修改** ChillNetease 的任何文件；卸载本插件即完全恢复原样。

---

## 它解决什么问题

ChillNetease 把网易云歌单注入游戏原生播放器后，顺序永远是歌单在网易云里的原始顺序（红心歌单 = 收藏时间从新到旧，自建歌单 = 添加顺序）。2954 首红心歌里想听自己最常听的那几首，只能往下翻。

本插件在注入前把列表重排成：

| 模式 | 排序依据 | 数据来源 |
|---|---|---|
| `PlayCountAll` | 全部时间听歌次数（多 → 少） | 网易云「听歌排行」全时间 |
| `PlayCountWeek` | 最近一周听歌次数（多 → 少） | 网易云「听歌排行」最近一周 |
| `RecentLiked` | 最近收藏优先 | 红心歌单的 `trackIds` 顺序 |
| `OldestLiked` | 最早收藏优先 | 同上（反向） |
| `Default` | 原样不动 | — |

没听过的歌 / 没收藏的歌不会被打乱 —— 它们按稳定排序跟在后面，保持原来的相对顺序。

---

## 安装

前提：已安装 **ChillNetease 0.3.1+**（本插件是它的补丁，靠它的桥接和面板工作）。

1. 下载 `ChillNeteaseSort.dll`
2. 放进游戏的 `BepInEx\plugins\ChillNeteaseSort\` 目录（文件夹名随意，但**不要**放进 `plugins\ChillNetease\`，那是上游的地盘）
3. 启动游戏

最终目录长这样：

```
Chill with You Lo-Fi Story\
└── BepInEx\
    └── plugins\
        ├── ChillNetease\            ← 上游插件（不动）
        │   ├── ChillNetease.Plugin.dll
        │   ├── ChillNetease.dll
        │   └── QRCoder.dll
        └── ChillNeteaseSort\        ← 本插件
            └── ChillNeteaseSort.dll
```

---

## 使用

### 两个热键

| 热键 | 作用 |
|---|---|
| **F10** | 循环切换排序模式（切换后立刻重排当前已注入的歌单，不自动播放） |
| **F11** | 一键：把「我喜欢的音乐」按当前模式重排后注入游戏播放列表并开始播放 |

**F10 的循环顺序**（屏幕下方会浮一条提示）：

```
歌单原顺序 → 听歌次数（全部时间） → 听歌次数（最近一周）
          → 最近收藏优先 → 最早收藏优先 → 回到原顺序
```

**F11 是最省事的一条路**：不用开 F6 面板翻歌单，进游戏按一下，直接开始放你最常听的那首。
它会把整张红心歌单（比如 2954 首）按当前模式排好，再交给上游注入前 1000 首、从第 0 首开始播。

> 热键可在配置文件里改（`F1`~`F12` / 单字母 / `None`）。
> **别用 F6/F7/F8/F9**：F6 是上游面板、F7/F9 是 Chill Env Sync、F8 是 ChillAI。
> 热键只在游戏窗口处于前台时响应，且做了**边沿检测** —— 按住不放只触发一次。

### 排序是在整张歌单上做的

ChillNetease 注入游戏播放列表时会截窗口，默认只放前 1000 首（`MaxInjectedSongs`）。
本插件是在**整张歌单**上排序，再把排好的完整列表交给上游去截 ——

- 顺序对了：你的真实 Top100 有 **98 首**落在 1000 首窗口里，最前面就是 `KiLLKiSS`(219 次)、`Hotel California (Live)`(101 次)、`霜雪千年`(67 次)…
- 如果只排前 1000 首再截，Top100 里只有 **5 首**能进窗口（因为真正常听的歌原本散落在 2954 首各处）

### 生效时机

| 场景 | 行为 |
|---|---|
| 按 **F11** | 拉红心歌单 → 按当前模式全量排序 → 注入并播放第 0 首 |
| 在 F6 面板里点歌单 | 按当前模式排序后注入 |
| 按 **F10** 改排序模式 | 立刻重排当前歌单 |
| 切歌 / 点歌 | 沿用已注入的顺序 |
| 重启游戏 | 先加载上次缓存排序 → 后台联网刷新 → 恢复歌单时会同步云端最新版本 |
| 漫游 FM / 搜索结果 | **不排序**（保持推荐/相关度顺序） |

### 配置文件

`BepInEx\config\com.kevin.chillneteasesort.cfg`

| 键 | 默认 | 说明 |
|---|---|---|
| `Sort.Mode` | `PlayCountAll` | 当前排序模式，游戏内切换会自动写回 |
| `Sort.CycleHotkey` | `F10` | 循环切换热键 |
| `Sort.InjectLikedHotkey` | `F11` | 一键注入红心歌单热键 |
| `Refresh.AutoRefreshOnStartup` | `true` | 每次启动联网刷新口味档案 |
| `Refresh.SyncPlaylistOnStartup` | `true` | 启动时把恢复的歌单同步成云端最新 |
| `Data.LikeTrackIdsNewestFirst` | `true` | 红心歌单 `trackIds` 是否「最新收藏在前」（方向反了就改 false） |
| `Data.ManualCookie` | 空 | 一般留空，自动读 go-musicfox 的 cookie |
| `UI.ShowToast` | `true` | 切换时屏幕提示 |

---

## 数据是怎么拿到的

ChillNetease 的原生桥 `ChillNetease.dll` 是 Go 编译的二进制，只导出 35 个固定函数，**里面没有听歌排行/收藏顺序接口**，也没法改那个二进制。所以本插件走网易云几个至今**不需要 weapi/eapi 加密**的老接口，带 `MUSIC_U` cookie 直接 GET：

```
GET api/nuser/account/get                       → uid
GET api/user/playlist?uid=&limit=1000           → 找 specialType=5 的「我喜欢的音乐」
GET api/v1/play/record?uid=&type=0              → allData[]（含 playCount）
GET api/v1/play/record?uid=&type=1              → weekData[]
GET api/v6/playlist/detail?id=&n=1000&s=8       → trackIds[]（收藏顺序）
```

cookie 用的是 `%LOCALAPPDATA%\go-musicfox\cookie` —— 也就是你在游戏里 F6 扫码登录后，原生桥自己写的那份，所以**永远与游戏登录态一致**，不用二次配置。

结果缓存到 `BepInEx\config\ChillNeteaseSort.cache.json`，断网时照样能按上次数据排序。

### 为什么用独立插件而不是 fork 上游

| | 独立插件（本方案） | fork ChillNetease |
|---|---|---|
| 体积 | ~40 KB | 16 MB（要一起分发原生 DLL） |
| 上游更新 | 互不干扰 | 每次都要 merge |
| 出问题 | 删掉 DLL 即恢复 | 要重装 |
| 代价 | 依赖上游方法签名（已全部做 try/catch 降级） | — |

---

## 已知限制

- **听歌排行服务端最多返回 100 首**（`allData`/`weekData` 都只有这个量级）。所以只有你真正听过、且进了 Top100 的歌才有次数权重，其余按原顺序跟在后面。这是网易云接口的硬上限，绕不过。
- 「最近收藏」依赖红心歌单的 `trackIds` 顺序。如果你在网易云 App 里给红心歌单换过排序（比如按歌手排），这个顺序会跟着变。
- 只对「注入游戏播放列表」的歌单生效；漫游 FM 与搜索结果保持原样。
- 需要 `ChillNetease 0.3.1+`。上游大改（类名/方法签名）会导致某些补丁安装失败 —— 日志里会写明哪几个失败，其余功能仍正常。

---

## 自行构建

```powershell
# 需要 .NET SDK 6+
.\build.ps1                                    # 产物在 dist\ChillNeteaseSort.dll
.\build.ps1 -Install                           # 顺便装进游戏
.\build.ps1 -GameDir "D:\Steam\...\Chill with You Lo-Fi Story" -Install
```

工程引用的是**游戏目录里的** `BepInEx.dll` / `0Harmony.dll` / `ChillNetease.Plugin.dll` / Unity 程序集，`Private=false`，不会打进产物。

---

## 许可

MIT。本插件不包含 ChillNetease 的任何代码或二进制，仅在运行时通过 Harmony 对它的方法做前后缀补丁。
