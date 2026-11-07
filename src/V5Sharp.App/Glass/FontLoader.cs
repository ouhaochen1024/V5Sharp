using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace V5Sharp.App;

/// <summary>
/// 内嵌字体加载器（MiSans）。
///
/// 字体随程序集一起分发，不依赖用户系统是否安装：
/// 用的是 OTF（CFF 轮廓）。GDI 的 ClearType 不作用于 CFF 轮廓，中文因此是灰度抗锯齿；
/// 这是刻意保留的观感 —— 试过换成 TTF 走 ClearType，12px 中文会出彩色噪点，反而更糊。
///
/// · <c>AddFontMemResourceEx</c> 让 GDI 也能看到它（TextBox / 原生控件的 TextRenderer 走 GDI）；
/// · <c>PrivateFontCollection.AddMemoryFont</c> 供 GDI+ 自绘使用。
/// 内存块在进程生命周期内保持有效（字体表被引用，不能释放）。
/// 任何一步失败都安静降级到系统字体。
/// </summary>
internal static class FontLoader
{
    private const string FamilyName = "MiSans";

    /// <summary>随包字重：Regular / Medium / Bold</summary>
    private static readonly string[] WeightFiles = { "MiSans-Regular.otf", "MiSans-Medium.otf", "MiSans-Bold.otf" };

    // 永不释放：GDI / GDI+ 会长期引用这块内存
    private static readonly List<IntPtr> _blocks = new();
    private static readonly List<IntPtr> _gdiHandles = new();

    private static bool _loaded;
    private static PrivateFontCollection? _collection;

    /// <summary>常规 + 粗体（同一字体族）</summary>
    public static FontFamily? Regular { get; private set; }

    /// <summary>中等字重，单独成像一个族（"MiSans Medium"）</summary>
    public static FontFamily? Medium { get; private set; }

    /// <summary>是否成功用上了内嵌的 MiSans</summary>
    public static bool Available => Regular is not null;

    public static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;

        _collection = new PrivateFontCollection();
        var asm = typeof(FontLoader).Assembly;
        string[] names = asm.GetManifestResourceNames();

        foreach (string file in WeightFiles)
        {
            // 单个字重坏掉不影响其余字重：三个 OTF 各 ~6MB，任何一个解析失败都不该让整套字体失效
            try
            {
                string? res = names.FirstOrDefault(n =>
                    n.EndsWith(file, StringComparison.OrdinalIgnoreCase));
                if (res is null) continue;

                byte[] data;
                using (Stream? s = asm.GetManifestResourceStream(res))
                {
                    if (s is null) continue;
                    using var ms = new MemoryStream((int)s.Length);
                    s.CopyTo(ms);
                    data = ms.ToArray();
                }
                if (data.Length == 0) continue;

                IntPtr block = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, block, data.Length);
                _blocks.Add(block);

                // GDI 侧（原生控件 TextRenderer）
                IntPtr h = AddFontMemResourceEx(block, (uint)data.Length, IntPtr.Zero, out uint count);
                if (h != IntPtr.Zero && count > 0) _gdiHandles.Add(h);

                // GDI+ 侧（Graphics.DrawString 自绘）
                _collection.AddMemoryFont(block, data.Length);
            }
            catch { /* 这个字重跳过，继续下一个 */ }
        }

        ResolveFamilies(_collection);
    }

    /// <summary>
    /// 从私有字体集合里挑出"常规族"和"Medium 族"。
    /// 正常情况是 "MiSans"（Regular+Bold 合族）与 "MiSans Medium"；
    /// 某些构建会把字重写进族名（"MiSans Regular"），这里一并兜住。
    /// </summary>
    private static void ResolveFamilies(PrivateFontCollection collection)
    {
        FontFamily? exact = null, medium = null, first = null;

        try
        {
            foreach (FontFamily fam in collection.Families)
            {
                first ??= fam;
                if (string.Equals(fam.Name, FamilyName, StringComparison.OrdinalIgnoreCase))
                    exact = fam;
                else if (fam.Name.StartsWith(FamilyName, StringComparison.OrdinalIgnoreCase) &&
                         fam.Name.Contains("Medium", StringComparison.OrdinalIgnoreCase) && medium is null)
                    medium = fam;
            }
        }
        catch { /* 集合已损坏，按"无字体"处理 */ }

        Regular = exact ?? medium ?? first;
        Medium = ReferenceEquals(Regular, medium) ? null : medium;
    }

    /// <summary>资源清单（调试用：确认字体确实被打进了程序集）</summary>
    public static string Describe()
    {
        EnsureLoaded();
        if (Regular is null) return "MiSans: 未加载（回退系统字体）";
        string m = Medium is null ? "无" : Medium.Name;
        return $"MiSans: 已加载 family=[{Regular.Name}] medium=[{m}] gdi={_gdiHandles.Count}";
    }

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddFontMemResourceEx(
        IntPtr pbFont, uint cbFont, IntPtr pdv, out uint pcFonts);
}
