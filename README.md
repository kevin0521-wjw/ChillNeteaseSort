# Steam游戏ChillNetease-放松时光-与你共享的Lo-Fi故事mod

> 给 [ChillNetease](https://github.com/V2tin19/ChillNetease)（《Chill with You: Lo-Fi Story》的网易云音乐插件）加三件事：
>
> 1. **歌单按口味排序** —— 按「听歌次数」「收藏顺序」「专辑」「歌手」重排注入到游戏原生播放列表的歌曲
> 2. **每次开游戏自动联网刷新** —— 启动时重新拉一遍听歌排行/收藏顺序，并把上次恢复的歌单同步成云端最新版本
> 3. **把任意歌单单独放成一个歌单** —— 配好 id，一键把「我喜欢的音乐」以外的歌单拉出来独立播放、独立存档
>
> 独立插件，**不修改** ChillNetease 的任何文件；卸载本插件即完全恢复原样。

**下载：[最新 Release](https://github.com/kevin0521-wjw/ChillNeteaseSort/releases/latest)**（`ChillNeteaseSort.dll`，~55 KB）· MIT

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
| `Album` | **按专辑归组**（同专辑连着放，组内保持原顺序） | 本地歌曲元数据 |
| `Artist` | **按歌手归组**（同歌手的连着放） | 本地歌曲元数据 |
| `Title` | **按歌名**（同名相邻） | 本地歌曲元数据 |
| `Default` | 原样不动 | — |

没听过的歌 / 没收藏的歌不会被打乱 —— 它们按稳定排序跟在后面，保持原来的相对顺序。

> 「按专辑 / 歌手 / 歌名」只在你**点开歌单**时生效；点「导入」分组里自己挑的歌单时保持你挑的顺序。

---

## 安装

前提：已安装 **ChillNetease 0.3.1+**（本插件是它的补丁，靠它的桥接和面板工作）。

1. 从 [Releases](https://github.com/kevin0521-wjw/ChillNeteaseSort/releases/latest) 下载 `ChillNeteaseSort.dll`（或自己 `build.ps1` 编）
2. 放进游戏的 `BepInEx\plugins\ChillNeteaseSort\` 目录（文件夹名随意，但**不要**放进 `plugins\ChillNetease\`，那是上游的地盘）
3. 启动游戏，进游戏后按 **F11** 试一下

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

### 四个热键

| 热键 | 作用 |
|---|---|
| **F10** | 循环切换排序模式（切换后立刻重排当前已注入的歌单，不自动播放） |
| **F11** | 一键：把「我喜欢的音乐」**全部曲目**（不受 1000 首截断）按当前模式重排后注入游戏播放列表并开始播放 |
| **F12** | 一键：把 `Data.ExtraPlaylists` 里配的每张歌单**各存成一个独立歌单**，并在面板里直接打开 |
| **F5** | 一键：拉今天的网易云**「每日推荐」**（日推，约 30~35 首）存成一张歌单并弹出面板 |

**F10 的循环顺序**（屏幕下方会浮一条提示）：

```
歌单原顺序 → 听歌次数（全部时间） → 听歌次数（最近一周）
          → 最近收藏优先 → 最早收藏优先
          → 按专辑归组 → 按歌手归组 → 按歌名 → 回到原顺序
```

**F11 是最省事的一条路**：不用开 F6 面板翻歌单，进游戏按一下，直接开始放你最常听的那首。
它会把整张红心歌单（比如 2954 首）按当前模式排好，再交给上游注入前 1000 首、从第 0 首开始播。

> 热键可在配置文件里改（`F1`~`F24` / 单字母 / `None`）。
> **别用 F6/F7/F8/F9**：F6 是上游面板、F7/F9 是 Chill Env Sync、F8 是 ChillAI。
> 热键只在游戏窗口处于前台时响应，且做了**边沿检测** —— 按住不放只触发一次。

### 把「另一张歌单」单独放出来（F12）

ChillNetease 只会从面板里点开歌单才能放。如果你想要的是**「我喜欢的音乐」以外的那批歌**
（比如自己在网易云收藏的某个合集、或者某个 UP 主整理好的场景 BGM 单），配一下 id 就行：

1. 在网易云网页/App 打开那张歌单，网址里 `playlist?id=` 后面那串数字就是 id
2. 编辑 `BepInEx\config\com.kevin.chillneteasesort.cfg`：

   ```ini
   [Data]
   ## 额外歌单：id:名称, id:名称
   ExtraPlaylists = 1374598567:重返未来1999 Scene BGM, 5012345:我的精选
   ```

3. 进游戏按 **F12**

会发生什么：

- 每张歌单都被拉全量、按当前排序模式排好，**各自存成本地歌单**（F6 面板「导入」分组里能看到，重启游戏仍在）
- 面板的「收藏」分组也会立刻刷新，能看到这几张歌单
- **面板会直接停在那张歌单的曲目列表上**（分组自动切到「导入」），想听就 Enter
- 不会打断你正在放的歌 —— 只是想多一个歌单时，不该被硬塞一首新歌

> 想做「几张歌单合成一个」的听感：把它们的 id 都填进去，按 F12 之后在「导入」分组里逐张切。

### 听「每日推荐」（F5）

网易云的日推是**每天换一次**的私人推荐，接口是 `GET /api/v3/discovery/recommend/songs`（免加密，只要 cookie）。

进游戏按 **F5**：

1. 拉今天的日推（通常 30~35 首）
2. 存成一张本地歌单「每日推荐 10-21」（每天覆盖同一张，不会越堆越多）
3. 面板自动弹出，直接停在曲目列表上

> 日推**不参与排序**：它的价值就在网易云给的推荐顺序，插件的排序补丁会按 id 集合识别出这张歌单并跳过重排。
> 不想被面板挡住画面，把 `UI.OpenPanelOnDaily` 改成 `false`，之后手动按 F6 一样能看到这张歌单。
> 想真正合并成一张大歌单，先在网易云里建一张自建歌单把它们加进去，然后直接填那张歌单的 id。

#### 关于 B 站合集 / 视频

B 站合集（比如 `space.bilibili.com/xxx/lists/517552`）是**视频**，不是网易云歌单 ——
两个平台音源不同，插件没法把视频直接变成歌。

而且游戏（准确说是上游 ChillNetease 的原生桥）只会播**网易云的流**，所以 QQ 音乐 / Spotify / B 站直链
在这条链路上都不通用。想听到视频里的音频，只有三条路：

| 路径 | 做法 | 代价 |
|---|---|---|
| **A. 找现成歌单**（推荐先试） | 网易云里搜同名 OST 合集，填 `id` 按 F12 | 零成本；但独占曲目（比如 B 站限定的场景曲）通常搜不到 |
| **B. 云盘上传** | 下载视频音频 → 转 mp3 → 传网易云云盘 → 建成歌单 → F12 | 一次性脚本，游戏侧零改动；能听到原版音频 |
| **C. 插件播本地文件** | 改插件接管取流地址，直接播本地 mp3 | 最彻底但最难维护，要跟上游斗 |

B 站音频流本身是**可以取的**（实测 `x/player/playurl?fnval=16` 返回 DASH 音频，带 `Referer` 头
直接 206 分段下载，单集 25 分钟约 16 MB），云盘的上传接口也还活着
（`/weapi/cloud/upload/check` 与 `/weapi/nos/token/alloc` 均返回 200）。
所以 B 这条路技术上通 —— 但那属于「把视频音频搬进自己账号」，版权上请自行判断，这里不代做。

最简单的做法仍然是先走 A：
1. 在网易云里找到对应的 BGM（或自己建一张歌单，把这几首场景曲加进去）
2. 把那张网易云歌单的 `playlist?id=` 填进 `ExtraPlaylists`，按 F12

这样它就成了游戏里一个独立歌单，跟红心歌单互不干扰。

### 排序是在整张歌单上做的

ChillNetease 注入游戏播放列表时会截窗口，默认只放前 1000 首（`MaxInjectedSongs`）。
本插件是在**整张歌单**上排序，再把排好的完整列表交给上游去截 ——

- 顺序对了：你的真实 Top100 有 **98 首**落在 1000 首窗口里，最前面就是 `KiLLKiSS`(219 次)、`Hotel California (Live)`(101 次)、`霜雪千年`(67 次)…
- 如果只排前 1000 首再截，Top100 里只有 **5 首**能进窗口（因为真正常听的歌原本散落在 2954 首各处）

### 生效时机

| 场景 | 行为 |
|---|---|
| 按 **F11** | 拉红心歌单全量曲目（分批补齐，约 7 秒/3000 首）→ 按当前模式排序 → 注入并播放第 0 首 |
| 按 **F12** | 拉 `ExtraPlaylists` 里的每张歌单 → 各自存成独立歌单 → 面板打开（不打断当前播放） |
| 按 **F5** | 拉今天的日推 → 存成「每日推荐 MM-dd」→ 面板自动弹出并停在这 34 首上 |
| 在 F6 面板里点歌单 | 按当前模式排序后注入 |
| 按 **F10** 改排序模式 | 立刻重排当前歌单 |
| 切歌 / 点歌 | 沿用已注入的顺序 |
| 重启游戏 | 先加载上次缓存排序 → 后台联网刷新 → 恢复歌单时会同步云端最新版本 |
| 漫游 FM / 搜索结果 | **不排序**（保持推荐/相关度顺序） |
| 点「导入」分组里的本地歌单 + 归组模式 | 保持你挑的顺序（不按专辑/歌手打乱） |

### 配置文件

`BepInEx\config\com.kevin.chillneteasesort.cfg`

| 键 | 默认 | 说明 |
|---|---|---|
| `Sort.Mode` | `PlayCountAll` | 当前排序模式，游戏内切换会自动写回 |
| `Sort.CycleHotkey` | `F10` | 循环切换热键 |
| `Sort.InjectLikedHotkey` | `F11` | 一键注入红心歌单热键 |
| `Sort.InjectExtraPlaylistsHotkey` | `F12` | 一键注入额外歌单热键 |
| `Sort.DailyRecommendHotkey` | `F5` | 拉每日推荐（日推）的热键 |
| `UI.OpenPanelOnDaily` | `true` | 按日推后是否自动弹出 F6 面板 |
| `Data.ExtraPlaylists` | 空 | 额外歌单 `id:名称, id:名称`，配了才有 F12 |
| `Data.MaxInjectSongs` | `0` | 单次注入的歌曲数上限，`0` = 全部（3000 首就是 3000 首） |
| `Refresh.AutoRefreshOnStartup` | `true` | 每次启动联网刷新口味档案 |
| `Refresh.SyncPlaylistOnStartup` | `true` | 启动时把恢复的歌单同步成云端最新 |
| `Data.LikeTrackIdsNewestFirst` | `true` | 红心歌单 `trackIds` 是否「最新收藏在前」（方向反了就改 false） |
| `Data.ManualCookie` | 空 | 一般留空，自动读 go-musicfox 的 cookie |
| `UI.ShowToast` | `true` | 切换时屏幕提示（支持多条同时显示） |

---

## 数据是怎么拿到的

ChillNetease 的原生桥 `ChillNetease.dll` 是 Go 编译的二进制，只导出 35 个固定函数，**里面没有听歌排行/收藏顺序接口**，也没法改那个二进制。所以本插件走网易云几个至今**不需要 weapi/eapi 加密**的老接口，带 `MUSIC_U` cookie 直接 GET：

```
GET api/nuser/account/get                       → uid
GET api/user/playlist?uid=&limit=1000           → 找 specialType=5 的「我喜欢的音乐」
GET api/v1/play/record?uid=&type=0              → allData[]（含 playCount）
GET api/v1/play/record?uid=&type=1              → weekData[]
GET api/v6/playlist/detail?id=&n=1&s=8          → trackIds[]（收藏顺序，**全量**）
GET api/song/detail?ids=[200 个 id]             → 分批补齐曲目信息
```

cookie 用的是 `%LOCALAPPDATA%\go-musicfox\cookie` —— 也就是你在游戏里 F6 扫码登录后，原生桥自己写的那份，所以**永远与游戏登录态一致**，不用二次配置。

结果缓存到 `BepInEx\config\ChillNeteaseSort.cache.json`，断网时照样能按上次数据排序。

### 为什么大歌单也能完整拉下来（1000 首截断这件事）

上游自带的 `Bridge.GetPlaylistSongs()` 走 `api/v6/playlist/detail`，而那个接口响应里的
`tracks` 字段**被服务端硬截在 1000 首** —— 实测 `n=1000` 和 `n=5000` 都只回 1000 首。
一张 2956 首的红心歌单，用上游方法就只能看到三分之一。

绕过办法是：同一个响应里的 `trackIds` 是**全量**的（2956 个一个不少），所以插件自己走两步：

1. `playlist/detail` 只取 `trackIds`（拿到全部 id）；
2. 每 200 个一批调 `song/detail?ids=[...]` 补齐曲目信息
   —— 这个接口单次最多吃 200 个 id，传 250 只回 201 首（实测）。

2956 首 = 1 次 + 15 批，本地实测约 **7 秒**。想要少拉点可以把 `Data.MaxInjectSongs` 改成具体数字。

### 为什么用独立插件而不是 fork 上游

| | 独立插件（本方案） | fork ChillNetease |
|---|---|---|
| 体积 | ~50 KB | 16 MB（要一起分发原生 DLL） |
| 上游更新 | 互不干扰 | 每次都要 merge |
| 出问题 | 删掉 DLL 即恢复 | 要重装 |
| 代价 | 依赖上游方法签名（已全部做 try/catch 降级） | — |

### 关于「只有几首」——那是游戏自己的列表 UI

游戏左侧那个「播放列表」面板是**游戏自带的 UI**，一屏大约显示 10 行，需要滚轮往下滚。
插件改不了那个面板的行数，但**能决定列表里有多少首歌**。所以：

- 列表里只有十几首 → 大概率是拉取被截断了（见上一节），或者 `Data.MaxInjectSongs` 设了上限；
- 想看到更多 → 滚轮往下滚，或者按 F11 重新注入全量。

---

## 已知限制

- **听歌排行服务端最多返回 100 首**（`allData`/`weekData` 都只有这个量级）。所以只有你真正听过、且进了 Top100 的歌才有次数权重，其余按原顺序跟在后面。这是网易云接口的硬上限，绕不过。
- 「最近收藏」依赖红心歌单的 `trackIds` 顺序。如果你在网易云 App 里给红心歌单换过排序（比如按歌手排），这个顺序会跟着变。
- 只对「注入游戏播放列表」的歌单生效；漫游 FM 与搜索结果保持原样。
- 需要 `ChillNetease 0.3.1+`。上游大改（类名/方法签名）会导致某些补丁安装失败 —— 日志里会写明哪几个失败，其余功能仍正常。
- **一次注入几千首会让游戏播放列表变长**，滚动和切歌仍可用，但低配机器可能觉得列表略钝；嫌重就把 `Data.MaxInjectSongs` 设成 500 / 1000。
- 曲目信息是**分批拉**的（每批 200 首），网速差时 3000 首要 10 秒上下，期间屏幕角落会显示「补齐曲目 x/y」。

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
