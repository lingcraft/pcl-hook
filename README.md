# PCL2 功能恢复 HOOK 补丁

以**运行时 Hook** 的方式恢复 Plain Craft Launcher 2（PCL2）被编译期裁剪的 Minecraft 下载与整合包安装功能。

不修改 `Plain Craft Launcher 2.exe` 的任何一个字节——通过 `AppDomainManager` + 原生入口 Hook，在启动时把补丁打进进程内存。删除文件即可完全还原。

## 功能

| # | 恢复的功能 | 目标方法 | 适用版本 |
|---|---|---|---|
| 1 | 下载页的 **MC 版本列表**（正式版 / 快照 / 远古版 / 愚人节版） | `PageDownloadInstall.LoaderInit` | 2.99.0.0 / 2.99.0.1 |
| 2 | **整合包安装分发**（CurseForge / HMCL / MMC / MCBBS / Modrinth / 启动器包 / 普通压缩包） | `ModModpack.ModpackInstall(File, InstanceName, Logo)` | 2.99.0.0 / 2.99.0.1 |
| 3 | **「安装整合包」入口**（文件选择对话框） | `ModModpack.ModpackInstall()` | 2.99.0.0 / 2.99.0.1 |
| 4 | 版本设置 → 修改页的 **MC 版本卡片**（不再被硬编码隐藏） | `PageDownloadModify.LoadInstanceFinished` | 2.99.0.0 / 2.99.0.1 |
| 5 | 资源详情页整合包版本的 **「安装」按钮**（原来是「保存」） | `ResourceVersion.ToListItem` | 2.99.0.0 / 2.99.0.1 |
| 6 | 下载页左侧的 **「原版游戏」入口** | `PageDownloadLeft.InitializeComponent` | 仅 2.99.0.1（旧版无此改动） |
| 7 | 启动页无实例时的 **「下载游戏」按钮** | `PageLaunchLeft.RefreshButtonsUI` | 仅 2.99.0.1（旧版无此改动） |

补充说明：

- 拖拽整合包安装（`FormMain.FileDrag`）无需单独处理，它会走 3 参 `ModpackInstall`，补丁 2 生效后自动可用
- 资源页按钮只在 `ResourceVersion.ResourceType == ModPack` 时改写，Mod / 资源包 / 光影仍保持「保存」语义
- 第 6/7 项仅作用于 2.99.0.1：旧版的相同状态来自用户的「隐藏下载页」设置，不应被覆盖

## 用法

### 安装

1. 关闭 PCL
2. 把以下 **2 个文件**放进 `Plain Craft Launcher 2.exe` 所在目录：

   ```
   PCL.dll
   Plain Craft Launcher 2.exe.config
   ```

3. 启动 PCL 即可，无需修改启动器程序

`.config` 是 CLR 的注入入口，内容固定：

```xml
<?xml version="1.0" encoding="utf-8" ?>
<configuration>
  <runtime>
    <appDomainManagerAssembly value="PCL, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" />
    <appDomainManagerType value="PCL.HOOK" />
  </runtime>
</configuration>
```

> 如果你的启动器不叫 `Plain Craft Launcher 2.exe`，`.config` 必须改成 `<你的exe全名>.exe.config`，否则不会生效。

### 卸载

删除 `PCL.dll` 和 `Plain Craft Launcher 2.exe.config` 即可，PCL 立即回到官方原版行为。

### 验证

启动后查看 `PCL\hook.log`（与 exe 同目录的 PCL 文件夹内），正常应类似：

```
[2026-10-25 21:27:10] pcl assembly: Plain Craft Launcher 2 2.99.0.1
[2026-10-25 21:27:11] waiting for dependencies: ok
[2026-10-25 21:27:11] hook ok: LoaderInit
[2026-10-25 21:27:11] hook ok: ModpackInstall3
[2026-10-25 21:27:11] hook ok: ModpackInstall0
[2026-10-25 21:27:11] hook ok: LoadInstanceFinished
[2026-10-25 21:27:11] hook ok: ToListItem
[2026-10-25 21:27:11] hook ok: PageDownloadLeftCtor
[2026-10-25 21:27:11] hook ok: PageLaunchRefresh
[2026-10-25 21:27:11] launch: no-instance download entry restored
[2026-10-25 21:27:14] nav restored: 原版游戏 entry visible
```

