using System;
using System.Reflection;
using ChillNetease.Plugin;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 自动化自检钩子：**只有设了环境变量才会生效**，正常玩游戏完全不触发。
    ///
    /// 存在的原因：面板是靠键盘热键（F6）打开的，而在自动化环境里往游戏窗口注入按键
    /// 极不可靠 —— Windows 前台锁会让 SetForegroundWindow 静默失败，
    /// 同机器上别的全屏程序还会把按键吞掉，表现为「日志里没有面板打开、画面也没变」。
    /// 与其跟输入链路较劲，不如直接调上游开面板的 API。
    ///
    /// 环境变量：
    ///   CNSORT_SELFTEST=panel            启动后自动打开 F6 面板
    ///   CNSORT_SELFTEST=panel:import      打开面板并切到「导入」分组（用户歌单列表）
    ///   CNSORT_SELFTEST=panel:songs       打开面板并切到「歌曲列表」视图
    ///   CNSORT_SELFTEST=quit              5 秒后自动退出游戏（给无人值守跑批用）
    /// </summary>
    public static class SelfTest
    {
        private static string _mode;
        private static bool _resolved;
        private static float _t0 = -1f;
        private static bool _done;
        private static bool _quitFired;

        public static void Tick()
        {
            try
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _mode = Environment.GetEnvironmentVariable("CNSORT_SELFTEST");
                    if (string.IsNullOrEmpty(_mode)) return;   // 正常玩：一次性判断完就再也不进来
                    _t0 = UnityEngine.Time.realtimeSinceStartup;
                    SortPlugin.Log("[SelfTest] 环境变量 CNSORT_SELFTEST=" + _mode + " → 自检模式启用");
                }
                if (_done || _t0 < 0f) return;

                var t = UnityEngine.Time.realtimeSinceStartup - _t0;

                if (_mode.StartsWith("quit"))
                {
                    if (!_quitFired && t > 5f)
                    {
                        _quitFired = true;
                        _done = true;
                        SortPlugin.Log("[SelfTest] 自检结束：退出游戏");
                        UnityEngine.Application.Quit();
                    }
                    return;
                }

                // 等 1.5 秒：让上游把登录态、歌单、本地存档都恢复完，面板才有内容
                if (t < 1.5f) return;
                if (!_done)
                {
                    _done = true;
                    OpenPanel();
                }
            }
            catch (Exception ex)
            {
                _done = true;
                SortPlugin.Warn("[SelfTest] 异常: " + ex.Message);
            }
        }

        private static void OpenPanel()
        {
            var ui = typeof(NeteaseUi);

            // 分组：Mine=0 / Collected=1 / Imported=2（用户自己的歌单在「导入」里）
            var wantImported = _mode.EndsWith("import");
            var wantSongs = _mode.EndsWith("songs");

            if (wantImported || wantSongs)
            {
                TryInvoke(ui, "SetGroup", wantImported ? 2 : 0);
            }

            // OpenStoredPlaylist 需要一个歌单 id；这里不传，只把面板打开即可。
            var open = ui.GetMethod("Open", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (open != null) { TryCall(open, null); return; }

            // 上游没有无参 Open，就直接翻 _windowOpen 私有字段
            var f = ui.GetField("_windowOpen", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (f != null)
            {
                f.SetValue(null, true);
                SortPlugin.Log("[SelfTest] 已直接置 _windowOpen=true 打开面板");
                return;
            }

            SortPlugin.Warn("[SelfTest] 找不到上游开面板的入口");
        }

        private static void TryInvoke(Type t, string name, params object[] args)
        {
            var m = t.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
            if (m == null) return;
            TryCall(m, args);
        }

        private static void TryCall(MethodInfo m, object[] args)
        {
            try { m.Invoke(null, args); }
            catch (Exception ex) { SortPlugin.Warn("[SelfTest] " + m.Name + " 调用失败: " + ex.Message); }
        }
    }
}