using System;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 歌单排序模式。切换顺序按枚举值递增循环（热键依次切换）。
    /// </summary>
    public enum SortMode
    {
        /// <summary>保持歌单原始顺序（不做任何事）。</summary>
        Default = 0,

        /// <summary>按全部时间的听歌次数从多到少（网易云「听歌排行」全时间，最多 100 首有记录）。</summary>
        PlayCountAll = 1,

        /// <summary>按最近一周的听歌次数从多到少（网易云「听歌排行」最近一周）。</summary>
        PlayCountWeek = 2,

        /// <summary>最近收藏优先（红心时间从新到旧）。</summary>
        RecentLiked = 3,

        /// <summary>最早收藏优先（红心时间从旧到新）。</summary>
        OldestLiked = 4,

        /// <summary>按专辑归组（同专辑的歌连在一起，专辑内保持原顺序）。</summary>
        Album = 5,

        /// <summary>按歌手归组（同歌手的歌连在一起）。</summary>
        Artist = 6,

        /// <summary>按歌名排序（忽略大小写与拼音外的符号）。</summary>
        Title = 7,
    }

    public static class SortModeInfo
    {
        public static readonly SortMode[] All =
        {
            SortMode.Default,
            SortMode.PlayCountAll,
            SortMode.PlayCountWeek,
            SortMode.RecentLiked,
            SortMode.OldestLiked,
            SortMode.Album,
            SortMode.Artist,
            SortMode.Title,
        };

        /// <summary>给玩家看的中文名。</summary>
        public static string DisplayName(SortMode m)
        {
            switch (m)
            {
                case SortMode.PlayCountAll: return "听歌次数（全部时间）";
                case SortMode.PlayCountWeek: return "听歌次数（最近一周）";
                case SortMode.RecentLiked: return "最近收藏优先";
                case SortMode.OldestLiked: return "最早收藏优先";
                case SortMode.Album: return "按专辑归组";
                case SortMode.Artist: return "按歌手归组";
                case SortMode.Title: return "按歌名";
                default: return "歌单原顺序";
            }
        }

        /// <summary>写进配置文件的短名。</summary>
        public static string ConfigValue(SortMode m) => m.ToString();

        /// <summary>从配置字符串解析，非法值回落到 Default。</summary>
        public static SortMode Parse(string s, SortMode fallback = SortMode.Default)
        {
            if (string.IsNullOrWhiteSpace(s)) return fallback;
            s = s.Trim();
            foreach (var m in All)
            {
                if (string.Equals(m.ToString(), s, StringComparison.OrdinalIgnoreCase)) return m;
            }
            // 容错：允许写 playcount / recent / week 之类的简写
            var lower = s.ToLowerInvariant();
            if (lower.Contains("week")) return SortMode.PlayCountWeek;
            if (lower.Contains("play") && lower.Contains("count")) return SortMode.PlayCountAll;
            if (lower.Contains("recent") || lower.Contains("new")) return SortMode.RecentLiked;
            if (lower.Contains("old") || lower.Contains("early")) return SortMode.OldestLiked;
            if (lower.Contains("album")) return SortMode.Album;
            if (lower.Contains("artist") || lower.Contains("singer")) return SortMode.Artist;
            if (lower.Contains("title") || lower.Contains("name")) return SortMode.Title;
            if (lower.Contains("default") || lower.Contains("none") || lower.Contains("off")) return SortMode.Default;
            return fallback;
        }

        /// <summary>循环切到下一个模式。</summary>
        public static SortMode Next(SortMode m)
        {
            var i = Array.IndexOf(All, m);
            if (i < 0) i = 0;
            return All[(i + 1) % All.Length];
        }

        /// <summary>
        /// 是否是「归组型」模式（按专辑/歌手/歌名）。
        /// 这类模式只在用户主动点开歌单时生效 —— 因为"导入歌单"是用户自己挑的一批歌，
        /// 保持原顺序才是他的意图。
        /// </summary>
        public static bool IsGrouping(SortMode m) =>
            m == SortMode.Album || m == SortMode.Artist || m == SortMode.Title;
    }
}
