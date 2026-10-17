using System;
using System.Collections.Generic;
using System.Reflection;
using ChillNetease.Plugin;
using HarmonyLib;
using UnityEngine;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 所有 Harmony 补丁。刻意全部用「显式定位 + 逐个 try/catch」安装：
    /// 上游 ChillNetease 一旦改签名，失败的只是某一个补丁，不会把整个游戏搞崩。
    /// </summary>
    public static class HarmonyPatches
    {
        private const BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>搜索结果的注入不该被重排（用户是按相关度找歌的）。</summary>
        private static bool _skipNextInject;

        public static void Install(Harmony harmony)
        {
            var okCount = 0;
            var fail = new List<string>();

            // 1) 歌单注入前重排（核心）
            try
            {
                var m = typeof(PlaylistLink).GetMethod("InjectPlaylist", AllStatic);
                if (m == null) throw new Exception("找不到 PlaylistLink.InjectPlaylist");
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(HarmonyPatches)
                    .GetMethod(nameof(InjectPrefix), AllStatic)));
                okCount++;
            }
            catch (Exception ex) { fail.Add("InjectPlaylist: " + ex.Message); }

            // 2) 每帧驱动（复用上游挂好的 RoomGameManager.Update 链条）
            try
            {
                var uiType = typeof(NeteaseUi);
                var m = uiType.GetMethod("Tick", AllStatic);
                if (m == null) throw new Exception("找不到 NeteaseUi.Tick");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(HarmonyPatches)
                    .GetMethod(nameof(TickPostfix), AllStatic)));
                okCount++;
            }
            catch (Exception ex) { fail.Add("NeteaseUi.Tick: " + ex.Message); }

            // 3) 启动恢复歌单后：联网把该歌单刷新成云端最新
            try
            {
                var m = typeof(NeteaseUi).GetMethod("TryRestoreImported", AllStatic);
                if (m == null) throw new Exception("找不到 NeteaseUi.TryRestoreImported");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(HarmonyPatches)
                    .GetMethod(nameof(RestorePostfix), AllStatic)));
                okCount++;
            }
            catch (Exception ex) { fail.Add("TryRestoreImported: " + ex.Message); }

            // 4) 屏幕提示（借上游 OnGUI 的渲染时机）
            try
            {
                var rendererType = typeof(PlaylistLink).Assembly.GetType("ChillNetease.Plugin.NeteaseUiRenderer");
                var m = rendererType?.GetMethod("OnGUI",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (m == null) throw new Exception("找不到 NeteaseUiRenderer.OnGUI");
                harmony.Patch(m, postfix: new HarmonyMethod(typeof(HarmonyPatches)
                    .GetMethod(nameof(OnGuiPostfix), AllStatic)));
                okCount++;
            }
            catch (Exception ex) { fail.Add("NeteaseUiRenderer.OnGUI: " + ex.Message); }

            // 5) 标记搜索结果注入，跳过重排
            try
            {
                var m = typeof(NeteaseUi).GetMethod("PlaySearchResult", AllStatic);
                if (m == null) throw new Exception("找不到 NeteaseUi.PlaySearchResult");
                harmony.Patch(m, prefix: new HarmonyMethod(typeof(HarmonyPatches)
                    .GetMethod(nameof(SearchPrefix), AllStatic)));
                okCount++;
            }
            catch (Exception ex) { fail.Add("PlaySearchResult: " + ex.Message); }

            SortPlugin.Log($"[Sort] Harmony 补丁安装完成：{okCount} 个成功" +
                (fail.Count > 0 ? "，失败 " + fail.Count + " 个 → " + string.Join(" | ", fail) : ""));
        }

        // ---------------- 补丁体 ----------------

        /// <summary>
        /// 注入前重排。用 ref 替换引用而不是原地改 List ——
        /// 上游有几处传的是 PlaylistStore 里的缓存 List，原地重排会把本地存档的顺序也改掉。
        /// </summary>
        private static void InjectPrefix(ref List<SongInfo> songs, int playIndex, object fmMoreProvider)
        {
            try
            {
                if (_skipNextInject) { _skipNextInject = false; return; }
                if (fmMoreProvider != null) return;      // 漫游 FM 会话：保持推荐顺序
                if (songs == null || songs.Count < 2) return;

                var mode = SortPlugin.CurrentMode;
                if (mode == SortMode.Default) return;

                var result = SortEngine.Apply(songs, mode);
                if (result.Songs == null || result.Songs.Count == 0) return;

                songs = result.Songs;
                SortPlugin.Log($"[Sort] 歌单排序（{SortModeInfo.DisplayName(mode)}）：{result.Note}，" +
                    $"共 {songs.Count} 首，播放位置 #{playIndex}");
            }
            catch (Exception ex)
            {
                SortPlugin.Warn("[Sort] 排序补丁异常（保持原顺序）: " + ex.Message);
            }
        }

        private static void TickPostfix()
        {
            SortPlugin.Tick();
        }

        private static void SearchPrefix()
        {
            _skipNextInject = true;
        }

        /// <summary>
        /// 启动恢复歌单之后：后台把该歌单从网易云重新拉一遍，
        /// 有变化就静默替换成最新版本（仍然不自动播放）。
        /// </summary>
        private static void RestorePostfix()
        {
            try
            {
                if (SortPlugin.SortOnStartupRestore == null || !SortPlugin.SortOnStartupRestore.Value) return;

                var latest = PlaylistStore.GetLatest();
                if (latest?.Songs == null || latest.Songs.Count == 0) return;

                var pid = latest.Id;
                SortPlugin.Log($"[Sort] 启动同步：正在从网易云刷新歌单「{latest.Name}」({pid})…");

                var bridge = Plugin.Bridge;
                if (bridge == null || !bridge.IsLoggedIn)
                {
                    SortPlugin.Warn("[Sort] 启动同步跳过：桥接未就绪");
                    return;
                }

                System.Threading.Tasks.Task.Run(() =>
                {
                    try { return bridge.GetPlaylistSongs(pid, true); }
                    catch (Exception ex)
                    {
                        SortPlugin.Warn("[Sort] 启动同步拉取失败: " + ex.Message);
                        return null;
                    }
                }).ContinueWith(t =>
                {
                    var fresh = t.Result;
                    if (fresh == null || fresh.Count == 0) return;

                    if (SameIds(latest.Songs, fresh))
                    {
                        SortPlugin.Log($"[Sort] 启动同步：歌单无变化（{fresh.Count} 首）");
                        return;
                    }

                    SortPlugin.Log($"[Sort] 启动同步：云端已变化 {latest.Songs.Count} → {fresh.Count} 首，重新注入");
                    PlaylistStore.Upsert(pid, latest.Name, fresh);

                    // P/Invoke 回调在线程池线程，而 Unity API 只能在主线程 → 排进队列
                    SortPluginMainThread.Run(() =>
                    {
                        try
                        {
                            var result = SortEngine.Apply(new List<SongInfo>(fresh), SortPlugin.CurrentMode);
                            PlaylistLink.InjectPlaylist(result.Songs, -1);
                            Toast.Show($"歌单已同步到最新：{fresh.Count} 首", 3f);
                        }
                        catch (Exception ex)
                        {
                            SortPlugin.Warn("[Sort] 启动同步重新注入失败: " + ex.Message);
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                SortPlugin.Warn("[Sort] 启动同步异常: " + ex.Message);
            }
        }

        private static bool SameIds(List<SongInfo> a, List<SongInfo> b)
        {
            if (a == null || b == null) return false;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if ((a[i]?.Id ?? 0) != (b[i]?.Id ?? 0)) return false;
            }
            return true;
        }

        private static void OnGuiPostfix()
        {
            Toast.Draw();
        }
    }

    /// <summary>把线程池里的动作排到主线程执行（Unity API 只能在主线程碰）。</summary>
    public static class SortPluginMainThread
    {
        private static readonly Queue<Action> Queue = new Queue<Action>();
        private static readonly object Gate = new object();

        public static void Run(Action action)
        {
            if (action == null) return;
            lock (Gate) Queue.Enqueue(action);
        }

        public static void Drain()
        {
            while (true)
            {
                Action a;
                lock (Gate)
                {
                    if (Queue.Count == 0) return;
                    a = Queue.Dequeue();
                }
                try { a(); }
                catch (Exception ex) { SortPlugin.Warn("[Sort] 主线程任务异常: " + ex.Message); }
            }
        }
    }
}
