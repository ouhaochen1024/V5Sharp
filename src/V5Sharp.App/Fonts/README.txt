MiSans 字体（随包内嵌）
======================

字体名称：MiSans
版权方 ：小米科技（Xiaomi Inc.）与 汉仪字库 / 蒙纳字库 联合出品
版本    ：取自官方字体包 MiSans.zip（2025-01 版，otf 目录）
官方地址：https://hyperos.mi.com/font
授权    ：免费商用（含个人与企业商业用途），可随软件一起分发；
          不得单独售卖字体文件，不得修改字形后再发布。

本项目只用到三个字重：
  MiSans-Regular.otf  —— 正文
  MiSans-Medium.otf   —— 卡片标题 / 窗口标题
  MiSans-Bold.otf     —— 强调文本、按钮

为什么用 OTF 而不是 TTF：OTF 是 CFF（PostScript）轮廓，GDI 的 ClearType 不
作用于它，中文会走灰度抗锯齿。12px 的中文在 ClearType 下会出现明显彩色噪点，
实机对比反而更糊，所以这里保留灰度渲染。换 ttf 目录的版本会变成 ClearType。

加载方式见 src/V5Sharp.App/Glass/FontLoader.cs：
编译期以 EmbeddedResource 打进程序集，运行时用
AddFontMemResourceEx（GDI）+ PrivateFontCollection.AddMemoryFont（GDI+）注册。
若加载失败会自动回退到系统字体（微软雅黑 / Segoe UI），不影响使用。
