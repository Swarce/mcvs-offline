// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Android only: compiled into the embedded DLL (MCVS_EMBEDDED), never into the net8 build.
#if MCVS_EMBEDDED
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using Android.Runtime;
using McvsOffline.Embedded.Install;
using McvsOffline.Server.Core;

namespace McvsOffline.Embedded.Android
{
    /// <summary>
    /// The Java installer's SharedPreferences ("MainActivityPrefs"), through the game's own static helpers
    /// <c>SUtils.getPreferenceString/getPreferenceInt/setPreference</c> (raw JNI: the linker trimmed Mono.Android's
    /// SharedPreferences binding). setPreference commits synchronously.
    /// </summary>
    internal sealed class AndroidInstallerPrefs : IInstallerPrefs
    {
        private const string SUtilsClass = "com/gameloft/android/GLUtils/SUtils";
        private const string PreferencesName = "MainActivityPrefs";   // GameInstaller.mPreferencesName

        public string GetString(string key)
        {
            try
            {
                IntPtr cls = JNIEnv.FindClass(SUtilsClass);
                IntPtr k = JNIEnv.NewString(key), d = JNIEnv.NewString(""), n = JNIEnv.NewString(PreferencesName);
                try
                {
                    IntPtr id = JNIEnv.GetStaticMethodID(cls, "getPreferenceString", "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
                    string value = JNIEnv.GetString(JNIEnv.CallStaticObjectMethod(cls, id, new[] { new JValue(k), new JValue(d), new JValue(n) }),
                                                    JniHandleOwnership.TransferLocalRef);
                    return string.IsNullOrEmpty(value) ? null : value;
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(k);
                    JNIEnv.DeleteLocalRef(d);
                    JNIEnv.DeleteLocalRef(n);
                    JNIEnv.DeleteGlobalRef(cls);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        public int GetInt(string key, int defaultValue)
        {
            try
            {
                IntPtr cls = JNIEnv.FindClass(SUtilsClass);
                IntPtr k = JNIEnv.NewString(key), n = JNIEnv.NewString(PreferencesName);
                try
                {
                    IntPtr id = JNIEnv.GetStaticMethodID(cls, "getPreferenceInt", "(Ljava/lang/String;ILjava/lang/String;)I");
                    return JNIEnv.CallStaticIntMethod(cls, id, new[] { new JValue(k), new JValue(defaultValue), new JValue(n) });
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(k);
                    JNIEnv.DeleteLocalRef(n);
                    JNIEnv.DeleteGlobalRef(cls);
                }
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        public bool SetString(string key, string value)
        {
            try
            {
                IntPtr v = JNIEnv.NewString(value ?? "");
                try
                {
                    return Set(key, v);
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(v);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool SetInt(string key, int value)
        {
            try
            {
                IntPtr integer = JNIEnv.FindClass("java/lang/Integer");
                try
                {
                    IntPtr valueOf = JNIEnv.GetStaticMethodID(integer, "valueOf", "(I)Ljava/lang/Integer;");
                    IntPtr boxed = JNIEnv.CallStaticObjectMethod(integer, valueOf, new[] { new JValue(value) });
                    try
                    {
                        return Set(key, boxed);
                    }
                    finally
                    {
                        JNIEnv.DeleteLocalRef(boxed);
                    }
                }
                finally
                {
                    JNIEnv.DeleteGlobalRef(integer);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>SUtils.setPreference(String key, Object value, String prefsName).</summary>
        private static bool Set(string key, IntPtr javaValue)
        {
            IntPtr cls = JNIEnv.FindClass(SUtilsClass);
            IntPtr k = JNIEnv.NewString(key), n = JNIEnv.NewString(PreferencesName);
            try
            {
                IntPtr id = JNIEnv.GetStaticMethodID(cls, "setPreference", "(Ljava/lang/String;Ljava/lang/Object;Ljava/lang/String;)V");
                JNIEnv.CallStaticVoidMethod(cls, id, new[] { new JValue(k), new JValue(javaValue), new JValue(n) });
                return true;
            }
            finally
            {
                JNIEnv.DeleteLocalRef(k);
                JNIEnv.DeleteLocalRef(n);
                JNIEnv.DeleteGlobalRef(cls);
            }
        }
    }

    /// <summary>
    /// Diagnostics: logs the Java GameInstaller's state machine while it runs (GameInstaller.f, its download sub-state M, the
    /// "Downloading X/Y MB" counters and its error numbers, addErrorNumber), so a stall on the download screen says where it
    /// is. Reads fields only (JNI ignores access), every 250 ms, until the installer hands over to the game or 30 minutes
    /// pass. Never writes anything into the installer.
    /// </summary>
    internal static class InstallerMonitor
    {
        private const string InstallerClass = "com/gameloft/android/installer/GameInstaller";

        /// <summary>GameInstaller.f values (layouts and n()/r() of GameInstaller.java).</summary>
        private static readonly Dictionary<int, string> States = new Dictionary<int, string>
        {
            { 0, "start" }, { 2, "check data against its index" }, { 3, "decide (logo, 3 s)" }, { 4, "no storage or space" },
            { 5, "ask: mobile data?" }, { 6, "turn Wi-Fi on" }, { 7, "wait for Wi-Fi" }, { 9, "start download" },
            { 10, "ask: download over carrier?" }, { 12, "index/download (CDN)" }, { 13, "download finished" },
            { 14, "error screen" }, { 20, "verify (CRC)" }, { 21, "start the game" }, { 23, "post-check (GameOptions, weekly check)" },
            { 24, "update check (CDN head)" }, { 25, "compare versions" }, { 27, "ask: update?" }, { 28, "update check failed" },
            { 41, "unzip" }, { 43, "logo" },
        };

        /// <summary>The download sub-state M in state 12 (GameInstaller.o()).</summary>
        private static readonly Dictionary<int, string> SubStates = new Dictionary<int, string>
        {
            { -1, "entering" }, { 0, "init" }, { 2, "prepare" }, { 10, "HEAD d.cdn.php (version)" }, { 3, "GET d.cdn.php + index" },
            { 8, "compare files with the index" }, { 9, "nothing to download" }, { 6, "start the downloaders" }, { 7, "downloading" },
            { 5, "paused" },
        };

        /// <summary>1 when the installer has handed over to the game (sbStarted, or state 21), 0 not yet, -1 cannot tell.</summary>
        public static int HandedOver()
        {
            IntPtr cls = IntPtr.Zero;
            try
            {
                cls = JNIEnv.FindClass(InstallerClass);
                IntPtr startedField = JNIEnv.GetStaticFieldID(cls, "sbStarted", "Z");
                if (JNIEnv.GetStaticBooleanField(cls, startedField)) return 1;
                IntPtr instanceField = JNIEnv.GetStaticFieldID(cls, "m_sInstance", "Lcom/gameloft/android/installer/GameInstaller;");
                IntPtr instance = JNIEnv.GetStaticObjectField(cls, instanceField);
                if (instance == IntPtr.Zero) return 0;
                try
                {
                    return JNIEnv.GetIntField(instance, JNIEnv.GetFieldID(cls, "f", "I")) == 21 ? 1 : 0;
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(instance);
                }
            }
            catch (Exception)
            {
                return -1;
            }
            finally
            {
                if (cls != IntPtr.Zero)
                {
                    try
                    {
                        JNIEnv.DeleteGlobalRef(cls);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        public static void Start(Log log, BootTrace boot)
        {
            var thread = new Thread(() => Run(log, boot));
            thread.IsBackground = true;
            thread.Name = "mcvs-installer-monitor";
            thread.Start();
        }

        private static void Run(Log log, BootTrace boot)
        {
            IntPtr cls = IntPtr.Zero;
            try
            {
                cls = JNIEnv.FindClass(InstallerClass);
                IntPtr instanceField = JNIEnv.GetStaticFieldID(cls, "m_sInstance", "Lcom/gameloft/android/installer/GameInstaller;");
                IntPtr startedField = JNIEnv.GetStaticFieldID(cls, "sbStarted", "Z");
                IntPtr errorField = JNIEnv.GetStaticFieldID(cls, "m_errorMessage", "Ljava/lang/String;");
                IntPtr fField = JNIEnv.GetFieldID(cls, "f", "I");
                IntPtr mField = JNIEnv.GetFieldID(cls, "M", "I");
                IntPtr totalField = JNIEnv.GetFieldID(cls, "c", "J");
                IntPtr presentField = JNIEnv.GetFieldID(cls, "d", "J");
                IntPtr kbField = JNIEnv.GetFieldID(cls, "h", "I");

                int started = Environment.TickCount;
                int lastF = -1000, lastM = -1000;   // never a state (int.MinValue is trimmed)
                string lastError = "";
                var path = new StringBuilder();
                int lastProgressLog = 0;
                bool seen = false;
                while (Environment.TickCount - started < 30 * 60 * 1000)
                {
                    Thread.Sleep(250);
                    IntPtr instance = JNIEnv.GetStaticObjectField(cls, instanceField);
                    if (instance == IntPtr.Zero) continue;
                    try
                    {
                        seen = true;
                        int f = JNIEnv.GetIntField(instance, fField);
                        int m = JNIEnv.GetIntField(instance, mField);
                        string error = JNIEnv.GetString(JNIEnv.GetStaticObjectField(cls, errorField), JniHandleOwnership.TransferLocalRef) ?? "";
                        bool handedOver = JNIEnv.GetStaticBooleanField(cls, startedField);
                        if (f != lastF)
                        {
                            if (path.Length > 0) path.Append(" > ");
                            path.Append(f.ToString(CultureInfo.InvariantCulture));
                            log.Info("[installer] state " + f + " (" + Name(States, f) + ")" + (f == 12 ? ", step " + m + " (" + Name(SubStates, m) + ")" : ""));
                            if (f == 14) boot.Mark("installer: error screen (errors" + (error.Length == 0 ? " none recorded" : error) + "); path " + path);
                        }
                        else if (f == 12 && m != lastM)
                        {
                            log.Info("[installer] state 12, step " + m + " (" + Name(SubStates, m) + ")");
                        }
                        if (error != lastError && error.Trim().Length > 0)
                        {
                            log.Warn("[installer] error numbers so far:" + error + " (GameInstaller.addErrorNumber; see docs/INSTALL-OFFLINE.md)");
                        }
                        if (f == 12 && m == 7 && Environment.TickCount - lastProgressLog > 10000)
                        {
                            lastProgressLog = Environment.TickCount;
                            long total = JNIEnv.GetLongField(instance, totalField);
                            long present = JNIEnv.GetLongField(instance, presentField);
                            int kb = JNIEnv.GetIntField(instance, kbField);
                            log.Info("[installer] downloading: " + ((present / 1024 + kb) / 1024) + " of " + (total >> 20) + " MB shown (" + (kb / 1024) + " MB fetched)");
                        }
                        lastF = f;
                        lastM = m;
                        lastError = error;
                        if (handedOver || f == 21)
                        {
                            boot.Mark("installer finished after " + ((Environment.TickCount - started) / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) +
                                      " s (states " + path + (lastError.Trim().Length > 0 ? "; errors" + lastError : "") + ")");
                            return;
                        }
                    }
                    finally
                    {
                        JNIEnv.DeleteLocalRef(instance);
                    }
                }
                if (seen) log.Warn("[installer] still running after 30 minutes (states " + path + ")");
            }
            catch (Exception ex)
            {
                log.Info("[installer] monitor stopped: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                if (cls != IntPtr.Zero)
                {
                    try
                    {
                        JNIEnv.DeleteGlobalRef(cls);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private static string Name(Dictionary<int, string> names, int value)
        {
            string name;
            return names.TryGetValue(value, out name) ? name : "?";
        }
    }
}
#endif
