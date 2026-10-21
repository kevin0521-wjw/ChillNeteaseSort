using System;
using System.Collections.Generic;
using System.Linq;
using ChillNetease.Plugin;

namespace ChillNeteaseSort
{
    /// <summary>音乐库条目的来源分类。</summary>
    public static class LibraryKind
    {
        public const string Liked = "liked";            // 我喜欢的音乐（specialType = 5）
        public const string Mine = "mine";              // 我自己创建的歌单
        public const string Subscribed = "subscribed";  // 我收藏的别人的歌单
        public const string Daily = "daily";            // 每日推荐（不是歌单，要现场拉）
        public const string Toplist = "toplist";        // 官方排行榜
    }

    /// <summary>
    /// 「我的音乐」里的一条可导入项 —— 一个歌单 / 一个榜单 / 今天的日推。
    /// 全部由**当前登录账号**实时拉取，插件里不写死任何人的歌单。
    /// </summary>
    public sealed class LibraryItem
    {
        public long Id;
        public string Name;
        public int TrackCount;
        public string Kind;
        public string Note;

        /// <summary>
        /// 写进本地歌单时用的名字：加中文方括号前缀，F6 面板的「导入」分组里一眼能分清来源。
        /// 不用 emoji —— 游戏内的 IMGUI 字体不一定带彩色 emoji，会显示成方框。
        /// </summary>
        public string Label
        {
            get
            {
                switch (Kind)
                {
                    case LibraryKind.Liked: return "【喜欢】" + Name;
                    case LibraryKind.Daily: return "【日推】" + Name;
                    case LibraryKind.Toplist: return "【榜单】" + Name;
                    case LibraryKind.Subscribed: return "【收藏】" + Name;
                    default: return "【我的】" + Name;
                }
            }
        }

        public string Describe()
        {
            var n = TrackCount > 0 ? TrackCount + " 首" : "首数未知";
            return string.IsNullOrEmpty(Note) ? Name + "（" + n + "）" : Name + "（" + n + " · " + Note + "）";
        }
    }

    /// <summary>
    /// 一键把「当前登录账号的音乐库」拉进游戏：我喜欢的音乐 / 每日推荐 / 排行榜 / 我的歌单 / 收藏的歌单。
    ///
    /// 为什么要有它：上游 ChillNetease 只能浏览「我创建的 / 我收藏的」两个分组，
    /// 想把这些歌**变成游戏里的本地歌单**得一张张点开、还只能存当前这一张；
    /// 而本插件早期版本要用户自己去 config 里手填歌单 id —— 对别人等于没用。
    /// 现在改成：F7 一次搞定，条目和数量由配置项控制。
    ///
    /// 全流程走已有的 NeteaseApi 请求队列（每帧 Pump），不阻塞游戏主线程。
    /// </summary>
    public static class MyLibrary
    {
        /// <summary>没配 MyLibraryToplistIds 时默认导这四个大榜（顺序即导入顺序）。</summary>
        private static readonly long[] DefaultToplists = { 3778678, 3779629, 19723756, 2884035 };

        public static bool IsRunning { get; private set; }
        public static long Uid { get; private set; }
        public static string Nickname { get; private set; }

        /// <summary>最近一次拉到的全部条目（含未导入的），供日志/排查用。</summary>
        public static readonly List<LibraryItem> Items = new List<LibraryItem>();

        private static int _okCount;
        private static int _failCount;
        private static int _songTotal;