- 日志每次启动重建（检测到已有 PCL 实例在运行时不重建，避免二次启动清空正在写的日志）
- `hook ok` = 补丁已挂上
- `hook skip` = 目标方法在当前版本不存在（版本结构变化），其余补丁照常工作，PCL 行为退回官方
- `hook fail` = 挂载异常（会带异常类型与消息）

## 兼容性

| PCL 版本 | 状态 |
|---|---|
| 2.99.0.0 | ✅ 已适配（第 6/7 项自动跳过） |
| 2.99.0.1 | ✅ 已适配（全部 7 项） |
| 其它版本 | ⚠️ 未验证。方法名保持一致时仍会尝试挂载，缺失的项记 `hook skip` |

补丁按「类型名 + 方法名 + IL 结构」定位，不使用固定偏移，因此版本号小幅变化通常不影响。

## 构建

需要 .NET SDK（目标框架 `net48`，DotNetDetour 源码已内联，无 NuGet 依赖）：

```bash
dotnet build PCL/PCL.csproj -c Release
```

产物：`PCL/bin/Release/net48/PCL.dll`，连同上面的 `.config` 一起复制到 PCL 目录即可。

## 原理

PCL 2.99 用 `Public Const RestrictMcDownload As Boolean = True` 在编译期关闭下载。VB 编译器把常量内联后，相关分支被当作死代码整段删除——**这些逻辑根本不在 exe 的 IL 里**，所以不存在"改个开关"这种解法。

本项目的处理方式：

1. `PCL.HOOK` 继承 `AppDomainManager`，在 PCL 的 `Main` 执行前被 CLR 实例化
2. 检测到 PCL 程序集后，轮询 `Assembly.Load("MeloongCore")` 等待其内嵌依赖注册完成
3. 用 DotNetDetour 对 7 个目标方法做**原生入口 Hook**（不重编译方法体）
   - 需要保留原逻辑的地方（如 `LoadInstanceFinished`、`ToListItem`）通过 `proxy` trampoline 先执行原方法，再追加/覆盖行为
   - 方法体无法被重编译的 `ModpackInstall(3)`（全分支 throw、无 `ret`、`finally` 边界延伸到方法末尾）也能正常挂载
4. 每个 Hook 独立重试，失败不影响其它项

这样做的结果：**exe 完全不动**，PCL 自动更新后补丁依然生效；若新版本改了方法结构，对应项只是跳过。

## 目录结构

```
PCL/
├─ PCL.slnx                解决方案
├─ README.md
├─ LICENSE
└─ PCL/
   ├─ HOOK.cs              AppDomainManager 入口与全部补丁逻辑
   ├─ PCL.csproj           项目文件（net48）
   └─ DotNetDetour/        内联的 DotNetDetour 源码（含 32/64 位 detour 实现）
```

## 调试

| 环境变量 | 作用 |
|---|---|
| `PCL_HOOK_PROBE=1` | 用一个不存在的路径调用 `ModpackInstall`，验证入口是否被接管 |
| `PCL_HOOK_PROBE=2` | 直接调用 `ResourceVersion.ToListItem`，验证 trampoline 能回到原方法 |
| `PCL_HOOK_PROBE=3` | 在 UI 线程构造 `PageDownloadModify` 并调用 `LoaderInit`，诊断修改页链路 |
| `PCL_HOOK_PACK=<zip路径>` | 配合 `PROBE=1`，用真实整合包验证类型识别与分发 |

## 免责声明

- 本项目为第三方产物，与 PCL 官方（龙腾猫跃 / Meloong）无任何关联
- 不包含、不分发任何 PCL 的二进制文件，仅在用户本机修改进程内存
- 请支持原作者；PCL 移除该功能是开发者的主动决定，使用本项目的后果由使用者自行承担
