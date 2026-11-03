using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TodoAssistant.Services
{
    /// <summary>开始菜单快捷方式 + AUMID 注册（Windows 通知中心对未打包应用的要求）</summary>
    public static class ShortcutHelper
    {
        public const string ExeName = "TodoAssistant.exe";
        public const string DisplayName = "待办小助手";

        /// <summary>当前 exe 完整路径</summary>
        public static string ExePath()
        {
            try
            {
                string p = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(p) && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return p;
            }
            catch { }
            try
            {
                var m = System.Diagnostics.Process.GetCurrentProcess().MainModule;
                if (m != null && !string.IsNullOrEmpty(m.FileName)) return m.FileName;
            }
            catch { }
            return System.IO.Path.Combine(AppContext.BaseDirectory, ExeName);
        }

        public static string AppDataRoot => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TodoAssistant");

        public static string StartMenuShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), DisplayName + ".lnk");

        public static string StartupShortcutPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup), DisplayName + ".lnk");

        /// <summary>注册 AUMID：快捷方式 System.AppUserModel.ID = 待办小助手</summary>
        public static bool EnsureAppRegistration(string exePath)
        {
            try
            {
                string lnk = StartMenuShortcutPath;
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                if (File.Exists(lnk))
                {
                    string existing = ReadShortcutTarget(lnk);
                    if (string.Equals(existing, exePath, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                CreateShortcut(lnk, exePath, "", DisplayName, DisplayName + " —— 待办与提醒", null);
                Log.Write("已创建开始菜单快捷方式: " + lnk);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("创建开始菜单快捷方式失败（Windows 通知可能无法显示）:" + ex.GetType().Name, ex);
                Log.Write("  快捷方式路径: " + StartMenuShortcutPath + "，目标: " + exePath);
                var inner = ex.InnerException;
                while (inner != null)
                {
                    Log.Write("  内层异常: " + inner.GetType().FullName + " HRESULT=" + inner.HResult + " " + inner.Message);
                    inner = inner.InnerException;
                }
                return false;
            }
        }

        public static void CreateStartupShortcut(string exePath)
        {
            string lnk = StartupShortcutPath;
            Directory.CreateDirectory(Path.GetDirectoryName(lnk));
            CreateShortcut(lnk, exePath, "--autostart", DisplayName, "开机自动启动，驻留系统托盘", null);
        }

        public static void RemoveStartupShortcut()
        {
            try
            {
                if (File.Exists(StartupShortcutPath)) File.Delete(StartupShortcutPath);
            }
            catch (Exception ex) { Log.Error("删除开机启动快捷方式失败", ex); }
        }

        public static bool StartupShortcutExists => File.Exists(StartupShortcutPath);

        /// <summary>注册表开机自启兜底（若启动文件夹被策略禁用时使用）</summary>
        public static void SetRunKey(bool enable, string exePath)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", true);
                if (key == null) return;
                if (enable)
                    key.SetValue(DisplayName, "\"" + exePath + "\" --autostart");
                else if (key.GetValue(DisplayName) != null)
                    key.DeleteValue(DisplayName, false);
            }
            catch (Exception ex) { Log.Error("写入 Run 注册表失败", ex); }
        }

        private static void CreateShortcut(string lnkPath, string target, string args, string description,
                                            string tooltip, string iconPath)
        {
            // 优先用 Explorer 自带的 ShellLink COM（与资源管理器权限一致），失败再退回 WScript.Shell
            try
            {
                var link = (IShellLinkW)new ShellLinkCoClass();
                link.SetPath(target);
                if (!string.IsNullOrEmpty(args)) link.SetArguments(args);
                link.SetWorkingDirectory(Path.GetDirectoryName(target) ?? "");
                if (!string.IsNullOrEmpty(description)) link.SetDescription(description);
                link.SetIconLocation(iconPath ?? (target + ",0"), 0);
                ((IPersistFileLn)link).Save(lnkPath, true);
                SetAppUserModelId(lnkPath, DisplayName);
                return;
            }
            catch (Exception ex)
            {
                Log.Write("ShellLink 创建失败（" + ex.Message + "），改用 WScript.Shell");
            }

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) throw new InvalidOperationException("WScript.Shell 不可用");
            object shell = Activator.CreateInstance(shellType);
            object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                                                     null, shell, new object[] { lnkPath });
            Type scType = shortcut.GetType();
            scType.InvokeMember("TargetPath", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { target });
            scType.InvokeMember("Arguments", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { args });
            scType.InvokeMember("WorkingDirectory", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { Path.GetDirectoryName(target) });
            scType.InvokeMember("Description", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { description });
            scType.InvokeMember("IconLocation", System.Reflection.BindingFlags.SetProperty, null, shortcut, new object[] { iconPath ?? (target + ",0") });
            scType.InvokeMember("Save", System.Reflection.BindingFlags.InvokeMethod, null, shortcut, null);
            SetAppUserModelId(lnkPath, DisplayName);
        }

        // ---------- Explorer 的 ShellLink COM 接口 ----------

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class ShellLinkCoClass { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszFile, int cch, IntPtr pfd, uint fFlags);
            void GetIDList(out IntPtr ppidl);
            void SetIDList(IntPtr pidl);
            void GetDescription([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszName, int cch);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszDir, int cch);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
            void GetArguments([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszArgs, int cch);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
            void GetHotkey(out short pwHotkey);
            void SetHotkey(short wHotkey);
            void GetShowCmd(out int piShowCmd);
            void SetShowCmd(int iShowCmd);
            void GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pszIconPath, int cch, out int piIcon);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
            void Resolve(IntPtr hwnd, uint fFlags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFileLn
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }

        private static string ReadShortcutTarget(string lnkPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(shellType);
                object shortcut = shellType.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod,
                                                         null, shell, new object[] { lnkPath });
                return (string)shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null);
            }
            catch { return null; }
        }

        // ---------- 通过 COM 给 .lnk 打上 System.AppUserModel.ID ----------

        [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
        private class CShellLink { }

        [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPropertyStore
        {
            int GetCount(out uint cProps);
            int GetAt(uint iProp, out PropertyKey pkey);
            int GetValue(ref PropertyKey key, out PropVariant pv);
            int SetValue(ref PropertyKey key, ref PropVariant pv);
            int Commit();
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        private struct PropertyKey
        {
            public Guid formatId;
            public uint propertyId;
            public PropertyKey(Guid f, uint p) { formatId = f; propertyId = p; }
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct PropVariant
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(8)] public IntPtr pointerValue;
            [FieldOffset(8)] public int intValue;
        }

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int PropVariantClear(ref PropVariant pvar);

        /// <summary>把 System.AppUserModel.ID 写入快捷方式</summary>
        public static bool SetAppUserModelId(string lnkPath, string aumid)
        {
            object link = null;
            try
            {
                link = new CShellLink();
                ((IPersistFile)link).Load(lnkPath, 2 /* STGM_READWRITE */);
                var store = (IPropertyStore)link;
                var key = new PropertyKey(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);   // System.AppUserModel.ID

                var pv = new PropVariant { vt = 31 /* VT_LPWSTR */ };
                pv.pointerValue = Marshal.StringToCoTaskMemUni(aumid);
                try
                {
                    int hr = store.SetValue(ref key, ref pv);
                    if (hr < 0) throw Marshal.GetExceptionForHR(hr);
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pv.pointerValue);
                }
                int hr2 = store.Commit();
                if (hr2 < 0) throw Marshal.GetExceptionForHR(hr2);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("写入 AppUserModelID 失败: " + lnkPath, ex);
                return false;
            }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            }
        }

        [ComImport, Guid("0000010b-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPersistFile
        {
            void GetClassID(out Guid pClassID);
            void IsDirty();
            void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);
            void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, bool remember);
            void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);
            void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
        }

        /// <summary>设置当前进程 AUMID（让 Toast 归属于同一个 AppId）</summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);

        public static void SetProcessAumid(string appId)
        {
            try { SetCurrentProcessExplicitAppUserModelID(appId); }
            catch (Exception ex) { Log.Error("SetProcessAumid 失败", ex); }
        }
    }
}