        /// <summary>F7 入口。</summary>
        public static void Run()
        {
            if (IsRunning) { Toast.Show("上一次导入还没跑完，稍等", 2.5f); return; }

            if (string.IsNullOrEmpty(NeteaseApi.GetCookie()))
            {
                Toast.Show("还没登录网易云：先按 F6 扫码，再回来按 F7", 5f);
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
                Toast.Show("游戏音乐服务还没就绪，等一下再按 F7", 3f);
                return;
            }

            IsRunning = true;
            _okCount = _failCount = _songTotal = 0;
            Items.Clear();

            Toast.Show("正在读取你的网易云音乐库…", 3f);
            SortPlugin.Log("[Sort] ===== 我的音乐库：开始读取 =====");

            NeteaseApi.GetAccount((uid, nick) =>
            {
                Uid = uid;
                Nickname = nick;
                SortPlugin.Log($"[Sort] 账号：{nick ?? "?"}（uid={uid}）");

                NeteaseApi.GetUserPlaylists(uid, playlists =>
                {
                    Items.AddRange(playlists);
                    SortPlugin.Log($"[Sort] 歌单：共 {playlists.Count} 张" +
                        $"（我的 {playlists.Count(x => x.Kind == LibraryKind.Mine)} / " +
                        $"收藏 {playlists.Count(x => x.Kind == LibraryKind.Subscribed)} / " +
                        $"喜欢 {playlists.Count(x => x.Kind == LibraryKind.Liked)}）");

                    NeteaseApi.GetToplists(tops =>
                    {
                        Items.AddRange(tops);
                        SortPlugin.Log($"[Sort] 排行榜：网易云共 {tops.Count} 个，全部列在下面（想换榜就抄 id 填 MyLibrary.ToplistIds）");
                        foreach (var t in tops)
                            SortPlugin.Log($"[Sort]   榜单 {t.Id}  {t.Name}  {t.TrackCount} 首  {t.Note}");
                        StartImport();
                    },
                    err =>
                    {
                        // 榜单接口挂了不该拖累整个流程 —— 其它类别照常导
                        SortPlugin.Warn("[Sort] 排行榜拉取失败，跳过这一类: " + err);
                        StartImport();
                    });
                },
                err => Fail("歌单列表拉取失败：" + err));
            },
            err => Fail(err));
        }

        /// <summary>按配置挑出这次要导入的条目，然后串行拉取。</summary>
        private static void StartImport()
        {
            List<LibraryItem> plan;
            try
            {
                plan = BuildPlan();
            }
            catch (Exception ex)
            {
                Fail("生成导入清单失败: " + ex.Message);
                return;
            }

            if (plan.Count == 0)
            {
                Fail("按当前配置没有要导入的项（Data 分组里的 MyLibraryInclude* 全关了吗？）");
                return;
            }

            SortPlugin.Log($"[Sort] 本次将导入 {plan.Count} 项：");
            foreach (var it in plan)
                SortPlugin.Log($"[Sort]   · {it.Label}  id={it.Id}  {it.Describe()}");

            Toast.Show($"准备导入 {plan.Count} 项，开始拉取…", 2.5f);
            ImportAt(plan, 0);
        }

        private static void ImportAt(List<LibraryItem> plan, int idx)
        {
            if (idx >= plan.Count) { Finish(plan.Count); return; }

            var item = plan[idx];
            Toast.Show($"导入 {idx + 1}/{plan.Count}：{item.Name}", 2f);

            Action<List<SongInfo>> done = songs =>
            {
                try
                {
                    if (songs == null || songs.Count == 0)
                    {
                        _failCount++;
                        SortPlugin.Warn($"[Sort] 「{item.Name}」没有可用歌曲，跳过");
                        ImportAt(plan, idx + 1);
                        return;
                    }

                    // 每张歌单的歌曲数上限（0 = 不限）。大歌单合并导入会拖慢游戏列表。
                    var cap = SortPlugin.MyLibraryMaxSongs?.Value ?? 0;
                    if (cap > 0 && songs.Count > cap)
                    {
                        SortPlugin.Log($"[Sort] 「{item.Name}」{songs.Count} 首超过上限 {cap}，只取前 {cap} 首");
                        songs = songs.Take(cap).ToList();
                    }

                    // 用真实歌单 id 作 key：重启游戏后条目还在，且同一张歌单重复导入会覆盖而不是堆积
                    PlaylistStore.Upsert(item.Id, item.Label, songs);
                    _okCount++;
                    _songTotal += songs.Count;
                    SortPlugin.Log($"[Sort]   ✓ {item.Label} → {songs.Count} 首已存入本地歌单");
                }
                catch (Exception ex)
                {
                    _failCount++;
                    SortPlugin.Warn($"[Sort] 保存「{item.Name}」失败: {ex.Message}");
                }
                ImportAt(plan, idx + 1);
            };

            Action<string> fail = err =>
            {
                _failCount++;
                SortPlugin.Warn($"[Sort] 「{item.Name}」拉取失败: {err}");
                ImportAt(plan, idx + 1);
            };

            try
            {
                if (item.Kind == LibraryKind.Daily) DailyPlaylist.Load(done, fail);
                else FullPlaylist.Load(item.Id, done, fail,
                    (got, total) => Toast.Show($"补齐 {got}/{total}：{item.Name}", 1.2f));
            }
            catch (Exception ex)
            {
                fail(ex.Message);
            }
        }

