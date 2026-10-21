using System;
using System.Collections.Generic;
using ChillNetease.Plugin;
using Newtonsoft.Json.Linq;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 「每日推荐」（日推）拉取器。
    ///
    /// 接口：<c>GET /api/v3/discovery/recommend/songs</c> → <c>data.dailySongs</c>（每天约 30~35 首）。
    /// 免 weapi 加密，只要 cookie（实测 code=200，34 首）。
    /// 老接口 <c>/api/discovery/recommend/songs</c> 也还能用（返回 32 首），
    /// 但字段是旧的 <c>recommend[].artists/album/duration</c>，跟 v3 的 <c>ar/al/dt</c> 不一致 ——
    /// <see cref="FullPlaylist.ToSongInfo"/> 两套都兼容，所以兜底走它也不会出错。
    ///
    /// 曲目对象的结构跟 <c>/api/song/detail</c> 完全一致，所以不用额外补齐信息，一次请求就够。
    /// </summary>
    public static class DailyPlaylist
    {
        /// <summary>
        /// 日推在本地歌单里用的固定 id。
        ///
        /// ⚠️ 不能用负数：实测 <c>PlaylistStore.Upsert(-1, …)</c> 静默失败 ——
        /// 既没写进 <c>ChillNetease.playlists.json</c>，紧接着 <c>Find(-1)</c> 也返回 null
        /// （上游内部对 id 有正数校验）。第一版用 -1，日志里就出现了
        /// 「本地歌单 -1 不存在，跳过面板展示」。
        ///
        /// 现在用 2^53-1：网易云真实歌单 id 目前是 10~11 位（1e9~1.5e10 量级），
        /// 16 位的 9007199254740991 不可能撞车，同时远小于 long 上限不会溢出。
        /// 每天拉一次就 Upsert 覆盖同一个 id，本地不会越堆越多。
        /// </summary>
        public const long LocalPlaylistId = 9007199254740991L;

        /// <summary>拉今天的日推。回调在主线程（NeteaseApi.Pump 里）执行。</summary>
        public static void Load(Action<List<SongInfo>> onDone, Action<string> onFail)
        {
            var url = "https://music.163.com/api/v3/discovery/recommend/songs";
            var csrf = NeteaseApi.GetCsrf();
            if (!string.IsNullOrEmpty(csrf)) url += "?csrf_token=" + csrf;

            NeteaseApi.Get(url, body =>
            {
                List<SongInfo> list;
                try
                {
                    var root = JObject.Parse(body);
                    var code = root["code"]?.Value<int>() ?? 0;
                    if (code != 200)
                    {
                        // 301 = 未登录 / 登录态失效（这里最容易踩）
                        onFail?.Invoke($"接口返回 code={code}" + (code == 301 ? "（登录态失效，按 F6 重新扫码）" : ""));
                        return;
                    }

                    var data = root["data"] as JObject;
                    var arr = (data?["dailySongs"] as JArray) ?? (root["recommend"] as JArray);
                    if (arr == null)
                    {
                        onFail?.Invoke("响应里没有 dailySongs 字段（接口结构可能变了）");
                        return;
                    }

                    list = new List<SongInfo>(arr.Count);
                    foreach (var o in arr)
                    {
                        var s = FullPlaylist.ToSongInfo(o);
                        if (s != null) list.Add(s);
                    }
                }
                catch (Exception ex)
                {
                    onFail?.Invoke("解析日推失败: " + ex.Message);
                    return;
                }

                if (list.Count == 0)
                {
                    onFail?.Invoke("日推是空的（今天还没生成，或账号新到没有足够数据）");
                    return;
                }

                SortPlugin.Log($"[Sort] 日推拉到 {list.Count} 首");
                onDone?.Invoke(list);
            }, err => onFail?.Invoke("拉取日推失败: " + err));
        }

        /// <summary>今天的日推在本地歌单里叫什么（每次 Upsert 同名覆盖）。</summary>
        public static string LocalName => "每日推荐 " + DateTime.Now.ToString("MM-dd");
    }
}
