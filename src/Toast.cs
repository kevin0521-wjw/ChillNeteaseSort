using System;
using System.Collections.Generic;
using UnityEngine;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 屏幕提示：支持**多行堆叠**（多件事同时发生时不会互相顶掉）。
    ///
    /// 为什么自己做 MonoBehaviour 而不是借上游的 IMGUI 时机：
    ///   上游 <c>NeteaseUi.Render()</c> 第一行是「面板没开就直接 return」，
    ///   它唯一的调用方 <c>NeteaseUiRenderer.OnGUI()</c> 又是这样写的：
    ///   <code>
    ///   void OnGUI() {
    ///       if (Instance != this) return;
    ///       NeteaseUi.Render();   // ← 包在一个 try 块里
    ///   }
    ///   </code>
    ///   Harmony 的 finalizer 默认按「try 块起止位置」匹配过滤，
    ///   这个 return 落在 catch 之后，过滤器判定不命中 → finalizer 也补不上。
    ///   （实测：面板关着时 toast 的绘制回调一次都没进来过。）
    ///
    ///   所以改成挂在**游戏自己的第一个 MonoBehaviour** 上：往它身上 AddComponent 一个
    ///   只有 OnGUI 的组件。面板开不开都跟提示无关，也完全不依赖上游有没有异常处理块。
    /// </summary>
    public static class Toast
    {
        /// <summary>最多同时显示的条数（再多就挤满屏幕了）。</summary>
        private const int MaxLines = 3;

        private static readonly List<string> Lines = new List<string>();
        private static readonly List<float> Untils = new List<float>();

        private static int _failLogged;

        public static void Show(string msg, float seconds = 2.5f)
        {
            if (string.IsNullOrEmpty(msg)) return;
            var until = Time.realtimeSinceStartup + seconds;

            // 同一条消息（比如连续按热键）直接续命，不要堆三条一样的
            var idx = Lines.LastIndexOf(msg);
            if (idx >= 0)
            {
                Untils[idx] = until;
            }
            else
            {
                Lines.Add(msg);
                Untils.Add(until);
                while (Lines.Count > MaxLines) { Lines.RemoveAt(0); Untils.RemoveAt(0); }
            }

            SortPlugin.Log("[Sort] " + msg);
        }

        public static void Tick()
        {
            if (Lines.Count == 0) return;
            var now = Time.realtimeSinceStartup;
            for (int i = Lines.Count - 1; i >= 0; i--)
            {
                if (now > Untils[i])
                {
                    Lines.RemoveAt(i);
                    Untils.RemoveAt(i);
                }
            }
        }

        /// <summary>由 <see cref="ToastRenderer"/> 的 OnGUI 调用（每帧多次，GUI 事件驱动）。</summary>
        public static void Draw()
        {
            if (Lines.Count == 0) return;
            try
            {
                var bg = UiAssets.Background;
                var style = UiAssets.ToastStyle;

                const float Gap = 6f;
                var lineHeight = style.CalcSize(new GUIContent("测")).y + 20f;

                // 上游面板在屏幕右侧，会压住"下方居中"的提示 → 面板开着时挪到左下角
                var panelOpen = SortPlugin.PanelOpen;

                for (int i = 0; i < Lines.Count; i++)
                {
                    var text = Lines[i];
                    var size = style.CalcSize(new GUIContent(text));
                    var w = Mathf.Min(Screen.width - 80f, size.x + 44f);
                    var bottom = Screen.height - 150f - i * (lineHeight + Gap);

                    float x;
                    if (panelOpen)
                    {
                        x = 24f;                                  // 左下角，贴左边缘往里 24px
                        w = Mathf.Min(Screen.width * 0.55f, w);
                    }
                    else
                    {
                        x = (Screen.width - w) / 2f;              // 屏幕下方居中
                    }

                    var rect = new Rect(x, bottom, w, lineHeight);
                    GUI.DrawTexture(rect, bg);
                    GUI.Label(rect, text, style);
                }
            }
            catch (Exception ex)
            {
                // 绘制失败不能影响游戏，但必须留下痕迹 —— 否则「提示看不见」只有一个黑洞可查
                _failLogged++;
                if (_failLogged <= 3)
                    SortPlugin.Warn("[Sort] 提示绘制失败: " + ex.GetType().Name + " " + ex.Message);
            }
        }

        /// <summary>清空（切场景时由渲染宿主回调，避免旧提示残留到下一个场景）。</summary>
        public static void Clear()
        {
            Lines.Clear();
            Untils.Clear();
        }
    }

    /// <summary>
    /// 宿主组件：由 <see cref="SortPlugin"/> 挂到游戏已有的 MonoBehaviour 所在 GameObject 上。
    ///
    /// 两个已踩实的坑（都实测过）：
    ///   1. 挂到 <b>正在运行</b>的 GameObject 上时，<c>OnGUI</c> 不一定会被驱动
    ///      （Unity 只在 Awake 时给脚本登记 GUI 事件）—— 所以宿主宁可挑
    ///      <c>enabled = true</c> 的上游组件，并保留"每帧重试"逻辑；
    ///   2. <c>Update</c> 里调 <c>GUI.DrawTexture</c> 会直接抛
    ///      <c>ArgumentException: You can only call GUI functions from inside OnGUI</c>。
    ///      别指望绕开 OnGUI 自己画，必须老老实实呆在 OnGUI 里。
    /// </summary>
    public sealed class ToastRenderer : MonoBehaviour
    {
        private void OnGUI()
        {
            Toast.Draw();
        }
    }

    /// <summary>
    /// 共享的 UI 资源（贴图/样式）。放在单独类里，避免 toast 与面板各自 new 一份。
    /// 注意：Texture2D / GUIStyle 都只能在主线程创建，所以全部懒加载 + 判空。
    /// </summary>
    public static class UiAssets
    {
        private static Texture2D _bg;
        private static GUIStyle _toastStyle;

        public static Texture2D Background
        {
            get
            {
                if (_bg == null)
                {
                    // 纯色背景用 1×1 贴图 + GUI.DrawTexture 拉伸，最省事也最稳
                    _bg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _bg.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.10f, 0.86f));
                    _bg.Apply();
                }
                return _bg;
            }
        }

        public static GUIStyle ToastStyle
        {
            get
            {
                if (_toastStyle == null)
                {
                    _toastStyle = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 15,
                        alignment = TextAnchor.MiddleCenter,
                        wordWrap = false
                    };
                    _toastStyle.normal.textColor = new Color(0.96f, 0.97f, 1f);
                }
                return _toastStyle;
            }
        }
    }
}