        private static void Finish(int total)
        {
            IsRunning = false;

            try
            {
                var bridge = Plugin.Bridge;
                if (bridge != null) SortPlugin.TryRefreshPanelPlaylists(bridge);
            }
            catch (Exception ex) { SortPlugin.Warn("[Sort] 刷新面板失败: " + ex.Message); }

            var msg = _failCount == 0
                ? $"导入完成：{_okCount} 个歌单 / {_songTotal} 首，按 F6 在「导入」里挑"
                : $"导入完成：{_okCount} 个成功、{_failCount} 个失败（{_songTotal} 首），看日志";
            Toast.Show(msg, 6f);
            SortPlugin.Log($"[Sort] ===== 我的音乐库：结束（{_okCount} 成功 / {_failCount} 失败 / {_songTotal} 首）=====");
        }

        private static void Fail(string err)
        {
            IsRunning = false;
            SortPlugin.Warn("[Sort] 我的音乐库失败: " + err);
            Toast.Show("读取音乐库失败：" + err, 5f);
        }

        // ---------------- 导入清单 ----------------

        private static List<LibraryItem> BuildPlan()
        {
            var plan = new List<LibraryItem>();

            // 1) 我喜欢的音乐（永远是第一张，用户最常用）
            if (SortPlugin.MyLibraryIncludeLiked?.Value ?? true)
            {
                var liked = Items.FirstOrDefault(x => x.Kind == LibraryKind.Liked);
                if (liked != null) plan.Add(liked);
                else SortPlugin.Warn("[Sort] 账号里没找到「我喜欢的音乐」，跳过");
            }

            // 2) 每日推荐：不是歌单，导入时现场拉
            if (SortPlugin.MyLibraryIncludeDaily?.Value ?? true)
            {
                plan.Add(new LibraryItem
                {
                    Id = DailyPlaylist.LocalPlaylistId,
                    Name = DailyPlaylist.LocalName,
                    Kind = LibraryKind.Daily,
                    Note = "今天"
                });
            }

            // 3) 排行榜：按配置给的 id 顺序导（空 = 默认四大榜）
            if (SortPlugin.MyLibraryIncludeToplists?.Value ?? true)
            {
                var want = ParseIds(SortPlugin.MyLibraryToplistIds?.Value);
                if (want.Count == 0) want = DefaultToplists.ToList();

                var byId = new Dictionary<long, LibraryItem>();
                foreach (var t in Items.Where(x => x.Kind == LibraryKind.Toplist)) byId[t.Id] = t;

                foreach (var id in want)
                {
                    if (byId.TryGetValue(id, out var it)) plan.Add(it);
                    else SortPlugin.Warn($"[Sort] 排行榜 {id} 不在网易云返回的榜单列表里（可能已下线），跳过");
                }
            }

            // 4) 我的歌单 / 5) 收藏的歌单：保持网易云的原始顺序（新→旧），各限 N 张
            AddGroup(plan, LibraryKind.Mine, SortPlugin.MyLibraryIncludeMine?.Value ?? true);
            AddGroup(plan, LibraryKind.Subscribed, SortPlugin.MyLibraryIncludeSubscribed?.Value ?? false);

            return plan;
        }

        private static void AddGroup(List<LibraryItem> plan, string kind, bool enabled)
        {
            if (!enabled) return;

            var list = Items.Where(x => x.Kind == kind).ToList();
            var limit = SortPlugin.MyLibraryPlaylistLimit?.Value ?? 10;
            if (limit > 0 && list.Count > limit)
            {
                SortPlugin.Log($"[Sort] {kind} 共 {list.Count} 张，按 Data.MyLibraryPlaylistLimit 只导前 {limit} 张");
                list = list.Take(limit).ToList();
            }
            plan.AddRange(list);
        }

        private static List<long> ParseIds(string raw)
        {
            var ids = new List<long>();
            if (string.IsNullOrWhiteSpace(raw)) return ids;
            foreach (var part in raw.Split(new[] { ',', '，', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (long.TryParse(part.Trim(), out var id) && id > 0) ids.Add(id);
            }
            return ids;
        }
    }
}
