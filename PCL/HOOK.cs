using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Linq.Expressions;
using LExpr = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Threading;
using DotNetDetour;
using Microsoft.Win32;
using System.Windows;

namespace PCL
{
    /// <summary>
    /// 纯 DotNetDetour 实现（单文件自包含，无第三方 dll 依赖）。
    /// 补丁点：
    ///  1. PageDownloadInstall.LoaderInit      -> 真 DlClientListLoader
    ///  2. ModModpack.ModpackInstall(3 参)     -> 类型分发
    ///  3. ModModpack.ModpackInstall()         -> 文件选择入口
    ///  4. PageDownloadModify.LoadInstanceFinished -> CardMinecraft 可见
    ///  5. ResourceVersion.ToListItem          -> 修正 Install/Save 按钮接线
    /// </summary>
    public class HOOK : AppDomainManager
    {
        static Assembly _asm;
        static bool _started;
        static Type _iconHandler;
        static Type _modpackType, _modifyType;
        static bool _tliLogged;
        static bool _launchLogged;

        const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        const BindingFlags I = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        public override void InitializeNewDomain(AppDomainSetup setup)
        {
            try { AppDomain.CurrentDomain.AssemblyLoad += OnLoad; } catch { }
            base.InitializeNewDomain(setup);
        }

