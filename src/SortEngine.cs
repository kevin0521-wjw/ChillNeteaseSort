using System;
using System.Collections.Generic;
using System.Linq;
using ChillNetease.Plugin;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 排序引擎：纯函数，输入原始歌曲列表 + 模式，输出排序后的新列表。
    /// 不修改入参（调用方传进来的是别人的 List，动原地会脏面板缓存）。
    /// </summary>
    public static class SortEngine
    {
        public sealed class Result
        {
            public List<SongInfo> Songs;
            public bool Changed;
            /// <summary>有多少首真的命中了排序数据（用来判断"看起来没生效"是不是因为没有数据）。</summary>
            public int Matched;
            public int Total;
            public string Note;
        }

        public static Result Apply(List<SongInfo> songs, SortMode mode)
        {
            var res = new Result { Songs = songs, Total = songs?.Count ?? 0 };
            if (songs == null || songs.Count < 2) return res;

            switch (mode)
            {
                case SortMode.PlayCountAll:
                    return ByCount(songs, TasteProfile.PlayCountAll, mode, "全部时间");
                case SortMode.PlayCountWeek:
                    return ByCount(songs, TasteProfile.PlayCountWeek, mode, "最近一周");
                case SortMode.RecentLiked:
                    return ByLikeRank(songs, newestFirst: true);
                case SortMode.OldestLiked:
                    return ByLikeRank(songs, newestFirst: false);
                case SortMode.Album:
                    return ByGroup(songs, s => s.Album, "专辑", s => s.Album ?? "");
                case SortMode.Artist:
                    return ByGroup(songs, s => (s.Artists != null && s.Artists.Count > 0) ? s.Artists[0] : null,
                        "歌手", s => (s.Artists != null && s.Artists.Count > 0) ? s.Artists[0] : "未知歌手");
                case SortMode.Title:
                    return ByGroup(songs, s => s.Name, "歌名", s => s.Name ?? "");
                default:
                    res.Note = "原顺序（不做处理）";
                    return res;
            }
        }

        /// <summary>按听歌次数降序；没听过的歌保持原相对顺序排在后面（LINQ OrderBy 是稳定排序）。</summary>
        private static Result ByCount(List<SongInfo> songs, Dictionary<long, int> counts, SortMode mode, string label)
        {
            int matched = 0;
            var sorted = songs
                .OrderByDescending(s =>
                {
                    if (s == null) return 0;
                    if (counts.TryGetValue(s.Id, out var c)) { matched++; return c; }
                    return 0;
                })
                .ToList();

            var changed = !SameOrder(songs, sorted);
            return new Result
            {
                Songs = sorted,
                Changed = changed,
                Matched = matched,
                Note = matched == 0
                    ? $"没有「{label}」听歌记录命中本歌单（先正常听几首再看）"
                    : $"{label}听歌次数，命中 {matched}/{songs.Count} 首" + (changed ? "" : "（顺序未变）")
            };
        }

        /// <summary>按收藏序号排；没有收藏记录的歌固定排在最后。</summary>
        private static Result ByLikeRank(List<SongInfo> songs, bool newestFirst)
        {
            int matched = 0;
            var sorted = songs
                .OrderBy(s =>
                {
                    if (s == null) return int.MaxValue;
                    if (TasteProfile.LikeRank.TryGetValue(s.Id, out var r))
                    {
                        matched++;
                        // 用 rank 本身做升序：newestFirst 时 rank 0 = 最近收藏，直接升序
                        return newestFirst ? r : int.MaxValue - 1 - r;
                    }
                    return int.MaxValue;
                })
                .ToList();

            var changed = !SameOrder(songs, sorted);
            return new Result
            {
                Songs = sorted,
                Changed = changed,
                Matched = matched,
                Note = matched == 0
                    ? "没有任何收藏记录命中本歌单（红心歌单才有收藏时间）"
                    : $"{(newestFirst ? "最近收藏" : "最早收藏")}优先，命中 {matched}/{songs.Count} 首" +
                      (changed ? "" : "（顺序未变：本歌单原本就是这个顺序）")
            };
        }

        /// <summary>
        /// 按某个文本字段归组（专辑 / 歌手 / 歌名）。
        /// 组与组按名字排序，**组内保持原相对顺序** —— LINQ 的 OrderBy 是稳定排序，
        /// 所以同专辑/同歌手的几首歌不会被随机打乱，仍然按原来的先后来。
        /// 空值（歌手/专辑缺失）统一排到最后，用一个不可能出现的键占位。
        /// </summary>
        private static Result ByGroup(List<SongInfo> songs, Func<SongInfo, string> selector,
            string label, Func<SongInfo, string> displaySelector)
        {
            const string Tail = "\uFFFF\uFFFF";   // 撞不到任何正常名称，保证空值排最后

            int matched = 0;
            var sorted = songs
                .OrderBy(s =>
                {
                    if (s == null) return Tail;
                    var key = selector(s);
                    if (!string.IsNullOrWhiteSpace(key)) { matched++; return Normalize(key); }
                    return Tail;
                }, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var changed = !SameOrder(songs, sorted);

            // 组数 = 不同的非空键数量，用来在日志里说明"分了几张专辑"
            var groups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in songs)
            {
                if (s == null) continue;
                var key = selector(s);
                if (!string.IsNullOrWhiteSpace(key)) groups.Add(Normalize(key));
            }

            return new Result
            {
                Songs = sorted,
                Changed = changed,
                Matched = matched,
                Note = groups.Count == 0
                    ? $"没有可用的「{label}」信息"
                    : $"按「{label}」归组，{groups.Count} 组，命中 {matched}/{songs.Count} 首" +
                      (changed ? "" : "（顺序未变：本歌单原本就是这个顺序）")
            };
        }

        /// <summary>
        /// 归组用的键规整：去首尾空白、忽略大小写。
        /// 中文名称不能直接做拼音排序（.NET 在 netstandard2.1 上拿不到中文区域排序规则，
        /// 用 OrdinalIgnoreCase 至少能保证「同名必相邻」这一核心语义）。
        /// </summary>
        private static string Normalize(string s) => s == null ? "" : s.Trim();

        private static bool SameOrder(List<SongInfo> a, List<SongInfo> b)
        {
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (ReferenceEquals(a[i], b[i])) continue;
                var ia = a[i]?.Id ?? 0;
                var ib = b[i]?.Id ?? 0;
                if (ia != ib) return false;
            }
            return true;
        }
    }
}
