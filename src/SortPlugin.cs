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
        public const string PluginVersion = "1.0.0";
        public const string UpstreamGuid = "com.haikisha.chillnetease";

        public static SortPlugin Instance;
        private static BepInEx.Logging.ManualLogSource _log;

        // ---------------- 配置 ----------------

        public static ConfigEntry<string> SortModeEntry;
        public static ConfigEntry<string> CycleHotkey;
        public static ConfigEntry<string> InjectHotkey;
        public static ConfigEntry<bool> AutoRefreshOnStartup;
        public static ConfigEntry<bool> LikeNewestFirst;
        public static ConfigEntry<string> ManualCookie;
        public static ConfigEntry<bool> ShowToastEntry;
        public static ConfigEntry<bool> SortOnStartupRestore;

        /// <summary>当前生效的排序模式（运行时可变，切换即写回配置）。</summary>
        public static SortMode CurrentMode = SortMode.Default;

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
        /// <summary>由 Harmony 补丁每帧调用（主线程）。</summary>
        public static void Tick()
        {
            try
            {
                // 线程池排进来的主线程任务（如启动同步后的重新注入）
                SortPluginMainThread.Drain();
                // 网络请求队列推进（必须每帧跑，否则请求永远不完成）
                NeteaseApi.Pump();
                Toast.Tick();
                PollHotkey();

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

        // ---------------- 热键 ----------------

        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int pid);

        /// <summary>只有游戏窗口在前台时才响应，避免在别的程序里打字误触发。</summary>
        private static bool IsGameFocused()
        {
            try
            {
                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) return false;
                int pid;
                GetWindowThreadProcessId(hwnd, out pid);
                return pid == System.Diagnostics.Process.GetCurrentProcess().Id;
            }
            catch { return false; }
        }

        private static void PollHotkey()
        {
            // 每帧轮询代价低，但没必要 200 次/秒，间隔 40ms 足够
            if (Time.realtimeSinceStartup < _nextHotkeyPoll) return;
            _nextHotkeyPoll = Time.realtimeSinceStartup + 0.04f;

            PollOne(CycleHotkey, ref _hotkeyDown, CycleMode);
            PollOne(InjectHotkey, ref _hotkeyDownInject, InjectLikedPlaylist);
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
            if (!IsGameFocused()) return;
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
            return 0;
        }

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

            Toast.Show("正在拉取红心歌单…", 2f);
            SortPlugin.Log($"[Sort] 一键注入红心歌单 {pid}（模式 {SortModeInfo.DisplayName(CurrentMode)}）");

            // P/Invoke 会阻塞 → 必须丢线程池，别卡主线程
            System.Threading.Tasks.Task.Run(() =>
            {
                try { return bridge.GetPlaylistSongs(pid, true); }
                catch (Exception ex)
                {
                    SortPlugin.Warn("[Sort] 拉取红心歌单失败: " + ex.Message);
                    return null;
                }
            }).ContinueWith(t =>
            {
                var songs = t.Result;
                if (songs == null || songs.Count == 0)
                {
                    SortPluginMainThread.Run(() => Toast.Show("红心歌单拉取失败：" + (bridge.LastError() ?? "未知"), 4f));
                    return;
                }

                SortPluginMainThread.Run(() =>
                {
                    try
                    {
                        // 排序交给 InjectPlaylist 的 Prefix 统一做（避免排两次、"顺序未变"日志误导）
                        PlaylistLink.InjectPlaylist(songs, 0);
                        PlaylistLink.InjectedPlaylistId = pid;
                        Toast.Show($"已注入 {songs.Count} 首 · {SortModeInfo.DisplayName(CurrentMode)}", 4f);
                        SortPlugin.Log($"[Sort] 一键注入完成: {songs.Count} 首");
                    }
                    catch (Exception ex)
                    {
                        SortPlugin.Warn("[Sort] 一键注入失败: " + ex.Message);
                        Toast.Show("注入失败: " + ex.Message, 4f);
                    }
                });
            });
        }

        // ---------------- 日志 ----------------

        public static void Log(string msg) => _log?.LogInfo(msg);
        public static void Warn(string msg) => _log?.LogWarning(msg);
        public static void Error(string msg) => _log?.LogError(msg);
    }
}