        static void OnLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                if (_started) return;
                var asm = e.LoadedAssembly;
                var page = asm.GetType("PCL.PageDownloadInstall", false);
                if (page == null) return;
                _asm = asm;
                _started = true;
                new Thread(() => { try { ApplyAll(page); } catch (Exception ex) { Log("applyall ex: " + ex); } })
                { IsBackground = true }.Start();
            }
            catch { }
        }

        static void ApplyAll(Type page)
        {
            ResetLog();
            Log("pcl assembly: " + _asm.GetName().Name + " " + _asm.GetName().Version);
            Log("waiting for dependencies: " + (WaitDeps() ? "ok" : "timeout"));
            _modpackType = FindType(_asm, "PCL.ModModpack");
            _modifyType = FindType(_asm, "PCL.PageDownloadModify");

            // 1) LoaderInit
            Try("LoaderInit", () =>
            {
                var origin = page.GetMethod("LoaderInit", I);
                Patch(origin, typeof(HOOK).GetMethod("LoaderRepl", F), null);
            });

            // 2) ModpackInstall(3)
            Try("ModpackInstall3", () =>
            {
                var origin = _modpackType.GetMethods(F)
                    .First(m => m.Name == "ModpackInstall" && m.GetParameters().Length == 3);
                Patch(origin, typeof(HOOK).GetMethod("MP3Repl", F), null);
            });

            // 3) ModpackInstall()
            Try("ModpackInstall0", () =>
            {
                var origin = _modpackType.GetMethods(F)
                    .First(m => m.Name == "ModpackInstall" && m.GetParameters().Length == 0);
                Patch(origin, typeof(HOOK).GetMethod("MP0Repl", F), null);
            });

            // 4) LoadInstanceFinished
            Try("LoadInstanceFinished", () =>
            {
                var origin = _modifyType.GetMethod("LoadInstanceFinished", I);
                Patch(origin, typeof(HOOK).GetMethod("LIFRepl", F), typeof(HOOK).GetMethod("LIFProxy", F));
            });

            // 5) ResourceVersion.ToListItem
            Try("ToListItem", () =>
            {
                var rv = FindType(_asm, "PCL.ResourceVersion");
                var origin = rv.GetMethods(I).First(m => m.Name == "ToListItem");
                _iconHandler = origin.GetParameters()[1].ParameterType;
                Patch(origin, typeof(HOOK).GetMethod("TLIRepl", F), typeof(HOOK).GetMethod("TLIProxy", F));
            });

            // 6) 下载页左侧「原版游戏」入口（2.99.0.1 起在 ctor 里被折叠）
            Try("PageDownloadLeftCtor", () =>
            {
                var t = FindType(_asm, "PCL.PageDownloadLeft");
                if (t == null) throw new HookNotFound("PCL.PageDownloadLeft not found");
                // DotNetDetour 只能挂 MethodInfo，构造函数挂不了；
                // 改为挂 ctor 里调用的 InitializeComponent，再排队到 ctor 结束后修正 4 个状态
                var init = t.GetMethod("InitializeComponent", I);
                if (init == null) throw new HookNotFound("PageDownloadLeft.InitializeComponent not found");
                Patch(init, typeof(HOOK).GetMethod("NavInitRepl", F), typeof(HOOK).GetMethod("NavInitProxy", F));
            });

            // 7) 启动页：无实例时恢复「下载游戏」入口（2.99.0.1 起被改成禁用态）
            Try("PageLaunchRefresh", () =>
            {
                var t = FindType(_asm, "PCL.PageLaunchLeft");
                if (t == null) throw new HookNotFound("PCL.PageLaunchLeft not found");
                var m = t.GetMethod("RefreshButtonsUI", I);
                if (m == null) throw new HookNotFound("RefreshButtonsUI not found");
                Patch(m, typeof(HOOK).GetMethod("LaunchRefreshRepl", F), typeof(HOOK).GetMethod("LaunchRefreshProxy", F));
            });

            PackTestIfRequested();
            ProbeIfRequested();
        }

        // 用真实整合包验证类型识别（只检测，不触发安装）
        static void PackTestIfRequested()
        {
            try
            {
                var pack = Environment.GetEnvironmentVariable("PCL_HOOK_PACK");
                if (string.IsNullOrEmpty(pack) || !File.Exists(pack)) return;
                var zip = OpenZip(pack);
                if (zip == null) { Log("packtest: open failed"); return; }
                string bf;
                int code = DetectPackType(zip, out bf);
                string name;
                switch (code)
                {
                    case 0: name = "InstallPackCurseForge"; break;
                    case 1: name = "InstallPackHMCL"; break;
                    case 2: name = "InstallPackMMC"; break;
                    case 3: name = "InstallPackMCBBS"; break;
                    case 4: name = "InstallPackModrinth"; break;
                    case 9: name = "InstallPackLauncherPack"; break;
                    default: name = "InstallPackCompress"; break;
                }
                Log($"packtest: {Path.GetFileName(pack)} type={code} base='{bf}' -> {name}");
            }
            catch (Exception ex) { Log("packtest ex: " + ex.Message); }
        }

        static bool WaitDeps(int attempts = 30, int delayMs = 300)
        {
            for (int i = 0; i < attempts; i++)
            {
                try { Assembly.Load("MeloongCore"); return true; } catch { Thread.Sleep(delayMs); }
            }
            return false;
        }

        // JIT 需要依赖程序集，依赖未就绪时 PrepareMethod 会抛 FileNotFoundException，因此重试；
        // 但方法本身不存在（版本更新后签名变化）时立即失败，不空转
        static void Try(string name, Action a, int attempts = 25, int delayMs = 300)
        {
            for (int i = 0; i < attempts; i++)
            {
                try { a(); Log("hook ok: " + name); return; }
                catch (HookNotFound ex)
                {
                    Log($"hook skip: {name} -> {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    if (i == attempts - 1)
                        Log($"hook fail: {name} -> {ex.GetType().Name}: {ex.Message}");
                    Thread.Sleep(delayMs);
                }
            }
        }

        sealed class HookNotFound : Exception
        {
            public HookNotFound(string msg) : base(msg) { }
        }

        static void Patch(MethodInfo origin, MethodInfo repl, MethodInfo proxy)
        {
            if (origin == null) throw new HookNotFound("method not found");
            var engine = DetourFactory.CreateDetourEngine();
            engine.Patch(origin, repl, proxy);
        }

        // ───────── 1) LoaderInit ─────────
        static void LoaderRepl(object inst)
        {
            try
            {
                var t = inst.GetType();
                t.GetMethod("ControlInit", I).Invoke(inst, null);

                var loadMinecraft = t.GetProperty("LoadMinecraft", I).GetValue(inst);
                var panLoad = t.GetProperty("PanLoad", I).GetValue(inst);
                var panAllBack = t.GetProperty("PanAllBack", I).GetValue(inst);

                var td = t.Assembly.GetType("PCL.ModDownload", true);
                var loader = td.GetField("DlClientListLoader", F).GetValue(null);
                if (loader == null) return;

                var onFinish = t.GetMethod("LoadMinecraft_OnFinish", I);
                var loaderType = loader.GetType();
                var resultType = loaderType.GetGenericArguments()[1];
                var actionType = typeof(Action<>).MakeGenericType(loaderType);

                var dm = new DynamicMethod("__finish", typeof(void),
                    new[] { typeof(object), loaderType }, t.Module, true);
                var il = dm.GetILGenerator();
                il.Emit(OpCodes.Ldarg_0);
                il.Emit(OpCodes.Castclass, t);
                il.Emit(OpCodes.Call, onFinish);
                il.Emit(OpCodes.Ret);
                var del = dm.CreateDelegate(actionType, inst);

                var pli = t.BaseType.GetMethods(I).First(x => x.Name == "PageLoaderInit")
                    .MakeGenericMethod(typeof(string), resultType);
                pli.Invoke(inst, new object[] { loadMinecraft, panLoad, panAllBack, null, loader, del, null, true });
            }
            catch { }
        }

        // ───────── 2) ModpackInstall(3) ─────────
        static object MP3Repl(string File, string InstanceName, string Logo) => DispatchCore(File, InstanceName, Logo);

        static object DispatchCore(string File, string InstanceName, string Logo)
        {
            try
            {
                var zip = OpenZip(File);
                if (zip == null) return null;

                string baseFolder;
                int code = DetectPackType(zip, out baseFolder);
                string name;
                switch (code)
                {
                    case 0: name = "InstallPackCurseForge"; break;
                    case 1: name = "InstallPackHMCL"; break;
                    case 2: name = "InstallPackMMC"; break;
                    case 3: name = "InstallPackMCBBS"; break;
                    case 4: name = "InstallPackModrinth"; break;
                    case 9: name = "InstallPackLauncherPack"; break;
                    default: name = "InstallPackCompress"; break;
                }
                Log($"dispatch: {Path.GetFileName(File)} type={code} base='{baseFolder}' -> {name}");

                var mi = _modpackType.GetMethods(F).FirstOrDefault(m => m.Name == name);
                if (mi == null) return null;

                var ps = mi.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    switch (ps[i].Name)
                    {
                        case "FileAddress": args[i] = File; break;
                        case "Archive": args[i] = zip; break;
                        case "ArchiveBaseFolder": args[i] = baseFolder; break;
                        case "InstanceName": args[i] = InstanceName; break;
                        case "Logo": args[i] = Logo; break;
                        default: args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null; break;
                    }
                }
                return mi.Invoke(null, args);   // InstallPack* 内部自行 Start
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Log("dispatch ex: " + inner);
                Hint("整合包安装失败：" + (inner.Message ?? "未知错误"));
                return null;
            }
        }

        // ───────── 3) ModpackInstall() ─────────
        static void MP0Repl()
        {
            try
            {
                var dlg = new OpenFileDialog
                {
                    Title = "选择整合包文件",
                    Filter = "整合包文件|*.zip;*.mrpack;*.rar|所有文件|*.*"
                };
                if (dlg.ShowDialog() != true) return;
                var file = dlg.FileName;
                if (string.IsNullOrEmpty(file)) return;
                Log("dialog selected: " + file);

                var mi3 = _modpackType.GetMethods(F)
                    .FirstOrDefault(m => m.Name == "ModpackInstall" && m.GetParameters().Length == 3);
                if (mi3 == null) return;
                new Thread(() =>
                {
                    try { mi3.Invoke(null, new object[] { file, null, null }); }
                    catch (Exception ex)
                    {
                        var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                        Hint("整合包安装失败：" + (inner.Message ?? "未知错误"));
                    }
                })
                { IsBackground = true }.Start();
            }
            catch { }
        }

        // ───────── 4) LoadInstanceFinished ─────────
        // 必须保留原方法逻辑（整页 UI 初始化都在里面），否则“版本设置-修改”右侧空白
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void LIFProxy(object inst, object task)
        {
            Log("LIFProxy should have been redirected");
        }

        static void LIFRepl(object inst, object task)
        {
            try { LIFProxy(inst, task); Log("modify page: original method ok"); }
            catch (Exception ex) { Log("lif orig ex: " + ex.Message); }

            try
            {
                if (inst == null) return;
                var p = inst.GetType().GetProperty("CardMinecraft", I);
                var card = p?.GetValue(inst, null) as UIElement;
                if (card != null) card.Visibility = Visibility.Visible;
            }
            catch { }
        }

        // ───────── 5) ToListItem：修正 Install/Save 接线（仅整合包） ─────────
        [MethodImpl(MethodImplOptions.NoInlining)]
        static object TLIProxy(object self, object h1, object h2, bool bad)
        {
            Log("TLIProxy should have been redirected");
            return null;
        }

        static object TLIRepl(object self, object h1, object h2, bool bad)
        {
            try
            {
                var d = h1 as Delegate;
                // 只有整合包版本文档才把"保存"改成"安装"，其它资源类型保持原样
                bool isModPack = false;
                try
                {
                    var rtField = self?.GetType().GetField("ResourceType", I);
                    var rt = rtField?.GetValue(self);
                    isModPack = rt != null && Convert.ToInt32(rt) == 2;   // PCLCS.ResourceTypes.ModPack
                }
                catch { }

                if (isModPack && h2 == null && d != null && d.Target != null && d.Method != null && d.Method.Name == "Save_Click")
                {
                    var target = d.Target;
                    var tp = target.GetType();
                    var install = tp.GetMethods(I).FirstOrDefault(m => m.Name == "Install_Click");
                    var save = tp.GetMethods(I).FirstOrDefault(m => m.Name == "Save_Click");
                    if (install != null && save != null && _iconHandler != null)
                    {
                        var newMain = MakeHandler(d.GetType(), target, install);
                        var newIcon = MakeHandler(_iconHandler, target, save);
                        if (newMain != null && newIcon != null)
                        {
                            h1 = newMain;
                            h2 = newIcon;
                            if (!_tliLogged)
                            {
                                _tliLogged = true;
                                Log("ToListItem rewired: install + save");
                            }
                        }
                    }
                }
            }
            catch (Exception ex) { Log("tli fix ex: " + ex.Message); }

            return TLIProxy(self, h1, h2, bad);
        }

        static Delegate MakeHandler(Type handlerType, object target, MethodInfo method)
        {
            var invoke = handlerType.GetMethod("Invoke");
            var sp = invoke.GetParameters();
            var pars = sp.Select(p => LExpr.Parameter(p.ParameterType, p.Name)).ToArray();
            var mp = method.GetParameters();
            if (mp.Length != pars.Length) return null;
            var args = new LExpr[mp.Length];
            for (int i = 0; i < mp.Length; i++)
                args[i] = LExpr.Convert(pars[i], mp[i].ParameterType);
            var call = LExpr.Call(
                method.IsStatic ? null : LExpr.Constant(target, method.DeclaringType), method, args);
            return LExpr.Lambda(handlerType, call, pars).Compile();
        }

        // ───────── 6) 下载页导航：恢复「原版游戏」入口 ─────────
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void NavInitProxy(object self)
        {
            Log("nav init proxy not redirected");
        }

        static void NavInitRepl(object self)
        {
            try { NavInitProxy(self); }
            catch (Exception ex) { Log("nav init orig ex: " + ex.Message); return; }

            // InitializeComponent 在 ctor 中间被调用，排队到 ctor 结束后再改状态
            try
            {
                var app = Application.Current;
                if (app == null) { FixNav(self); return; }
                app.Dispatcher.BeginInvoke(new Action(() =>
                {
                    try { FixNav(self); }
                    catch (Exception ex) { Log("nav fix ex: " + ex.Message); }
                }));
            }
            catch (Exception ex) { Log("nav schedule ex: " + ex.Message); }
        }

        static void FixNav(object self)
        {
            try
            {
                var t = self.GetType();

                var itemInstall = t.GetProperty("ItemInstall", I)?.GetValue(self, null) as UIElement;
                if (itemInstall != null) itemInstall.Visibility = Visibility.Visible;

                var labResource = t.GetProperty("LabResource", I)?.GetValue(self, null) as UIElement;
                if (labResource != null) labResource.Visibility = Visibility.Visible;

                // 不能只改 PageID：PageChange 里有 “PageID == Id 就 return” 的短路，
                // 只改字段会导致点了「原版游戏」也不切换。这里直接走 PageChange(0)。
                var change = t.GetMethod("PageChange", I);
                if (change != null)
                {
                    var pt = change.GetParameters()[0].ParameterType;
                    change.Invoke(self, new[] { Enum.ToObject(pt, 0) });
                }

                var itemMod = t.GetProperty("ItemMod", I)?.GetValue(self, null);
                if (itemMod != null)
                {
                    var checkedProp = itemMod.GetType().GetProperty("Checked", I);
                    if (checkedProp != null && checkedProp.CanWrite && checkedProp.PropertyType == typeof(bool))
                        checkedProp.SetValue(itemMod, false, null);
                }

                Log("nav restored: 原版游戏 entry visible");
            }
            catch (Exception ex) { Log("nav fix ex: " + ex.Message); }
        }

        // ───────── 7) 启动页：无实例时恢复「下载游戏」 ─────────
        [MethodImpl(MethodImplOptions.NoInlining)]
        static void LaunchRefreshProxy(object self)
        {
            Log("launch refresh proxy not redirected");
        }

        static void LaunchRefreshRepl(object self)
        {
            try { LaunchRefreshProxy(self); }
            catch (Exception ex) { Log("launch refresh orig ex: " + ex.Message); return; }

            try
            {
                // 2.99.0.0 的 state 1 来自「隐藏下载页」设置，不能覆盖；只有 2.99.0.1+ 才无条件下调
                if (_asm.GetName().Version < new Version(2, 99, 0, 1)) return;

                var t = self.GetType();
                var stateField = t.GetField("BtnLaunchState", I);
                if (stateField == null) return;
                if (Convert.ToInt32(stateField.GetValue(self)) != 1) return;

                var btn = t.GetProperty("BtnLaunch", I)?.GetValue(self, null);
                if (btn is UIElement ui)
                {
                    var textProp = btn.GetType().GetProperty("Text", I);
                    if (textProp != null && textProp.CanWrite) textProp.SetValue(btn, "下载游戏", null);
                    ui.IsEnabled = true;
                }
                stateField.SetValue(self, 2);   // 点击处理器按 state 分发，必须一起改
                if (!_launchLogged)
                {
                    _launchLogged = true;
                    Log("launch: no-instance download entry restored");
                }
            }
            catch (Exception ex) { Log("launch fix ex: " + ex.Message); }
        }

        // ───────── 辅助 ─────────
        static Type FindType(Assembly asm, string name)
        {
            try { var t = asm.GetType(name, false); if (t != null) return t; } catch { }
            try { return asm.GetTypes().FirstOrDefault(x => x.FullName == name); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.FirstOrDefault(x => x != null && x.FullName == name); }
            catch { return null; }
        }

        static ZipArchive OpenZip(string path)
        {
            try
            {
                var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return new ZipArchive(fs, ZipArchiveMode.Read);
            }
            catch (Exception ex)
            {
                Hint(string.Equals(Path.GetExtension(path), ".rar", StringComparison.OrdinalIgnoreCase)
                    ? "PCL 无法处理 rar 格式的压缩包，请解压后重新压缩为 zip 再试"
                    : "打开整合包文件失败：" + (ex.Message ?? "未知错误"));
                return null;
            }
        }

        static int DetectPackType(ZipArchive zip, out string baseFolder)
        {
            baseFolder = "";
            if (zip.GetEntry("mcbbs.packmeta") != null) return 3;
            if (zip.GetEntry("mmc-pack.json") != null) return 2;
            if (zip.GetEntry("modrinth.index.json") != null) return 4;
            if (zip.GetEntry("manifest.json") != null)
                return HasAddons(zip.GetEntry("manifest.json")) ? 3 : 0;
            if (zip.GetEntry("modpack.json") != null) return 1;
            if (zip.GetEntry("modpack.zip") != null || zip.GetEntry("modpack.mrpack") != null) return 9;

            foreach (var e in zip.Entries)
            {
                var parts = e.FullName.Split('/');
                if (parts.Length < 2) continue;
                baseFolder = parts[0] + "/";
                if (parts.Length != 2) continue;
                switch (parts[1])
                {
                    case "mcbbs.packmeta": return 3;
                    case "mmc-pack.json": return 2;
                    case "modrinth.index.json": return 4;
                    case "manifest.json":
                        if (HasAddons(e)) { baseFolder = "overrides/"; return 3; }
                        return 0;
                    case "modpack.json": return 1;
                    case "modpack.zip":
                    case "modpack.mrpack": return 9;
                }
            }
            return -1;
        }

        static bool HasAddons(ZipArchiveEntry entry)
        {
            try
            {
                using (var s = entry.Open())
                using (var r = new StreamReader(s, Encoding.UTF8))
                    return Regex.IsMatch(r.ReadToEnd(), "\"addons\"\\s*:");
            }
            catch { return false; }
        }

        static void Hint(string message)
        {
            Log("hint: " + message);
            try
            {
                var t = _asm?.GetType("PCL.ModMain", false);
                var mi = t?.GetMethods(F).FirstOrDefault(m => m.Name == "Hint");
                if (mi == null) return;
                var ps = mi.GetParameters();
                var args = new object[ps.Length];
                args[0] = message;
                for (int i = 1; i < ps.Length; i++)
                {
                    var pt = ps[i].ParameterType;
                    if (pt.IsEnum) args[i] = Enum.ToObject(pt, 2);
                    else if (pt == typeof(bool)) args[i] = true;
                    else args[i] = ps[i].HasDefaultValue ? ps[i].DefaultValue : null;
                }
                mi.Invoke(null, args);
            }
            catch { }
        }

        static void ProbeIfRequested()
        {
            try
            {
                var probe = Environment.GetEnvironmentVariable("PCL_HOOK_PROBE");
                if (probe != "1" && probe != "2" && probe != "3") return;

                if (probe == "3")
                {
                    try
                    {
                        var app = Application.Current;
                        if (app == null) { Log("probe3: no application"); return; }
                        app.Dispatcher.BeginInvoke(new Action(() =>
                        {
                            try
                            {
                                var pt = FindType(_asm, "PCL.PageDownloadModify");
                                var pageObj = Activator.CreateInstance(pt);
                                Log("probe3: page created");
                                pt.GetMethod("LoaderInit", I).Invoke(pageObj, null);
                                Log("probe3: LoaderInit invoked");
                            }
                            catch (Exception e)
                            {
                                var ie = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                                Log("probe3 ex: " + ie.GetType().Name + ": " + ie.Message);
                            }
                        }));
                    }
                    catch (Exception e) { Log("probe3 outer: " + e.Message); }
                    return;
                }
                var mi = _modpackType?.GetMethods(F)
                    .FirstOrDefault(m => m.Name == "ModpackInstall" && m.GetParameters().Length == 3);
                if (mi == null) { Log("probe: method missing"); return; }
                if (probe == "2")
                {
                    var rv = FindType(_asm, "PCL.ResourceVersion");
                    var tli = rv.GetMethods(I).First(m => m.Name == "ToListItem");
                    object inst = null;
                    try { inst = Activator.CreateInstance(rv, true); } catch (Exception e) { Log("probe2: ctor fail " + e.GetType().Name); }
                    if (inst != null)
                    {
                        try
                        {
                            var rr = tli.Invoke(inst, new object[] { null, null, false });
                            Log("probe2: ToListItem -> " + (rr == null ? "null" : rr.GetType().Name));
                        }
                        catch (Exception e)
                        {
                            var ie = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
                            Log("probe2: ToListItem ex " + ie.GetType().Name + ": " + ie.Message);
                        }
                    }
                    return;
                }
                var missing = Path.Combine(Path.GetTempPath(), "__pcl_hook_probe_missing.zip");
                Log("probe: invoke with nonexistent path");
                var r = mi.Invoke(null, new object[] { missing, null, null });
                Log("probe: returned " + (r == null ? "null" : r.GetType().Name));
            }
            catch (Exception ex)
            {
                var inner = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                Log("probe ex: " + inner.GetType().Name + ": " + inner.Message);
            }
        }

        // 日志写到 exe 同目录的 PCL\hook.log
        static void ResetLog()
        {
            try
            {
                lock (_lock)
                {
                    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL");
                    Directory.CreateDirectory(dir);
                    var p = Path.Combine(dir, "hook.log");
                    // 单实例启动器：二次启动会被踢掉，此时不要清空正在运行实例的日志
                    bool otherInstance = false;
                    try
                    {
                        var self = Process.GetCurrentProcess();
                        foreach (var pr in Process.GetProcessesByName(self.ProcessName))
                            if (pr.Id != self.Id) { otherInstance = true; break; }
                    }
                    catch { }
                    if (!otherInstance && File.Exists(p)) File.Delete(p);
                    _logPath = p;
                }
            }
            catch { }
        }

        static readonly object _lock = new object();
        static string _logPath;

        static string LogPath
        {
            get
            {
                if (_logPath != null) return _logPath;
                try
                {
                    var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PCL");
                    Directory.CreateDirectory(dir);
                    _logPath = Path.Combine(dir, "hook.log");
                }
                catch { _logPath = Path.Combine(Path.GetTempPath(), "hook.log"); }
                return _logPath;
            }
        }

        static void Log(string msg)
        {
            try
            {
                lock (_lock)
                {
                    var p = LogPath;
                    var fi = new FileInfo(p);
                    if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();
                    File.AppendAllText(p, "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
