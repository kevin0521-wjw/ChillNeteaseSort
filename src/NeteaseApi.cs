using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.Networking;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 网易云「免加密旧版 api」客户端。
    ///
    /// 为什么不用 chillnetease 的原生桥？因为 ChillNetease.dll（Go 编译）只导出 35 个固定函数，
    /// 里面**没有**听歌排行 / 收藏顺序这两个接口，也改不了那个二进制。
    /// 好在网易云有几个老接口至今不需要 weapi/eapi 加密，带 MUSIC_U cookie 直接 GET 就能拿到数据。
    ///
    /// 走 UnityWebRequest（游戏本身就用它下载音频，已验证能出网、TLS 正常），
    /// 请求在主线程发起、每帧 Pump 推进，不阻塞游戏。
    /// </summary>
    public static class NeteaseApi
    {
        private const string Ua =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        private sealed class Job
        {
            public string Url;
            public Action<string> OnOk;
            public Action<string> OnFail;
            public float Deadline;
        }

        private static readonly Queue<Job> Pending = new Queue<Job>();
        private static UnityWebRequest _current;
        private static Job _currentJob;

        public static bool IsBusy => _current != null || Pending.Count > 0;

        // ---------------- cookie ----------------

        private static string _cookieCache;
        private static DateTime _cookieCacheAt = DateTime.MinValue;

        /// <summary>
        /// 取登录 cookie。优先用插件目录里 go-musicfox 写入的 cookie 文件（扫码登录后由原生桥更新），
        /// 这样和游戏里的登录态永远一致；配置里手填的 cookie 作为兜底。
        /// </summary>
        public static string GetCookie()
        {
            var manual = SortPlugin.ManualCookie?.Value;
            if (!string.IsNullOrWhiteSpace(manual))
            {
                var m = manual.Trim();
                // 允许直接粘贴 "MUSIC_U=xxx; __csrf=yyy" 或整个 Netscape 文件内容
                return m.Contains("\t") ? ParseNetscape(m) ?? m : m;
            }

            if (_cookieCache != null && (DateTime.UtcNow - _cookieCacheAt).TotalSeconds < 30)
            {
                return _cookieCache;
            }

            _cookieCache = null;
            _cookieCacheAt = DateTime.UtcNow;
            try
            {
                var candidates = new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "go-musicfox", "cookie"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "go-musicfox", "cookie"),
                };
                foreach (var path in candidates)
                {
                    if (!File.Exists(path)) continue;
                    var parsed = ParseNetscape(File.ReadAllText(path));
                    if (!string.IsNullOrEmpty(parsed))
                    {
                        _cookieCache = parsed;
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                SortPlugin.Warn("[Sort] 读取 cookie 失败: " + ex.Message);
            }
            return _cookieCache;
        }

        /// <summary>
        /// 从当前 cookie 里取 <c>__csrf</c> 的值。
        /// 每日推荐接口带不带它都通，但带上更稳（部分账号/风控状态下不带会返回空列表）；
        /// 写接口（建歌单/加歌）则**必须**带，漏了会报 code 301「系统错误」（其实是缺 csrf）。
        /// </summary>
        public static string GetCsrf()
        {
            try
            {
                var ck = GetCookie();
                if (string.IsNullOrEmpty(ck)) return null;
                foreach (var part in ck.Split(';'))
                {
                    var s = part.Trim();
                    if (s.StartsWith("__csrf=", StringComparison.Ordinal))
                        return s.Substring(7);
                }
            }
            catch { }
            return null;
        }

        /// <summary>把 Netscape cookiejar 文本解析成 Cookie 请求头（只取 music.163.com 的键值）。</summary>
        private static string ParseNetscape(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var parts = new List<string>();
            var seen = new HashSet<string>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Length == 0 || line[0] == '#') continue;
                var cols = line.Split('\t');
                if (cols.Length < 7) continue;
                if (!cols[0].Contains("163.com")) continue;
                var name = cols[5];
                if (!seen.Add(name)) continue;
                parts.Add(name + "=" + cols[6]);
            }
            return parts.Count == 0 ? null : string.Join("; ", parts);
        }

        // ---------------- 请求队列 ----------------

        public static void Get(string url, Action<string> onOk, Action<string> onFail)
        {
            Pending.Enqueue(new Job { Url = url, OnOk = onOk, OnFail = onFail });
        }

        /// <summary>每帧推进（必须在主线程调用）。</summary>
        public static void Pump()
        {
            if (_current == null)
            {
                if (Pending.Count == 0) return;
                _currentJob = Pending.Dequeue();
                try
                {
                    var req = UnityWebRequest.Get(_currentJob.Url);
                    var cookie = GetCookie();
                    if (!string.IsNullOrEmpty(cookie)) req.SetRequestHeader("Cookie", cookie);
                    req.SetRequestHeader("User-Agent", Ua);
                    req.SetRequestHeader("Referer", "https://music.163.com/");
                    req.timeout = 20;
                    req.SendWebRequest();
                    _current = req;
                    _currentJob.Deadline = UnityEngine.Time.realtimeSinceStartup + 25f;
                }
                catch (Exception ex)
                {
                    var job = _currentJob;
                    _currentJob = null;
                    job?.OnFail?.Invoke("请求创建失败: " + ex.Message);
                }
                return;
            }

            var done = _current.isDone;
            var timedOut = UnityEngine.Time.realtimeSinceStartup > _currentJob.Deadline;
            if (!done && !timedOut) return;

            var j = _currentJob;
            var r = _current;
            _current = null;
            _currentJob = null;

            if (timedOut && !done)
            {
                try { r.Abort(); } catch { }
                try { r.Dispose(); } catch { }
                j.OnFail?.Invoke("请求超时");
                return;
            }

            try
            {
                var err = r.error;
                var text = r.downloadHandler != null ? r.downloadHandler.text : null;
                var code = (int)r.responseCode;
                if (!string.IsNullOrEmpty(err) || code < 200 || code >= 300 || string.IsNullOrEmpty(text))
                {
                    j.OnFail?.Invoke($"HTTP {code} {err}".Trim());
                }
                else
                {
                    j.OnOk?.Invoke(text);
                }
            }
            catch (Exception ex)
            {
                j.OnFail?.Invoke("读取响应失败: " + ex.Message);
            }
            finally
            {
                try { r.Dispose(); } catch { }
            }
        }

        public static void ClearQueue()
        {
            Pending.Clear();
            if (_current != null)
            {
                try { _current.Abort(); } catch { }
                try { _current.Dispose(); } catch { }
            }
            _current = null;
            _currentJob = null;
        }

        // ---------------- 刷新流程 ----------------

        private static bool _refreshing;

        public static bool IsRefreshing => _refreshing;

        /// <summary>
        /// 拉一轮「口味档案」：
        /// account → 用户歌单（找红心歌单）→ 听歌排行(全时间/最近一周) → 红心歌单 trackIds。
        /// 全链路串行，任一步失败都不影响后面的可用性（能拿到多少算多少）。
        /// </summary>
        public static void StartRefresh(Action<bool, string> onDone = null)
        {
            if (_refreshing) return;
            if (string.IsNullOrEmpty(GetCookie()))
            {
                SortPlugin.Warn("[Sort] 没有可用的登录 cookie（先在游戏里 F6 扫码登录网易云）");
                onDone?.Invoke(false, "未登录");
                return;
            }

            _refreshing = true;
            TasteProfile.BeginRefresh();
            SortPlugin.Log("[Sort] 开始联网刷新口味档案…");

            var failed = new List<string>();

            // 1) 账号信息 → uid
            Get("https://music.163.com/api/nuser/account/get", body =>
            {
                long uid = 0;
                try
                {
                    var root = JObject.Parse(body);
                    uid = root["account"]?["id"]?.Value<long>() ?? 0;
                }
                catch (Exception ex) { failed.Add("account 解析: " + ex.Message); }

                if (uid <= 0)
                {
                    _refreshing = false;
                    TasteProfile.FinishRefresh(false, "cookie 已失效（拿不到账号）");
                    SortPlugin.Warn("[Sort] cookie 已失效，拿不到账号。请在游戏里重新扫码登录。");
                    onDone?.Invoke(false, "cookie 失效");
                    return;
                }
                TasteProfile.SetUser(uid);
                SortPlugin.Log($"[Sort] 账号 uid={uid}");

                // 2) 歌单列表 → 找「我喜欢的音乐」(specialType = 5)
                StepPlaylists(uid, failed, onDone);
            }, err =>
            {
                _refreshing = false;
                TasteProfile.FinishRefresh(false, "账号接口失败: " + err);
                onDone?.Invoke(false, err);
            });
        }

        private static void StepPlaylists(long uid, List<string> failed, Action<bool, string> onDone)
        {
            Get($"https://music.163.com/api/user/playlist?uid={uid}&limit=1000&offset=0", body =>
            {
                long likedId = 0;
                try
                {
                    var root = JObject.Parse(body);
                    var arr = root["playlist"] as JArray;
                    if (arr != null)
                    {
                        foreach (var p in arr)
                        {
                            var st = p["specialType"]?.Value<int>() ?? 0;
                            if (st == 5) { likedId = p["id"]?.Value<long>() ?? 0; break; }
                        }
                        // 兜底：名字里带「喜欢」
                        if (likedId == 0)
                        {
                            foreach (var p in arr)
                            {
                                var name = p["name"]?.Value<string>() ?? "";
                                if (name.Contains("喜欢")) { likedId = p["id"]?.Value<long>() ?? 0; break; }
                            }
                        }
                    }
                }
                catch (Exception ex) { failed.Add("playlist 解析: " + ex.Message); }

                if (likedId > 0)
                {
                    TasteProfile.SetLikedPlaylistId(likedId);
                    SortPlugin.Log($"[Sort] 红心歌单 id={likedId}");
                }

                // 3) 听歌排行：全部时间
                StepPlayRecord(uid, false, failed, () =>
                    // 4) 听歌排行：最近一周
                    StepPlayRecord(uid, true, failed, () =>
                        // 5) 红心歌单 trackIds（收藏顺序）
                        StepLikeOrder(likedId, failed, () =>
                        {
                            _refreshing = false;
                            var note = failed.Count == 0 ? null : string.Join("；", failed);
                            TasteProfile.FinishRefresh(true, note);
                            onDone?.Invoke(true, note);
                        })));
            }, err =>
            {
                failed.Add("歌单列表失败: " + err);
                // 歌单列表失败不影响听歌次数
                StepPlayRecord(uid, false, failed, () =>
                    StepPlayRecord(uid, true, failed, () =>
                    {
                        _refreshing = false;
                        TasteProfile.FinishRefresh(true, string.Join("；", failed));
                        onDone?.Invoke(true, string.Join("；", failed));
                    }));
            });
        }

        private static void StepPlayRecord(long uid, bool week, List<string> failed, Action next)
        {
            var type = week ? 1 : 0;
            Get($"https://music.163.com/api/v1/play/record?uid={uid}&type={type}", body =>
            {
                var counts = new Dictionary<long, int>();
                try
                {
                    var root = JObject.Parse(body);
                    var arr = (week ? root["weekData"] : root["allData"]) as JArray;
                    if (arr != null)
                    {
                        foreach (var item in arr)
                        {
                            var id = item["song"]?["id"]?.Value<long>() ?? 0;
                            var c = item["playCount"]?.Value<int>() ?? 0;
                            if (id > 0) counts[id] = c;
                        }
                    }
                }
                catch (Exception ex) { failed.Add("听歌排行解析: " + ex.Message); }
                TasteProfile.SetPlayCounts(week, counts);
                SortPlugin.Log($"[Sort] 听歌排行({(week ? "最近一周" : "全部时间")}): {counts.Count} 首");
                next();
            }, err =>
            {
                failed.Add($"听歌排行{(week ? "周" : "全部")}失败: {err}");
                next();
            });
        }

        private static void StepLikeOrder(long likedId, List<string> failed, Action next)
        {
            if (likedId <= 0) { next(); return; }
            Get($"https://music.163.com/api/v6/playlist/detail?id={likedId}&n=1000&s=8", body =>
            {
                try
                {
                    var root = JObject.Parse(body);
                    var node = root["playlist"] ?? root["result"];
                    var ids = node?["trackIds"] as JArray;
                    var list = new List<long>();
                    if (ids != null)
                    {
                        foreach (var t in ids)
                        {
                            var id = t is JObject o ? (o["id"]?.Value<long>() ?? 0) : t.Value<long>();
                            if (id > 0) list.Add(id);
                        }
                    }
                    TasteProfile.SetLikeOrder(list, SortPlugin.LikeNewestFirst?.Value ?? true);
                    SortPlugin.Log($"[Sort] 红心歌单顺序: {list.Count} 首（" +
                        $"{(SortPlugin.LikeNewestFirst?.Value ?? true ? "trackIds[0]=最近收藏" : "已反转")}）");
                }
                catch (Exception ex) { failed.Add("收藏顺序解析: " + ex.Message); }
                next();
            }, err =>
            {
                failed.Add("收藏顺序失败: " + err);
                next();
            });
        }
    }
}
