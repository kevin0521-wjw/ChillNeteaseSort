using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using Newtonsoft.Json;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 「我的口味档案」：从网易云拉取的听歌次数与收藏顺序，供排序引擎使用。
    ///
    /// 数据来源（全部走免加密的旧版 api，只需 MUSIC_U cookie）：
    ///   - api/v1/play/record?uid=&amp;type=0   全部时间听歌排行（含 playCount，服务端最多返回 100 首）
    ///   - api/v1/play/record?uid=&amp;type=1   最近一周听歌排行
    ///   - api/user/playlist?uid=             用户歌单列表（找 specialType=5 的「我喜欢的音乐」）
    ///   - api/v6/playlist/detail?id=         歌单 trackIds（红心歌单的顺序 = 收藏时间从新到旧）
    ///
    /// 落盘到 BepInEx/config/ChillNeteaseSort.cache.json：进游戏先有上次数据可排，
    /// 联网刷新成功后覆盖 —— 断网也不至于排序全乱。
    /// </summary>
    public static class TasteProfile
    {
        private static readonly object Gate = new object();

        /// <summary>songId → 全部时间听歌次数。</summary>
        public static Dictionary<long, int> PlayCountAll { get; private set; } = new Dictionary<long, int>();

        /// <summary>songId → 最近一周听歌次数。</summary>
        public static Dictionary<long, int> PlayCountWeek { get; private set; } = new Dictionary<long, int>();

        /// <summary>songId → 收藏序号（0 = 最近收藏，越大越早）。</summary>
        public static Dictionary<long, int> LikeRank { get; private set; } = new Dictionary<long, int>();

        public static long UserId { get; private set; }
        public static long LikedPlaylistId { get; private set; }
        public static DateTime UpdatedAtUtc { get; private set; } = DateTime.MinValue;
        public static string LastError { get; private set; }

        /// <summary>本次启动后是否已经成功联网刷新过一次。</summary>
        public static bool RefreshedThisSession { get; private set; }

        public static bool HasData
        {
            get
            {
                lock (Gate)
                {
                    return PlayCountAll.Count > 0 || PlayCountWeek.Count > 0 || LikeRank.Count > 0;
                }
            }
        }

        public static string AgeText
        {
            get
            {
                if (UpdatedAtUtc == DateTime.MinValue) return "从未刷新";
                var span = DateTime.UtcNow - UpdatedAtUtc;
                if (span.TotalMinutes < 1) return "刚刚";
                if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
                if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
                return $"{(int)span.TotalDays} 天前";
            }
        }

        private static string CachePath => Path.Combine(Paths.ConfigPath, "ChillNeteaseSort.cache.json");

        // ---------------- 落盘 ----------------

        private sealed class CacheDto
        {
            [JsonProperty("userId")] public long UserId { get; set; }
            [JsonProperty("likedPlaylistId")] public long LikedPlaylistId { get; set; }
            [JsonProperty("updatedAt")] public DateTime UpdatedAt { get; set; }
            [JsonProperty("playCountAll")] public Dictionary<long, int> PlayCountAll { get; set; }
            [JsonProperty("playCountWeek")] public Dictionary<long, int> PlayCountWeek { get; set; }
            [JsonProperty("likeOrder")] public List<long> LikeOrder { get; set; }
        }

        public static void LoadCache()
        {
            lock (Gate)
            {
                try
                {
                    var path = CachePath;
                    if (!File.Exists(path)) return;
                    var dto = JsonConvert.DeserializeObject<CacheDto>(File.ReadAllText(path));
                    if (dto == null) return;

                    UserId = dto.UserId;
                    LikedPlaylistId = dto.LikedPlaylistId;
                    UpdatedAtUtc = dto.UpdatedAt.ToUniversalTime();
                    PlayCountAll = dto.PlayCountAll ?? new Dictionary<long, int>();
                    PlayCountWeek = dto.PlayCountWeek ?? new Dictionary<long, int>();
                    LikeRank = BuildRank(dto.LikeOrder);

                    SortPlugin.Log($"[Sort] 口味档案缓存已加载：听歌次数 {PlayCountAll.Count}/{PlayCountWeek.Count} 首，" +
                        $"收藏顺序 {LikeRank.Count} 首（{AgeText}）");
                }
                catch (Exception ex)
                {
                    SortPlugin.Warn("[Sort] 缓存读取失败（忽略）: " + ex.Message);
                }
            }
        }

        private static Dictionary<long, int> BuildRank(List<long> order)
        {
            var dict = new Dictionary<long, int>();
            if (order == null) return dict;
            for (int i = 0; i < order.Count; i++)
            {
                if (!dict.ContainsKey(order[i])) dict[order[i]] = i;
            }
            return dict;
        }

        private static void SaveCache()
        {
            try
            {
                CacheDto dto;
                lock (Gate)
                {
                    dto = new CacheDto
                    {
                        UserId = UserId,
                        LikedPlaylistId = LikedPlaylistId,
                        UpdatedAt = UpdatedAtUtc,
                        PlayCountAll = PlayCountAll,
                        PlayCountWeek = PlayCountWeek,
                        // 按 rank 还原成数组，省掉 JSON 里重复的 key
                        LikeOrder = LikeRank.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToList()
                    };
                }
                var dir = Path.GetDirectoryName(CachePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                var tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(dto));
                if (File.Exists(CachePath)) File.Delete(CachePath);
                File.Move(tmp, CachePath);
            }
            catch (Exception ex)
            {
                SortPlugin.Warn("[Sort] 缓存写入失败: " + ex.Message);
            }
        }

        // ---------------- 刷新（由 NeteaseApi 回调填充）----------------

        /// <summary>刷新开始（清错误）。</summary>
        public static void BeginRefresh()
        {
            lock (Gate) LastError = null;
        }

        public static void SetUser(long uid)
        {
            lock (Gate) UserId = uid;
        }

        public static void SetLikedPlaylistId(long id)
        {
            lock (Gate) LikedPlaylistId = id;
        }

        public static void SetPlayCounts(bool week, Dictionary<long, int> counts)
        {
            lock (Gate)
            {
                if (week) PlayCountWeek = counts ?? new Dictionary<long, int>();
                else PlayCountAll = counts ?? new Dictionary<long, int>();
            }
        }

        /// <summary>按 trackIds 原顺序填收藏序号（index 0 = 最新收藏）。</summary>
        public static void SetLikeOrder(List<long> trackIds, bool newestFirst)
        {
            var dict = new Dictionary<long, int>();
            if (trackIds != null)
            {
                var seq = newestFirst ? (IEnumerable<long>)trackIds : Enumerable.Reverse(trackIds);
                int i = 0;
                foreach (var id in seq)
                {
                    if (!dict.ContainsKey(id)) dict[id] = i;
                    i++;
                }
            }
            lock (Gate) LikeRank = dict;
        }

        /// <summary>一轮刷新收尾：落盘 + 标记。</summary>
        public static void FinishRefresh(bool ok, string error = null)
        {
            lock (Gate)
            {
                if (ok)
                {
                    UpdatedAtUtc = DateTime.UtcNow;
                    RefreshedThisSession = true;
                }
                LastError = error;
            }
            if (ok)
            {
                SaveCache();
                SortPlugin.Log($"[Sort] 口味档案刷新完成：全部时间 {PlayCountAll.Count} 首 / " +
                    $"最近一周 {PlayCountWeek.Count} 首 / 收藏顺序 {LikeRank.Count} 首");
            }
        }

        /// <summary>排序时可用的统计信息（写日志用）。</summary>
        public static string Describe()
        {
            lock (Gate)
            {
                return $"听歌次数(全部){PlayCountAll.Count} 首、听歌次数(周){PlayCountWeek.Count} 首、" +
                       $"收藏记录{LikeRank.Count} 首、更新于 {AgeText}";
            }
        }
    }
}
