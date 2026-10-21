using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using ChillNetease.Plugin;
using HarmonyLib;
using UnityEngine;

namespace ChillNeteaseSort
{
    /// <summary>
    /// 把上游 F6 面板的「列表区」放大：行更高、字号更大、面板更宽。
    ///
    /// ── 为什么必须动 IL ─────────────────────────────────────────────
    /// 上游把行距写成了 <c>private const float LineH = 26f;</c>。
    /// C# 的 const 是**编译期**常量：它在编译上游插件时就已经被内联成 IL 里的
    /// <c>ldc.r4 26</c>，运行时去反射改这个字段是**完全无效**的（字段根本没有存储）。
    ///
    /// ── 但 26f 在上游有四种完全不同的用途 ────────────────────────────
    /// 盲目「见到 26 就改」会把搜索框和页脚一起弄坏。反汇编逐条确认后的语义表：
    ///
    ///   ldc.r4 26 紧跟 mul  →  i * LineH            （行间距乘数）  ✅ 改
    ///   ldc.r4 26 紧跟 div  →  (p.y - top) / LineH  （行命中/可见行数除数）✅ 改
    ///   ldc.r4 26 紧跟 sub  →  FooterH（页脚高）                 ❌ 不动
    ///   ldc.r4 26 → Rect::.ctor → 搜索框高 / 右上角按钮高（各 26）  ❌ 不动
    ///   ldc.r4 23          →  LineH - 3（行矩形高）              ✅ 改成 新行高 - 3
    ///
    /// 所以匹配规则是**按紧跟的那条 opcode**，而不是按固定 IL offset ——
    /// 上游更新导致指令顺序变化时会改不到（安全降级），而不是改错地方（面板崩坏）。
    ///
    /// ── 不用动 IL 的部分 ─────────────────────────────────────────────
    ///   · <c>WindowRect</c> 是普通 static 字段，<c>Tick()</c> 每帧只重算它的 x/y，
    ///     不碰 width/height → 纯反射赋值即可。
    ///     但**必须在上游 Tick() 之后**改：Tick 里
    ///     <c>WindowRect.x = (Screen.width - WindowRect.width) / 2f</c>，
    ///     先改宽度再让它算居中才对；顺序反了面板会偏。
    ///   · GUIStyle 的 fontSize 是普通属性，同样纯反射覆写。
    /// </summary>
    public static class UiScale
    {
        /// <summary>上游的行距常量（用于识别与回退）。</summary>
        public const float UpstreamLineH = 26f;

        // ---- 反射缓存 ----
        private static FieldInfo _windowRectField;
        private static bool _windowRectResolved;

        /// <summary>
        /// 需要放大字号的样式。
        ///
        /// 刻意**只含行样式**：上游的标题区高度是编译期常量 <c>HeaderH = 84f</c>，
        /// 而标题（_titleStyle 17px，y+8）与状态行（_smallStyle 12px，y+34）之间
        /// 只有 26px 间距 —— 一旦把小字也放大到 18px，状态行就会顶到标题上
        /// （实测截图确认过）。而放大 HeaderH 要连带改 84 和 142（搜索视图行区偏移）
        /// 两处编译期常量，风险远大于收益：列表本身才是用户嫌挤的地方。
        ///
        /// false = 保持上游原样。
        /// </summary>
        private static readonly (string field, bool scale)[] StyleFontSizes = new[]
        {
            ("_rowStyle", true),        // 歌单/歌曲行 ← 放大目标
            ("_selectedStyle", true),   // 选中行
            ("_headerStyle", false),    // 标题区：不动
            ("_smallStyle", false),     // 状态行 / 分组提示 / ✕ 删除按钮：不动
            ("_toastStyle", false),     // 面板底部提示：不动
            ("_titleStyle", false),     // 「网易云音乐」：不动
            ("_tabNormal", false),      // 分组标签：不动
            ("_tabSelected", false),
        };

        private static FieldInfo[] _styleFields;
        private static bool _styleFieldsResolved;
        /// <summary>上游各样式的原始字号（首次读到时留档，便于回退与排错）。</summary>
        private static readonly Dictionary<FieldInfo, int> _origFontSizes = new Dictionary<FieldInfo, int>();

