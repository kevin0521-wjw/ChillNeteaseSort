using UnityEngine;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 极简屏幕提示：切模式/刷新完成时在屏幕下方浮一条，2~4 秒后消失。
    /// 绘制挂在 上游 NeteaseUiRenderer.OnGUI 的 Postfix 上（借用它的 IMGUI 时机），
    /// 所以不需要自己建 MonoBehaviour，也不依赖游戏是否驱动插件的 Update。
    /// </summary>
    public static class Toast
    {
        private static string _text;
        private static float _until;
        private static Texture2D _bg;
        private static GUIStyle _style;

        public static void Show(string msg, float seconds = 2.5f)
        {
            if (string.IsNullOrEmpty(msg)) return;
            _text = msg;
            _until = Time.realtimeSinceStartup + seconds;
            SortPlugin.Log("[Sort] " + msg);
        }

        public static void Tick()
        {
            if (_text != null && Time.realtimeSinceStartup > _until) _text = null;
        }

        public static void Draw()
        {
            if (string.IsNullOrEmpty(_text)) return;
            try
            {
                if (_bg == null)
                {
                    _bg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _bg.SetPixel(0, 0, new Color(0.06f, 0.07f, 0.10f, 0.86f));
                    _bg.Apply();
                }
                if (_style == null)
                {
                    _style = new GUIStyle(GUI.skin.label)
                    {
                        fontSize = 15,
                        alignment = TextAnchor.MiddleCenter,
                        wordWrap = false
                    };
                    _style.normal.textColor = new Color(0.96f, 0.97f, 1f);
                }

                var content = new GUIContent(_text);
                var size = _style.CalcSize(content);
                var w = Mathf.Min(Screen.width - 80f, size.x + 44f);
                var h = size.y + 20f;
                var rect = new Rect((Screen.width - w) / 2f, Screen.height - 150f, w, h);

                GUI.DrawTexture(rect, _bg);
                _style.alignment = TextAnchor.MiddleCenter;
                GUI.Label(rect, _text, _style);
            }
            catch
            {
                // 绘制失败不能影响游戏
            }
        }
    }
}
