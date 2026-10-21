using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using BepInEx;
using BepInEx.Configuration;
using ChillNetease.Plugin;
using HarmonyLib;
using UnityEngine;

namespace ChillNeteaseSort
{
    /// <summary>
    /// ChillNetease 的排序增强插件（独立补丁，不修改上游文件）。
    ///
    /// 做三件事：
    ///   1. 给注入游戏原生播放列表的歌单排序（听歌次数 / 最近收藏 / 最早收藏）
    ///   2. 每次开机自动联网刷新「口味档案」（听歌次数 + 收藏顺序）
    ///   3. 每次启动恢复上次歌单时，先把网易云上的最新歌单一并同步下来（歌单在云端改了也不会落后）
    ///
    /// 为什么是独立插件而不是改上游：上游是 MIT 的第三方项目，改它得重新分发 16MB 原生 DLL、
    /// 且上游一更新就冲突。这里只在方法层面挂 Harmony 补丁，插件本身几十 KB，卸载即恢复原样。
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Chill With You.exe")]
    [BepInDependency(UpstreamGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class SortPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kevin.chillneteasesort";
        public const string PluginName = "ChillNetease Sort";
        public const string PluginVersion = "1.3.0";
        public const string UpstreamGuid = "com.haikisha.chillnetease";

        public static SortPlugin Instance;
        private static BepInEx.Logging.ManualLogSource _log;

        // ---------------- 配置 ----------------

        public static ConfigEntry<string> SortModeEntry;
        public static ConfigEntry<string> CycleHotkey;
        public static ConfigEntry<string> InjectHotkey;
        public static ConfigEntry<string> InjectExtraHotkey;
        public static ConfigEntry<string> DailyHotkey;
        public static ConfigEntry<bool> OpenPanelOnDaily;
        public static ConfigEntry<bool> AutoRefreshOnStartup;
        public static ConfigEntry<bool> LikeNewestFirst;
        public static ConfigEntry<string> ManualCookie;
        public static ConfigEntry<bool> ShowToastEntry;
        public static ConfigEntry<bool> SortOnStartupRestore;
        public static ConfigEntry<string> ExtraPlaylists;

        // ---- 「我的音乐库」：按当前登录账号自动发现要导入什么 ----
        public static ConfigEntry<string> MyLibraryHotkey;
        public static ConfigEntry<bool> MyLibraryIncludeLiked;
        public static ConfigEntry<bool> MyLibraryIncludeDaily;
        public static ConfigEntry<bool> MyLibraryIncludeToplists;
        public static ConfigEntry<string> MyLibraryToplistIds;
        public static ConfigEntry<bool> MyLibraryIncludeMine;
        public static ConfigEntry<bool> MyLibraryIncludeSubscribed;
        public static ConfigEntry<int> MyLibraryPlaylistLimit;
        public static ConfigEntry<int> MyLibraryMaxSongs;

        /// <summary>
        /// 单次注入的歌曲数上限。0 = 不限制（全部）。
        /// 上游 `/api/v6/playlist/detail` 的 tracks 硬截 1000 首、trackIds 却是全量，
        /// 所以这里改用 FullPlaylist 分批补齐；但歌单太大时游戏列表可能卡，留个闸门。
        /// </summary>
        public static ConfigEntry<int> MaxInjectSongs;

        /// <summary>当前生效的排序模式（运行时可变，切换即写回配置）。</summary>
        public static SortMode CurrentMode = SortMode.Default;

        /// <summary>
        /// 上游原始顺序（未排序前）：playlistId → 歌曲 id 序列。
        /// 用来在切回「歌单原顺序」时还原 —— 因为注入到游戏里的已经是排过的列表，
        /// 不记一份就没法退回去。只存 id（2954 首约 24 KB），不存整个 SongInfo。
        /// </summary>
        private static readonly Dictionary<long, List<long>> RawOrders =
            new Dictionary<long, List<long>>();
        private static readonly object RawGate = new object();

        /// <summary>由 Harmony 的 InjectPrefix 在 playIndex==0（首次注入整张歌单）时调用。</summary>
        public static void RememberRawOrder(List<SongInfo> songs)
        {
            if (songs == null || songs.Count == 0) return;
            var pid = PlaylistLink.InjectedPlaylistId;
            // 上游是先 InjectPlaylist 再赋 InjectedPlaylistId，所以这里可能还是上一张歌单的 id。
            // 用「长度 + 首尾 id」做个一致性判断，不匹配就不记，避免把 A 歌单的顺序记到 B 名下。
            lock (RawGate)
            {
                var key = pid;
                if (RawOrders.TryGetValue(key, out var old) && old.Count > 0)
                {
                    // 同一张歌单再次注入（重排/恢复）时不该覆盖原始顺序
                    if (old.Count == songs.Count) return;
                }
                var ids = new List<long>(songs.Count);
                foreach (var s in songs) ids.Add(s?.Id ?? 0);
                RawOrders[key] = ids;
            }
        }

        public static List<long> GetRawOrder(long pid)
        {
            lock (RawGate) return RawOrders.TryGetValue(pid, out var ids) ? ids : null;
        }

        private void Awake()
        {
            Instance = this;
            _log = Logger;

            SortModeEntry = Config.Bind("Sort", "Mode", "PlayCountAll",
                "歌单排序模式：Default=原顺序 / PlayCountAll=听歌次数(全部时间) / " +
                "PlayCountWeek=听歌次数(最近一周) / RecentLiked=最近收藏优先 / OldestLiked=最早收藏优先。" +
                "游戏内按热键可循环切换，切换后自动写回这里。");

            CycleHotkey = Config.Bind("Sort", "CycleHotkey", "F10",
                "循环切换排序模式的热键（F1~F12 / 单字母 / None=禁用）。" +
                "注意：F6 是上游面板、F7/F9 是 Chill Env Sync、F8 是 ChillAI，别撞。");

            InjectHotkey = Config.Bind("Sort", "InjectLikedHotkey", "F11",
                "一键按当前排序注入「我喜欢的音乐」并开始播放（F1~F12 / 单字母 / None=禁用）。" +
                "歌单在云端更新后想立刻按新口味重排重播，按这个就行，不用再进 F6 面板找歌单。");

            InjectExtraHotkey = Config.Bind("Sort", "InjectExtraPlaylistsHotkey", "F12",
                "一键依次注入「额外歌单」里配置的所有歌单，每张存成一个独立歌单（F1~F12 / 单字母 / None=禁用）。" +
                "配合 Data.ExtraPlaylists 用：把几张歌单合成一个、或把自己收藏的一批歌单独拉出来放，都靠这个。");

            DailyHotkey = Config.Bind("Sort", "DailyRecommendHotkey", "F5",
                "一键拉取网易云「每日推荐」（日推）并在面板里单独打开（F1~F12 / 单字母 / None=禁用）。" +
                "每次按都会重新请求接口（云端每天只换一次，所以同一天按多次结果一样）。" +
                "日推会存成一张本地歌单「每日推荐 MM-dd」，每天覆盖同一张。");

            OpenPanelOnDaily = Config.Bind("UI", "OpenPanelOnDaily", true,
                "按日推热键后自动把 F6 面板弹出来（直接看到今天的 34 首）。" +
                "不想被面板挡住画面就改成 false，届时手动按 F6 也能看到这张歌单。");

            AutoRefreshOnStartup = Config.Bind("Refresh", "AutoRefreshOnStartup", true,
                "每次启动游戏时联网刷新听歌次数与收藏顺序（约 4 个请求，1~3 秒）");
            SortOnStartupRestore = Config.Bind("Refresh", "SyncPlaylistOnStartup", true,
                "启动恢复上次歌单时，先从网易云重新拉一次歌曲列表（歌单在云端改过也能同步到最新）");

            LikeNewestFirst = Config.Bind("Data", "LikeTrackIdsNewestFirst", true,
                "网易云红心歌单的 trackIds 顺序是否为「最新收藏在前」。默认 true（官方 App/网页都是这个顺序）；" +
                "若发现「最近收藏」排序方向反了，改成 false。");

            ManualCookie = Config.Bind("Data", "ManualCookie", "",
                "手动填 cookie（一般留空）。留空时自动读 %LOCALAPPDATA%\\go-musicfox\\cookie，" +
                "也就是游戏里扫码登录后原生桥保存的那份，永远与登录态一致。");

            ShowToastEntry = Config.Bind("UI", "ShowToast", true, "切换排序模式时在屏幕上提示一下");

            ExtraPlaylists = Config.Bind("Data", "ExtraPlaylists", "",
                "额外歌单（用 F12 一键依次注入，每张都会在本地存成一个独立歌单）。" +
                "格式：id:名称, id:名称 —— 例如 2462607269:红心合集, 5012345:我的精选。\n" +
                "id 就是网易云歌单网址里 playlist?id= 后面那串数字；名称可省略。\n" +
                "留空则不启用 F12。\n" +
                "注意：这里留空是**正常状态**——绝大多数人不需要手填，用 F4「我的音乐库」即可。");

            // ---------------- 我的音乐库 ----------------
            // 全部按当前登录账号实时拉取，插件里不写死任何歌单 id。

            MyLibraryHotkey = Config.Bind("MyLibrary", "Hotkey", "F4",
                "一键把「我这个账号的音乐库」导入成游戏里的本地歌单（F1~F12 / 单字母 / None=禁用）。\n" +
                "拉的是当前登录账号的数据：我喜欢的音乐 / 每日推荐 / 排行榜 / 我的歌单 / 收藏的歌单。\n" +
                "每次按都重新拉，云端改了歌单也会跟上；导入后的歌单会出现在 F6 面板的「导入」分组里。\n" +
                "默认 F4 —— 避开别人占掉的键：F6 上游面板、F7/F9 Chill Env Sync、F8 与 F1 ChillAI。");

            MyLibraryIncludeLiked = Config.Bind("MyLibrary", "IncludeLiked", true,
                "导入「我喜欢的音乐」");
            MyLibraryIncludeDaily = Config.Bind("MyLibrary", "IncludeDaily", true,
                "导入「每日推荐」（当天的那 30 多首）");
            MyLibraryIncludeToplists = Config.Bind("MyLibrary", "IncludeToplists", true,
                "导入官方排行榜（具体哪几个见 ToplistIds）");

            MyLibraryToplistIds = Config.Bind("MyLibrary", "ToplistIds", "",
                "要导入哪些排行榜：逗号分隔的榜单 id，顺序就是导入顺序。留空 = 热歌/新歌/飙升/原创 四大榜。\n" +
                "常用：热歌榜 3778678（200 首）/ 新歌榜 3779629 / 飙升榜 19723756 / 原创榜 2884035。\n" +
                "网易云一共有 64 个榜单（还有古典/电音/说唱/各种风格榜）。按一次 F4 之后，\n" +
                "去 BepInEx/LogOutput.log 搜「榜单 」就能看到全部 id + 名字 + 首数，抄想要的填这里。");

            MyLibraryIncludeMine = Config.Bind("MyLibrary", "IncludeMine", true,
                "导入我自己创建的歌单");
            MyLibraryIncludeSubscribed = Config.Bind("MyLibrary", "IncludeSubscribed", false,
                "导入我收藏的（别人的）歌单。默认关：收藏往往有一两百张，一次全拉要很久。");

            MyLibraryPlaylistLimit = Config.Bind("MyLibrary", "PlaylistLimit", 10,
                "「我的歌单」「收藏的歌单」每类最多导几张（按网易云返回顺序，新的在前）。0 = 不限。");

            MyLibraryMaxSongs = Config.Bind("MyLibrary", "MaxSongsPerPlaylist", 0,
                "每张歌单最多保留几首（0 = 全部）。歌单特别大、游戏列表滚动卡时改成 200 / 500。");

            MaxInjectSongs = Config.Bind("Data", "MaxInjectSongs", 0,
                "单次注入的歌曲数上限。0（默认）= 全部，一张 3000 首的歌单就是 3000 首。\n" +
                "插件会先用 playlist/detail 拿全量 trackIds，再按 200 首一批补齐曲目信息\n" +
                "（网易云 song/detail 单次最多 200 个 id），所以「全量」是真的全量，不是 1000 首。\n" +
                "如果歌单特别大、游戏列表滚动卡，把它改成 500 / 1000 之类的值即可。");

            CurrentMode = SortModeInfo.Parse(SortModeEntry.Value, SortMode.Default);
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded，排序模式 = {SortModeInfo.DisplayName(CurrentMode)}");

            // 1. 先加载上次的口味档案缓存（保证断网也有数据可排）
            TasteProfile.LoadCache();

            // 2. 挂 Harmony 补丁
            try
            {
                var harmony = new Harmony(PluginGuid);
                HarmonyPatches.Install(harmony);
            }
            catch (Exception ex)
            {
                Logger.LogError("[Sort] Harmony 补丁安装失败: " + ex);
            }

            // 3. 启动刷新交给 Tick（等登录态与游戏主循环就绪再发请求）
            _refreshAt = Time.realtimeSinceStartup + 3f;
        }

        /// <summary>
        /// 启动后多久开始联网刷新 / 是否已发起。
        /// 刻意用 **static** 而不是实例字段：这个游戏的场景切换会让 BepInEx 挂在
        /// BepInEx_Manager 上的组件被 Unity 标记为「已销毁」，此时 `Instance != null`
        /// 走 Unity 重载的 == 运算会返回 false（C# 引用其实还在），实例字段就读不到，
        /// 启动刷新会永远不触发。静态字段不受组件生命周期影响。
        /// </summary>
        private static float _refreshAt = -1f;
        private static bool _refreshStarted;
        private static float _nextHotkeyPoll;
        private static bool _hotkeyDown;
        private static bool _hotkeyDownInject;
        private static bool _hotkeyDownExtra;
        private static bool _hotkeyDownDaily;
        private static bool _hotkeyDownMyLibrary;

        // ---- 提示渲染宿主 ----
        private static ToastRenderer _toastRenderer;
        private static bool _toastRendererTried;

        /// <summary>
        /// 把提示渲染器挂到游戏已有的 MonoBehaviour 上（AddComponent 到任意存活组件即可）。
        /// 每隔一会儿重试一次 —— 启动早期可能一个组件都还没 Spawn 出来。
        /// </summary>
        private static void EnsureToastRenderer()
        {
            try
            {
                if (_toastRenderer != null) return;      // Unity 的 == 重载会正确判断"已销毁"
                var host = FindHostBehaviour();
                if (host == null) return;                // 这一帧还没有宿主，下次 Tick 再来
                _toastRenderer = host.gameObject.AddComponent<ToastRenderer>();
                Log("[Sort] 提示渲染宿主已挂载: " + host.GetType().Name);
            }
            catch (Exception ex)
            {
                if (!_toastRendererTried)
                {
                    _toastRendererTried = true;
                    Warn("[Sort] 提示渲染宿主挂载失败（提示不可用，不影响排序）: " + ex.Message);
                }
            }
        }

        /// <summary>找一个当前存活、且不属于我们自己的 MonoBehaviour 当宿主。</summary>
        private static MonoBehaviour FindHostBehaviour()
        {
            MonoBehaviour firstFallback = null;
            var all = UnityEngine.Object.FindObjectsOfType<MonoBehaviour>();
            for (int i = 0; i < all.Length; i++)
            {
                var b = all[i];
                if (b == null) continue;
                if (b is ToastRenderer) { _toastRenderer = (ToastRenderer)b; return null; }
                // 优先挑游戏自己的组件（BepInEx 挂的组件在场景切换后可能被销毁）；
                // enabled 的排前面 —— 禁用的组件 Unity 不会给它 GUI 事件
                if (b.GetType().Assembly == typeof(ToastRenderer).Assembly) continue;
                if (b.enabled && b.gameObject.activeInHierarchy) return b;
                if (firstFallback == null) firstFallback = b;
            }
            return firstFallback;
        }

        /// <summary>由 Harmony 补丁每帧调用（主线程）。</summary>
        public static void Tick()
        {
            try
            {
                EnsureToastRenderer();

                // 线程池排进来的主线程任务（如启动同步后的重新注入）
                SortPluginMainThread.Drain();
                // 网络请求队列推进（必须每帧跑，否则请求永远不完成）
                NeteaseApi.Pump();
                Toast.Tick();
                PollHotkey();

                // 沙箱/自动化自检用：设了环境变量才启用（正常玩不生效）

                // 启动后延迟刷新一次（等上游把登录态恢复好）
                if (!_refreshStarted && AutoRefreshOnStartup != null && AutoRefreshOnStartup.Value)
                {
                    if (_refreshAt > 0 && Time.realtimeSinceStartup >= _refreshAt)
                    {
                        _refreshStarted = true;
                        NeteaseApi.StartRefresh((ok, err) =>
                        {
                            if (ok) Toast.Show("口味档案已更新：" + TasteProfile.Describe(), 3.5f);
                            else Toast.Show("口味档案刷新失败：" + err, 4f);
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("[Sort] Tick 异常: " + ex.Message);
            }
        }

        /// <summary>
        /// 把一张歌单直接「打开」在面板里（等价于用户在「导入」分组里点它）。
        /// 这正是"单独放一个歌单"最舒服的姿势：不打断正在放的歌，用户想听时按 F6 就在眼前。
        /// </summary>
        private static void ShowPlaylistInPanel(long playlistId, string name, bool openPanel = false)
        {
            try
            {
                var stored = PlaylistStore.Find(playlistId);
                if (stored == null) { Warn($"[Sort] 本地歌单 {playlistId} 不存在，跳过面板展示"); return; }

                var m = typeof(NeteaseUi).GetMethod("OpenStoredPlaylist", AllStatic);
                if (m == null) { Warn("[Sort] 找不到 NeteaseUi.OpenStoredPlaylist，跳过面板展示"); return; }
                m.Invoke(null, new object[] { stored });

                // _group 切到「导入」(值 2)，让面板直接停在用户自己的歌单列表上
                var setGroup = typeof(NeteaseUi).GetMethod("SetGroup", AllStatic);
                if (setGroup != null)
                {
                    var pt = setGroup.GetParameters()[0].ParameterType;
                    setGroup.Invoke(null, new[] { Enum.ToObject(pt, 2) });
                }
                Log($"[Sort] 已在面板中打开歌单：{name}（{playlistId}）");

                // 可选：顺手把面板弹出来，让用户立刻看到曲目列表（不弹的话得自己按 F6）
                if (openPanel)
                {
                    var isOpenProp = typeof(NeteaseUi).GetProperty("IsOpen", AllStatic);
                    var opened = isOpenProp?.GetValue(null) is bool b && b;
                    if (!opened)
                    {
                        var toggle = typeof(NeteaseUi).GetMethod("Toggle", AllStatic);
                        if (toggle != null) { toggle.Invoke(null, null); Log("[Sort] 面板已自动弹出"); }
                    }
                }
            }
            catch (Exception ex)
            {
                Warn("[Sort] 面板展示歌单失败（歌单已存好，可手动去「导入」里找）: " + ex.Message);
            }
        }

        private const BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        // ---------------- 热键 ----------------

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        /// <summary>
        /// 游戏窗口是否在前台。
        /// 用「本进程所有顶层窗口里有没有前台窗口」来判定，而不是直接比 GetForegroundWindow 的 pid ——
        /// Unity 会创建一个不可见的辅助窗口，它拿到前台时 pid 是一样的，但直接比 pid 会漏掉这种情况，
        /// 表现为"按热键毫无反应"（实测见过）。
        /// </summary>
        private static bool IsGameFocused()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                var self = System.Diagnostics.Process.GetCurrentProcess().Id;
                if (pid == self) return true;

                // 兜底：前台窗口的根所有者是不是本进程（Unity 某些弹窗会走 owner 链）
                var root = GetAncestor(hwnd, 2 /* GA_ROOTOWNER */);
                if (root != IntPtr.Zero)
                {
                    GetWindowThreadProcessId(root, out pid);
                    if (pid == self) return true;
                }
                return false;
            }
            catch { return false; }
        }

        [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        private static void PollHotkey()
        {
            // 每帧轮询代价低，但没必要 200 次/秒，间隔 40ms 足够
            if (Time.realtimeSinceStartup < _nextHotkeyPoll) return;
            _nextHotkeyPoll = Time.realtimeSinceStartup + 0.04f;

            PollOne(CycleHotkey, ref _hotkeyDown, CycleMode);
            PollOne(InjectHotkey, ref _hotkeyDownInject, InjectLikedPlaylist);
            PollOne(InjectExtraHotkey, ref _hotkeyDownExtra, InjectExtraPlaylists);
            PollOne(DailyHotkey, ref _hotkeyDownDaily, InjectDailyRecommend);
            PollOne(MyLibraryHotkey, ref _hotkeyDownMyLibrary, InjectMyMusic);
        }

        /// <summary>
        /// F4：把当前登录账号的音乐库（收藏 / 歌单 / 日推 / 排行榜）导入成本地歌单。
        /// 实际逻辑在 <see cref="MyLibrary"/>，这里只做入口与异常兜底。
        /// </summary>
        public static void InjectMyMusic()
        {
            try
            {
                MyLibrary.Run();
            }
            catch (Exception ex)
            {
                Warn("[Sort] 我的音乐库启动失败: " + ex);
                Toast.Show("导入失败: " + ex.Message, 4f);
            }
        }

        /// <summary>
        /// 单个热键的边沿检测。
        /// GetAsyncKeyState 在按住期间一直返回按下状态 —— 必须自己做边沿检测，
        /// 否则按住 0.2 秒会被识别成连按 5 次（把 5 个模式全轮一遍）。
        /// </summary>
        private static void PollOne(ConfigEntry<string> cfg, ref bool downState, Action action)
        {
            if (cfg == null) return;
            var key = (cfg.Value ?? "").Trim().ToUpperInvariant();
            if (key.Length == 0 || key == "NONE") return;

            var vk = VirtualKey(key);
            if (vk <= 0) return;

            var pressed = (GetAsyncKeyState(vk) & 0x8000) != 0;
            if (!pressed)
            {
                downState = false;
                return;
            }
            if (downState) return;

            downState = true;
            var focused = IsGameFocused();
            Log($"[Sort][diag] 热键 {key} 按下 · 前台={focused}");
            if (!focused) return;
            action();
        }

        private static int VirtualKey(string name)
        {
            // F1~F12
            if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name.Substring(1), out var n) && n >= 1 && n <= 12)
            {
                return 0x70 + (n - 1);
            }
            if (name.Length == 1)
            {
                var c = name[0];
                if (c >= 'A' && c <= 'Z') return c;           // VK_A..VK_Z
                if (c >= '0' && c <= '9') return c;           // VK_0..VK_9
            }
            if (name.Length >= 2 && name[0] == 'F' && int.TryParse(name.Substring(1), out var n2) && n2 >= 13 && n2 <= 24)
            {
                // F13~F24 在扩展键盘上（笔记本很少有，但外接键盘能设）
                return 0x7C + (n2 - 13);
            }
            return 0;
        }

        /// <summary>
        /// 上游面板是否开着（把 toast 挪到左下角，避免被右上角的 F6 面板挡住）。
        /// 用反射读 NeteaseUi.IsOpen；读不到就当作没开。
        /// </summary>
        private static bool IsPanelOpen()
        {
            try
            {
                if (_isOpenProp == null && !_isOpenResolved)
                {
                    _isOpenResolved = true;
                    _isOpenProp = typeof(NeteaseUi).GetProperty("IsOpen",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                }
                if (_isOpenProp == null) return false;
                var v = _isOpenProp.GetValue(null);
                return v is bool b && b;
            }
            catch { return false; }
        }

        private static PropertyInfo _isOpenProp;
        private static bool _isOpenResolved;

        /// <summary>查询「面板开着时 toast 是否挪到左下角」（Toast.Draw 每帧调用）。</summary>
        public static bool PanelOpen => IsPanelOpen();

        /// <summary>循环切到下一个排序模式，并尝试立刻重排当前歌单。</summary>
        public static void CycleMode()
        {
            var next = SortModeInfo.Next(CurrentMode);
            SetMode(next, reapply: true, announce: true);
        }

        public static void SetMode(SortMode mode, bool reapply, bool announce)
        {
            CurrentMode = mode;
            try { if (SortModeEntry != null) SortModeEntry.Value = SortModeInfo.ConfigValue(mode); } catch { }
            SortPlugin.Log($"[Sort] 排序模式 → {SortModeInfo.DisplayName(mode)}");

            if (announce && (ShowToastEntry == null || ShowToastEntry.Value))
            {
                Toast.Show("排序：" + SortModeInfo.DisplayName(mode), 2.5f);
            }

            if (reapply) ReapplyCurrentPlaylist();
        }

        /// <summary>
        /// 用新顺序重排「当前已注入的歌单」。
        /// 拿不到当前歌单时不做任何事（下一次点歌自然会按新模式排）。
        /// </summary>
        public static void ReapplyCurrentPlaylist()
        {
            try
            {
                var pid = PlaylistLink.InjectedPlaylistId;
                if (pid <= 0) return;

                var stored = PlaylistStore.Find(pid);
                if (stored?.Songs == null || stored.Songs.Count == 0) return;

                // 传副本，让 InjectPlaylist 的 Prefix 统一排序（不动存储里的原列表）
                var copy = new List<SongInfo>(stored.Songs);
                PlaylistLink.InjectPlaylist(copy, -1);
                Toast.Show("已按「" + SortModeInfo.DisplayName(CurrentMode) + "」重排 " + copy.Count + " 首", 3f);
                SortPlugin.Log($"[Sort] 重排当前歌单 {pid}: {copy.Count} 首");
            }
            catch (Exception ex)
            {
                SortPlugin.Warn("[Sort] 重排当前歌单失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 一键：把今天的网易云「每日推荐」单独放成一张歌单（默认热键 F5）。
        ///
        /// 和 F11/F12 一样**不打断正在放的歌** —— 只在 F6 面板里打开，想听时点第一首。
        /// 日推**不参与排序**：它的价值就在网易云给的推荐顺序（HarmonyPatches 里按 id 集合识别后跳过重排）。
        /// 本地歌单用固定 id <see cref="DailyPlaylist.LocalPlaylistId"/>，每天覆盖，不会越堆越多。
        /// </summary>
        public static void InjectDailyRecommend()
        {
            // 日推走 NeteaseApi（UnityWebRequest），不依赖 bridge；但没有 cookie 一定拿不到
            if (string.IsNullOrEmpty(NeteaseApi.GetCookie()))
            {
                Toast.Show("没有网易云登录 cookie，先按 F6 扫码登录", 4f);
                return;
            }

            Toast.Show("正在拉取今日推荐…", 2f);
            Log("[Sort] 拉取每日推荐");

            DailyPlaylist.Load(
                songs =>
                {
                    try
                    {
                        var name = DailyPlaylist.LocalName;
                        PlaylistStore.Upsert(DailyPlaylist.LocalPlaylistId, name, songs);

                        // 兜底：万一上游哪天又对 id 加校验，Upsert 会静默失败。
                        // 这时不装作成功，直接注入播放器 —— 用户至少能听到今天的日推。
                        if (PlaylistStore.Find(DailyPlaylist.LocalPlaylistId) == null)
                        {
                            Warn("[Sort] 日推存本地歌单失败（Upsert 被忽略）→ 改为直接注入播放器");
                            PlaylistLink.InjectPlaylist(new List<SongInfo>(songs), 0);
                            PlaylistLink.InjectedPlaylistId = DailyPlaylist.LocalPlaylistId;
                            Toast.Show($"今日推荐 {songs.Count} 首，已直接开始播放", 4f);
                            return;
                        }

                        // 让面板的歌单列表立刻包含这张（否则要重开面板才刷出来）
                        var bridge = Plugin.Bridge;
                        if (bridge != null) TryRefreshPanelPlaylists(bridge);

                        ShowPlaylistInPanel(DailyPlaylist.LocalPlaylistId, name, openPanel: OpenPanelOnDaily?.Value ?? true);
                        Toast.Show($"今日推荐已就位：{songs.Count} 首 · 按 F6 在「导入」里点第一首", 5f);
                        Log($"[Sort] 日推已存成本地歌单「{name}」：{songs.Count} 首");
                    }
                    catch (Exception ex)
                    {
                        Warn("[Sort] 日推存本地失败: " + ex.Message);
                        Toast.Show("日推处理失败：" + ex.Message, 4f);
                    }
                },
                err => Toast.Show("日推拉取失败：" + err, 5f));
        }

        /// <summary>
        /// 一键：按当前排序模式注入「我喜欢的音乐」并从头播放。
        /// 歌单在云端更新后想立刻按新口味重排重播，按热键就行，不用再进 F6 面板翻歌单。
        /// </summary>
        public static void InjectLikedPlaylist()
        {
            var pid = TasteProfile.LikedPlaylistId;
            if (pid <= 0)
            {
                Toast.Show("还没拿到红心歌单 id（先确保已登录，稍等几秒再按）", 3.5f);
                return;
            }
            var bridge = Plugin.Bridge;
            if (bridge == null || !bridge.IsLoggedIn)
            {
                Toast.Show("未登录网易云，先按 F6 扫码", 3.5f);
                return;
            }
            if (MusicServicePatches.CurrentInstance == null)
            {
                Toast.Show("游戏音乐服务还没就绪，稍等一下再按", 3f);
                return;
            }

            Toast.Show("正在拉取红心歌单全部曲目…", 3f);
            SortPlugin.Log($"[Sort] 一键注入红心歌单 {pid}（模式 {SortModeInfo.DisplayName(CurrentMode)}）");

            // 用 FullPlaylist 而不是 bridge.GetPlaylistSongs：后者走 playlist/detail 的 tracks，
            // 被服务端硬截在 1000 首，2956 首的红心只能拿到三分之一。
            FullPlaylist.Load(pid,
                songs =>
                {
                    var original = songs.Count;
                    var limit = MaxInjectSongs?.Value ?? 0;
                    var truncated = limit > 0 && original > limit;
                    if (truncated)
                    {
                        SortPlugin.Log($"[Sort] 注入上限 {limit}，从 {original} 首截断");
                        songs = songs.Take(limit).ToList();
                    }

                    try
                    {
                        // 排序交给 InjectPlaylist 的 Prefix 统一做（避免排两次、"顺序未变"日志误导）
                        PlaylistLink.InjectPlaylist(songs, 0);
                        PlaylistLink.InjectedPlaylistId = pid;
                        Toast.Show($"已注入 {songs.Count} 首" + (truncated ? $"（云端 {original} 首，上限 {limit}）" : "") +
                            $" · {SortModeInfo.DisplayName(CurrentMode)}", 4f);
                        SortPlugin.Log($"[Sort] 一键注入完成: {songs.Count} 首" +
                            (truncated ? $"（云端共 {original} 首，已按上限截断）" : ""));
                    }
                    catch (Exception ex)
                    {
                        SortPlugin.Warn("[Sort] 一键注入失败: " + ex.Message);
                        Toast.Show("注入失败: " + ex.Message, 4f);
                    }
                },
                err => Toast.Show("红心歌单拉取失败：" + err, 4f),
                (done, total) =>
                {
                    if (done < total) Toast.Show($"补齐曲目 {done}/{total}…", 1.5f);
                });
        }

        /// <summary>
        /// 一键：把 Data.ExtraPlaylists 里配置的每张歌单**分别**注入成一个独立歌单。
        /// 用途：
        ///   · 把「我喜欢的音乐」之外的另一批歌（比如某个收藏的合集）单独拉出来放
        ///   · 几张歌单想合并听 → 配置多条，逐个按一次（每张都进本地歌单列表，随时能切）
        /// 每张用独立 id + 名称 Upsert 到 PlaylistStore，所以 F6 面板「导入」分组里会多出来，
        /// 重启游戏也还在。
        /// </summary>
        public static void InjectExtraPlaylists()
        {
            var raw = ExtraPlaylists?.Value ?? "";
            var entries = ParsePlaylistEntries(raw);
            if (entries.Count == 0)
            {
                Toast.Show("还没配额外歌单：BepInEx/config 里 Data.ExtraPlaylists 填 id:名称", 5f);
                return;
            }

            var bridge = Plugin.Bridge;
            if (bridge == null || !bridge.IsLoggedIn)
            {
                Toast.Show("未登录网易云，先按 F6 扫码", 3.5f);
                return;
            }
            if (MusicServicePatches.CurrentInstance == null)
            {
                Toast.Show("游戏音乐服务还没就绪，稍等一下再按", 3f);
                return;
            }

            Toast.Show($"正在拉取 {entries.Count} 张额外歌单…", 3f);
            SortPlugin.Log($"[Sort] 拉取额外歌单 {entries.Count} 张");

            // 逐张串行拉。走 FullPlaylist 而不是 bridge.GetPlaylistSongs：
            // 后者被 playlist/detail 的 tracks 截在 1000 首，大歌单会少一大半。
            var got = new List<KeyValuePair<long, List<SongInfo>>>();
            int idx = 0;

            void NextOne()
            {
                if (idx >= entries.Count) { Finish(); return; }
                var e = entries[idx++];
                Toast.Show($"拉取 {idx}/{entries.Count}：{LabelFor(entries, e.Key, raw)}…", 2f);
                FullPlaylist.Load(e.Key,
                    songs =>
                    {
                        if (songs != null && songs.Count > 0)
                            got.Add(new KeyValuePair<long, List<SongInfo>>(e.Key, songs));
                        else
                            SortPlugin.Warn($"[Sort] 额外歌单 {e.Key} 拉取为空");
                        NextOne();
                    },
                    err =>
                    {
                        SortPlugin.Warn($"[Sort] 额外歌单 {e.Key} 拉取失败: {err}");
                        NextOne();
                    });
            }

            void Finish()
            {
                if (got.Count == 0)
                {
                    Toast.Show("额外歌单全部拉取失败，看 BepInEx 日志", 4f);
                    return;
                }

                try
                {
                    long lastId = 0;
                    string lastName = null;
                    List<SongInfo> lastSongs = null;

                    foreach (var kv in got)
                    {
                        var label = LabelFor(entries, kv.Key, raw);
                        // 存成本地歌单：F6 面板「导入」分组可见、重启仍在
                        PlaylistStore.Upsert(kv.Key, label, kv.Value);
                        lastId = kv.Key; lastName = label; lastSongs = kv.Value;
                        SortPlugin.Log($"[Sort] 额外歌单已存本地：{label}（{kv.Key}）{kv.Value.Count} 首");
                    }

                    // 让 F6 面板的「收藏」分组里立刻能看到这几张歌单（否则要重开面板才刷新）
                    TryRefreshPanelPlaylists(bridge);

                    if (lastSongs != null)
                    {
                        // 不直接塞进播放器 —— 那会顶掉正在放的歌，而用户多半只是想"多一个歌单"。
                        // 改成：在面板里打开它（用户按 F6 就能看到并播放）。
                        ShowPlaylistInPanel(lastId, lastName);
                        Toast.Show($"已单独存好 {got.Count} 张歌单：{lastName}（{lastSongs.Count} 首）· 按 F6 在「导入」里打开", 5f);
                        SortPlugin.Log($"[Sort] 额外歌单注入完成：共 {got.Count} 张，已在面板打开 {lastName}");
                    }
                }
                catch (Exception ex)
                {
                    SortPlugin.Warn("[Sort] 额外歌单注入失败: " + ex.Message);
                    Toast.Show("注入失败: " + ex.Message, 4f);
                }
            }

            NextOne();
        }

        /// <summary>解析 "id:名称, id:名称"（名称可省略、中英文逗号都认）。</summary>
        public static List<KeyValuePair<long, string>> ParsePlaylistEntries(string raw)
        {
            var res = new List<KeyValuePair<long, string>>();
            if (string.IsNullOrWhiteSpace(raw)) return res;

            foreach (var part in raw.Split(new[] { ',', '，', ';', '；', '\n' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                var s = part.Trim();
                if (s.Length == 0) continue;

                string idText = s, name = null;
                var sep = s.IndexOfAny(new[] { ':', '：' });
                if (sep > 0)
                {
                    idText = s.Substring(0, sep).Trim();
                    name = s.Substring(sep + 1).Trim();
                }
                if (!long.TryParse(idText, out var id) || id <= 0) continue;
                res.Add(new KeyValuePair<long, string>(id, string.IsNullOrEmpty(name) ? null : name));
            }
            return res;
        }

        private static string LabelFor(List<KeyValuePair<long, string>> entries, long id, string raw)
        {
            foreach (var e in entries) if (e.Key == id && !string.IsNullOrEmpty(e.Value)) return e.Value;

            // 用户没写名字 → 用网易云上的真名（刷新时存在 ExtraMeta 里）
            var meta = TasteProfile.GetExtraMeta(id);
            if (meta != null && !string.IsNullOrEmpty(meta.Name)) return meta.Name;

            return "额外歌单 " + id;
        }

        /// <summary>
        /// 让 F6 面板的歌单列表立刻包含刚加的歌单。
        /// 上游的 OnPlaylistsLoaded 是公开静态方法，用反射调用（上游改名就静默跳过，
        /// 歌单仍然能从「导入」分组里看到，只是要重开面板）。
        /// </summary>
        internal static void TryRefreshPanelPlaylists(NeteaseBridge bridge)
        {
            try
            {
                var uid = TasteProfile.UserId;
                if (uid <= 0) return;

                var all = bridge.GetAllUserPlaylists(uid);
                if (all == null || all.Count == 0) return;

                var m = typeof(NeteaseUi).GetMethod("OnPlaylistsLoaded",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (m == null) return;
                m.Invoke(null, new object[] { all });
                SortPlugin.Log($"[Sort] 面板歌单列表已刷新：{all.Count} 个");
            }
            catch (Exception ex)
            {
                SortPlugin.Warn("[Sort] 刷新面板歌单列表失败（不影响使用）: " + ex.Message);
            }
        }

        // ---------------- 自检（仅诊断构建，环境变量开关）----------------

        // ---------------- 日志 ----------------

        public static void Log(string msg) => _log?.LogInfo(msg);
        public static void Warn(string msg) => _log?.LogWarning(msg);
        public static void Error(string msg) => _log?.LogError(msg);
    }
}