        // 记录上一次真正写入的值，避免每帧重复反射赋值（Tick 每帧都跑）
        private static float _appliedW = -1f;
        private static float _appliedH = -1f;
        private static int _appliedFont = -1;

        /// <summary>是否处于放大状态（任一项偏离上游默认值即算放大）。</summary>
        public static bool Active =>
            SortPlugin.EnlargePanelUi != null && SortPlugin.EnlargePanelUi.Value &&
            RowHeight > UpstreamLineH + 0.01f;

        /// <summary>生效中的行高（transpiler 打的是常量，运行期不再变）。</summary>
        public static float RowHeight
        {
            get
            {
                var v = SortPlugin.PanelRowHeight?.Value ?? 0;
                return v > UpstreamLineH + 0.01f ? v : UpstreamLineH;
            }
        }

        /// <summary>面板目标宽度。</summary>
        public static float PanelWidth
        {
            get
            {
                var v = SortPlugin.PanelWidth?.Value ?? 0;
                return v >= 200f ? v : 440f;      // 440 = 上游默认值
            }
        }

        /// <summary>面板目标高度。</summary>
        public static float PanelHeight
        {
            get
            {
                var v = SortPlugin.PanelHeight?.Value ?? 0;
                return v >= 200f ? v : 470f;      // 470 = 上游默认值
            }
        }

        /// <summary>歌单行字号。0 = 按上游 13px。</summary>
        public static int RowFontSize
        {
            get
            {
                var v = SortPlugin.PanelRowFontSize?.Value ?? 0;
                return v >= 10 ? v : 13;
            }
        }

        // ---------------- Harmony 接入 ----------------

        /// <summary>
        /// 需要打 transpiler 的方法 → 用哪条改写规则。
        /// 绘制侧是「乘法」语义（i * LineH），命中/可见行数侧是「除法」语义（/ LineH）。
        /// 少一个就会出现「画得大但点不准 / 滚不到底」的错位。
        /// </summary>
        private static readonly (string method, string rule)[] ScaledMethods =
        {
            ("DrawPlaylists", "mul"),   // 歌单列表：26*mul / 23
            ("DrawSongs", "mul"),       // 歌曲列表：26*mul / 23
            ("DrawSearch", "mul"),      // 搜索结果：26*mul / 23（搜索框那两个 26 是 Rect 参数，不受影响）
            ("HandleClick", "div"),     // 行命中：26*div
            ("VisibleLines", "div"),    // 可见行数：26*div（另一个 26 是 FooterH，sub，不受影响）
            ("SearchVisibleLines", "div"),
        };

        public static void Install(Harmony harmony)
        {
            var newH = RowHeight;
            var newFont = RowFontSize;

            // 上游 DrawRow 里 Truncate(label, 48)：面板变宽后按比例放宽，
            // 免得歌单名在放大后更容易被截断。
            var truncateLen = Mathf.Clamp(
                Mathf.RoundToInt(48f * PanelWidth / 440f), 48, 160);

            var t = AccessTools.Method(typeof(NeteaseUi), "Truncate");
            var patchedOk = 0;
            var failed = new List<string>();

            foreach (var (name, ruleName) in ScaledMethods)
            {
                try
                {
                    var m = AccessTools.Method(typeof(NeteaseUi), name);
                    if (m == null) { failed.Add(name + "(找不到)"); continue; }

                    var rule = ruleName == "div" ? "TranspilerRows" : "Transpiler";
                    var tr = AccessTools.Method(typeof(UiScale), rule);
                    harmony.Patch(m, transpiler: new HarmonyMethod(tr));
                    patchedOk++;
                }
                catch (Exception ex) { failed.Add(name + ": " + ex.Message); }
            }

            // Truncate 的截断长度（DrawRow / DrawSearch 里调它）
            try
            {
                if (t != null && truncateLen > 48)
                {
                    var tr = AccessTools.Method(typeof(UiScale), nameof(TranspilerTruncate));
                    harmony.Patch(t, transpiler: new HarmonyMethod(tr));
                    patchedOk++;
                }
            }
            catch (Exception ex) { failed.Add("Truncate: " + ex.Message); }

            SortPlugin.Log($"[Sort] 界面放大：行高 {UpstreamLineH} → {newH}，字号 → {newFont}px，" +
                $"面板 {PanelWidth}×{PanelHeight}，截断 → {truncateLen} 字；" +
                $"transpiler 成功 {patchedOk} 个" +
                (failed.Count > 0 ? "，失败：" + string.Join(" | ", failed) : ""));

        }

