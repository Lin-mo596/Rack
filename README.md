# Rack

常驻任务栏托盘的文件抽屉。把桌面上的快捷方式收进来，点一下托盘图标（或按热键）弹出，双击即启动。

给"桌面被几十个图标铺满"这件事一个不占地方的解法。

## 特性

- **真·实时透明** —— 抽屉背景是实时透出的桌面，不是截图快照（背后内容一动它就跟着动）
- **几乎零开销** —— 抽屉关掉时整个进程只占约 **6 MB**，闲置 CPU 为 0
- **拖进来就收纳** —— 从桌面把图标拖进抽屉，桌面立刻变干净
- **分组 + 排序** —— 分类可增删、可拖拽调序；组内图标也能拖拽排序
- **全局热键** —— 默认 `Ctrl + Alt + Space`，任何程序里都能唤出
- **支持 .lnk / .url / .exe / 文件夹**，并正确还原 Steam 游戏的图标

## 系统要求

- Windows 10 / 11
- **.NET Framework 4.6+**（Windows 自带，不需要装任何东西）

## 编译

不需要 .NET SDK、不需要 Visual Studio —— 用 Windows 自带的 `csc.exe` 就能编译：

```powershell
.\build.ps1
```

产物在 `dist\Rack.exe`（单文件，约 70 KB）。

## 使用

1. 运行 `Rack.exe`，托盘出现一个蓝色抽屉图标（可能藏在 `^` 溢出区里，拖到任务栏上即可常驻）
2. 左键点图标，或按 `Ctrl + Alt + Space` 打开抽屉
3. 点「导入…」批量勾选桌面程序，或者直接把图标拖进去
4. 右键抽屉里的图标可以换分类、放回桌面

详细说明见 [docs/使用说明.md](docs/使用说明.md)。

## 数据在哪

收纳内容就是三个普通文件夹：

```
%LOCALAPPDATA%\Rack\Dock\<分类名>\    ← 你的快捷方式原文件
%LOCALAPPDATA%\Rack\config.ini         ← 设置
%LOCALAPPDATA%\Rack\order.ini          ← 图标顺序、分类顺序
```

没有数据库，没有锁定，随时能用资源管理器直接改。

## 几个技术上的坑（留给后来人）

这个项目踩过的坑，比功能本身有意思：

**1. Win11 砍掉了窗口模糊**

`DwmExtendFrameIntoClientArea` 只能让窗口"纯透明"，Win11 早在 21H2 就移除了 Aero 的模糊合成。`DwmSetWindowAttribute` 的 `SYSTEMBACKDROP_TYPE` 对 WinForms 窗口不生效，对 WPF 也不生效。

最终方案：WPF + `ExtendFrame` + 一层半透明色纱。实时跟随，但没有模糊。要模糊只能用 WinUI 3 的 `DesktopAcrylicBackdrop`（需装 1GB SDK）。

**2. `.NET` 的 `Icon.ToBitmap()` 解不了 PNG 压缩的 ICO**

Vista 之后的 256×256 大图标普遍用 PNG 压缩存进 `.ico`，`Icon.ToBitmap()` 解出来是彩色噪点。本项目的做法是手动解析 ICO 目录，把 PNG 子图直接交给 WPF 的 `BitmapImage` 解码。

**3. `.url` 文件的地球图标**

Steam 生成的 `.url` 里虽然写了 `IconFile=`，但 Shell 一律返回默认的地球图标。得自己读那一行去加载。

**4. `Image.FromHbitmap` 会吃掉 alpha 通道**

从 Shell 拿到的 ARGB 图标，用 `Image.FromHbitmap` 读会变成不透明，透明区域全黑。必须用 `GetDIBits` 直接读像素。

**5. WinForms 自定义控件的双击事件收不到**

`MouseDoubleClick` 依赖窗口类的 `CS_DBLCLKS` 样式，自定义控件经常收不到。改成自己按系统双击时间和距离判定。

**6. `PointToScreen` 是物理像素，`Left/Top` 是逻辑单位**

高 DPI 下不除以缩放比，窗口拖起来会以 1.5 倍速度追鼠标。

## License

[MIT](LICENSE)