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
