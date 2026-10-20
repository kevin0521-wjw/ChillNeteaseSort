using System;
using System.Collections.Generic;
using System.Linq;
using ChillNetease.Plugin;
using Newtonsoft.Json.Linq;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 「整张歌单全量」拉取器 —— 替代上游的 <c>Plugin.Bridge.GetPlaylistSongs</c>。
    ///
    /// 为什么必须自己写：
    /// 上游走 <c>/api/v6/playlist/detail</c>，而那个接口响应里的 <c>tracks</c> 字段
    /// **硬截在 1000 首**（实测 n=1000 和 n=5000 都只回 1000）。红心 2956 首就只能看到 1000 首，
    /// 这就是「歌单显示不全」的根因。
    ///
    /// 但同一个响应里的 <c>trackIds</c> 是**全量**的（2956 个一个不少），所以分两步走：
    ///   1) playlist/detail 只取 trackIds（全量的 id 列表）；
    ///   2) 每 200 个一批调 <c>/api/song/detail?ids=[...]</c> 补齐曲目信息
    ///      —— 这个接口单次最多吃 200 个 id，传 250 只回 201 首（实测）。
    ///
    /// 两步都只需要 cookie（MUSIC_U），免 weapi 加密。
    /// </summary>
    public static class FullPlaylist
    {
        /// <summary>song/detail 单次 id 上限。250 就只回 201 首，所以压到 200。</summary>
        private const int BatchSize = 200;

        /// <summary>
        /// 拉一张歌单的全部歌曲（保持云端 trackIds 的原始顺序）。
        /// 回调都在主线程（NeteaseApi 的 Pump 里）执行，可以直接碰 Unity API。
        /// </summary>
        /// <param name="pid">歌单 id</param>
        /// <param name="onProgress">进度：(已补齐, 总数)，用来刷提示</param>
        public static void Load(long pid, Action<List<SongInfo>> onDone,
            Action<string> onFail, Action<int, int> onProgress = null)
        {
            if (pid <= 0) { onFail?.Invoke("歌单 id 无效"); return; }

            var url = "https://music.163.com/api/v6/playlist/detail?id=" + pid + "&n=1&s=8";
            NeteaseApi.Get(url, body =>
            {
                List<long> ids;
                try
                {
                    var root = JObject.Parse(body);
                    var pl = root["playlist"] ?? root["result"];
                    if (pl == null) { onFail?.Invoke("响应里没有 playlist 字段"); return; }

                    ids = new List<long>();
                    var arr = pl["trackIds"] as JArray;
                    if (arr != null)
                    {
                        foreach (var t in arr)
                        {
                            var id = t is JObject o ? (o["id"]?.Value<long>() ?? 0) : t.Value<long>();
                            if (id > 0) ids.Add(id);
                        }
                    }
                    if (ids.Count == 0) { onFail?.Invoke("歌单里没有歌曲（或被设为私密）"); return; }

                    SortPlugin.Log($"[Sort] 全量拉取「{pl["name"]?.Value<string>()}」：" +
                        $"trackIds {ids.Count} 个，分 {(ids.Count + BatchSize - 1) / BatchSize} 批补齐");
                }
                catch (Exception ex)
                {
                    onFail?.Invoke("解析歌单失败: " + ex.Message);
                    return;
                }

                FetchBatches(ids, 0, new Dictionary<long, SongInfo>(), onDone, onFail, onProgress);
            },
            err => onFail?.Invoke("拉取歌单失败: " + err));
        }

        /// <summary>逐批拉，串行。单批失败不放弃整体（能拿到多少算多少）。</summary>
        private static void FetchBatches(List<long> ids, int offset,
            Dictionary<long, SongInfo> acc, Action<List<SongInfo>> onDone,
            Action<string> onFail, Action<int, int> onProgress)
        {
            if (offset >= ids.Count)
            {
                // 按云端原始顺序还原（song/detail 的返回顺序不保证跟入参一致）
                var list = new List<SongInfo>(acc.Count);
                foreach (var id in ids)
                {
                    if (acc.TryGetValue(id, out var s)) list.Add(s);
                }

                var missing = ids.Count - list.Count;
                SortPlugin.Log($"[Sort] 全量拉取完成：{list.Count}/{ids.Count} 首" +
                    (missing > 0 ? $"（{missing} 首已下架/无版权，已跳过）" : ""));
                if (list.Count == 0) { onFail?.Invoke("一首都没拿到，可能登录态失效"); return; }
                onDone?.Invoke(list);
                return;
            }

            var slice = ids.Skip(offset).Take(BatchSize).ToList();
            // 方括号必须转义，否则 Uri 构造会抛
            var url = "https://music.163.com/api/song/detail?ids=%5B"
                + string.Join("%2C", slice) + "%5D";

            NeteaseApi.Get(url, body =>
            {
                try
                {
                    var arr = JObject.Parse(body)["songs"] as JArray;
                    if (arr != null)
                    {
                        foreach (var o in arr)
                        {
                            var s = ToSongInfo(o);
                            if (s != null) acc[s.Id] = s;
                        }
                    }
                }
                catch (Exception ex)
                {
                    SortPlugin.Warn("[Sort] 解析歌曲批次失败: " + ex.Message);
                }

                onProgress?.Invoke(Math.Min(offset + BatchSize, ids.Count), ids.Count);
                FetchBatches(ids, offset + BatchSize, acc, onDone, onFail, onProgress);
            },
            err =>
            {
                SortPlugin.Warn($"[Sort] 第 {offset / BatchSize + 1} 批失败（跳过）: {err}");
                onProgress?.Invoke(Math.Min(offset + BatchSize, ids.Count), ids.Count);
                FetchBatches(ids, offset + BatchSize, acc, onDone, onFail, onProgress);
            });
        }

        /// <summary>
        /// 取字段，兼容两套命名：新版接口用缩写（<c>ar</c>/<c>al</c>/<c>dt</c>），
        /// 老版 `/api/song/detail` 用全称（<c>artists</c>/<c>album</c>/<c>duration</c>）。
        /// 注意：不能直接写 <c>o["ar"] ?? o["artists"]</c> —— key 存在但值是 JSON null 时
        /// JToken 不是 C# 的 null，`??` 不会回退。
        /// </summary>
        private static JToken Pick(JToken o, string primary, string fallback)
        {
            var v = o[primary];
            if (v != null && v.Type != JTokenType.Null) return v;
            return o[fallback];
        }

        /// <summary>
        /// 网易云 song 对象 → 上游 SongInfo。
        /// ⚠️ 单位：接口的 <c>dt</c>/<c>duration</c> 都是**毫秒**（如 225013），
        /// 而 SongInfo.Duration 用**秒**（游戏自己缓存的歌单里存的是 194.768 这种值，对照过）。
        /// </summary>
        private static SongInfo ToSongInfo(JToken o)
        {
            if (o == null) return null;
            var id = o["id"]?.Value<long>() ?? 0;
            if (id <= 0) return null;

            var artists = new List<string>();
            var ar = Pick(o, "ar", "artists") as JArray;
            if (ar != null)
            {
                foreach (var a in ar)
                {
                    var n = a?["name"]?.Value<string>();
                    if (!string.IsNullOrEmpty(n)) artists.Add(n);
                }
            }

            var al = Pick(o, "al", "album");
            var ms = Pick(o, "dt", "duration")?.Value<double>() ?? 0;

            return new SongInfo
            {
                Id = id,
                Name = o["name"]?.Value<string>() ?? "",
                Duration = ms / 1000.0,
                Artists = artists,
                Album = al?["name"]?.Value<string>() ?? "",
                AlbumId = al?["id"]?.Value<long>() ?? 0,
                CoverUrl = al?["picUrl"]?.Value<string>() ?? "",
            };
        }
    }
}