        /// <summary>
        /// 行矩形高度。上游硬编码 <c>LineH - 3</c>（编译期算成 23），
        /// 行高变了它必须跟着变，否则行与行之间会露出越来越大的缝。
        /// </summary>
        public static float RowRectHeight => RowHeight - 3f;

        /// <summary>
        /// 绘制侧改写：<c>i * LineH</c> 的乘数、<c>LineH - 3</c> 的行矩形高。
        /// </summary>
        private static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var newH = RowHeight;
            var list = new List<CodeInstruction>(instructions);
            var mul = OpCodes.Mul.Value;
            var changed = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];

                // 只认 ldc.r4 单精度浮点常量
                if (ins.opcode != OpCodes.Ldc_R4) continue;
                if (!(ins.operand is float f)) continue;

                // ① ldc.r4 23 → LineH - 3（行矩形高）
                if (Math.Abs(f - 23f) < 0.01f)
                {
                    ins.operand = RowRectHeight;
                    changed++;
                    continue;
                }

                // ② ldc.r4 26 → 只有紧跟 mul（i * LineH）才是行间距
                if (Math.Abs(f - UpstreamLineH) < 0.01f && i + 1 < list.Count &&
                    list[i + 1].opcode.Value == mul)
                {
                    ins.operand = newH;
                    changed++;
                }
            }

            SortPlugin.LogOnce("[Sort] 界面放大 transpiler：" + list.Count + " 条指令，改写 " + changed + " 处");
            return list;
        }

        /// <summary>
        /// 命中 / 可见行数侧改写：<c>(p.y - top) / LineH</c> 的除数。
        /// 这里的 26 紧跟的是 <c>div</c>；同一方法里紧跟 <c>sub</c> 的那个是 FooterH，不碰。
        /// </summary>
        private static IEnumerable<CodeInstruction> TranspilerDiv(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var newH = RowHeight;
            var list = new List<CodeInstruction>(instructions);
            var div = OpCodes.Div.Value;
            var changed = 0;

            for (int i = 0; i < list.Count; i++)
            {
                var ins = list[i];
                if (ins.opcode != OpCodes.Ldc_R4) continue;
                if (!(ins.operand is float f)) continue;
                if (Math.Abs(f - UpstreamLineH) > 0.01f) continue;
                if (i + 1 >= list.Count) continue;
                if (list[i + 1].opcode.Value != div) continue;
                ins.operand = newH;
                changed++;
            }

            SortPlugin.LogOnce("[Sort] 界面放大 transpiler(div)：" + list.Count + " 条指令，改写 " + changed + " 处");
            return list;
        }

        /// <summary>行高相关的除法（命中判定 / 可见行数）走这一条。</summary>
        private static IEnumerable<CodeInstruction> TranspilerRows(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
            => TranspilerDiv(instructions, il);

        /// <summary>Truncate(label, 48) → 更长的截断长度。</summary>
        private static IEnumerable<CodeInstruction> TranspilerTruncate(
            IEnumerable<CodeInstruction> instructions, ILGenerator il)
        {
            var len = Mathf.Clamp(Mathf.RoundToInt(48f * PanelWidth / 440f), 48, 160);
            var list = new List<CodeInstruction>(instructions);
            foreach (var ins in list)
            {
                if (ins.opcode != OpCodes.Ldc_I4) continue;
                if (ins.operand is int i && i == 48) ins.operand = len;
            }
            return list;
        }

        // ---------------- 每帧：面板宽高 + 字号 ----------------

        /// <summary>
        /// 由 <c>NeteaseUi.Tick</c> 的 postfix 调用（必须在上游 Tick 之后：
        /// 它用 WindowRect.width 算居中 x，先改宽会让面板偏出屏幕中心）。
        /// </summary>
        public static void ApplyPerFrame()
        {
            if (!Active) return;
            try
            {
                var rect = GetWindowRect();
                if (rect == null) return;
                var r = (Rect)rect.GetValue(null);

                var wantW = Mathf.Min(PanelWidth, Screen.width - 40f);
                var wantH = Mathf.Min(PanelHeight, Screen.height - 60f);

                if (Math.Abs(r.width - wantW) > 0.5f || Math.Abs(r.height - wantH) > 0.5f)
                {
                    // 上游 Tick 已算好 x/y，这里只改宽高 —— 但 x 是按旧宽算的，要重新居中
                    var w = wantW; var h = wantH;
                    var x = (Screen.width - w) / 2f;
                    var y = 10f;
                    rect.SetValue(null, new Rect(x, y, w, h));
                    _appliedW = w; _appliedH = h;
                }

                ApplyFontSizes();
            }
            catch (Exception ex)
            {
                SortPlugin.WarnOnce("[Sort] 界面放大每帧应用失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 覆写上游 GUIStyle 的字号。
        /// 上游的 <c>EnsureStyles()</c> 只在首次调用时创建样式（有 _stylesReady 一次性标志），
        /// 所以我们**不能**去改它的创建逻辑，只能等样式存在后按字段名覆写属性 ——
        /// GUIStyle.fontSize 是普通属性，改了立刻生效。
        /// </summary>
        private static void ApplyFontSizes()
        {
            var want = RowFontSize;
            if (want == _appliedFont) return;      // 没变就不折腾
            if (!ResolveStyleFields()) return;

            var any = false;
            for (int i = 0; i < _styleFields.Length; i++)
            {
                var f = _styleFields[i];
                if (f == null) continue;
                GUIStyle st;
                try
                {
                    st = f.GetValue(null) as GUIStyle;      // 上游还没 EnsureStyles 时为 null
                }
                catch { continue; }
                if (st == null) continue;

                // 原字号留档：只有标记为「要放大」的样式才动，其余保持上游原样
                if (!_origFontSizes.ContainsKey(f)) _origFontSizes[f] = st.fontSize;
                var orig = _origFontSizes[f];
                var target = StyleFontSizes[i].scale ? want : orig;

                try
                {
                    if (st.fontSize == target) continue;
                    st.fontSize = target;
                    any = true;
                }
                catch { /* 单个样式失败不影响其它 */ }
            }

            _appliedFont = want;
            if (any)
            {
                int o1 = 0;
                if (_styleFields.Length > 0 && _styleFields[0] != null)
                    _origFontSizes.TryGetValue(_styleFields[0], out o1);
                SortPlugin.LogOnce($"[Sort] 界面放大：歌单行字号 {o1} → {want}px（标题区保持上游原样）");
            }
        }

        private static FieldInfo GetWindowRect()
        {
            if (!_windowRectResolved)
            {
                _windowRectResolved = true;
                try
                {
                    _windowRectField = typeof(NeteaseUi).GetField("WindowRect",
                        BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public);
                }
                catch { _windowRectField = null; }
                if (_windowRectField == null)
                    SortPlugin.Warn("[Sort] 界面放大：找不到上游 NeteaseUi.WindowRect，放大不生效");
            }
            return _windowRectField;
        }

        private static bool ResolveStyleFields()
        {
            if (_styleFieldsResolved) return _styleFields != null;
            _styleFieldsResolved = true;
            try
            {
                const BindingFlags F = BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Public;
                _styleFields = new FieldInfo[StyleFontSizes.Length];
                for (int i = 0; i < StyleFontSizes.Length; i++)
                {
                    _styleFields[i] = typeof(NeteaseUi).GetField(StyleFontSizes[i].field, F);
                }
                return true;
            }
            catch { _styleFields = null; return false; }
        }
    }
}