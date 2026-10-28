// Host: embedded - owner group: EMBED (hosts/embedded, patcher/)
// Platform-independent (reflection on Gameloft.Engine): compiled into the embedded DLL and the net8 tests.
using System;
using System.Reflection;

namespace McvsOffline.Embedded.Hd
{
    /// <summary>
    /// <see cref="IEngineFiles"/> over the engine's own managed file API, by reflection (the host does not reference
    /// Gameloft.Engine): <c>GlfStream.SavePath</c> / <c>CachePath</c>, <c>GameApplication.Instance.DataPath</c>,
    /// <c>SaveStream.PushZip(string)</c>, <c>CacheStream.PushZip(string)</c>, <c>AssetStream.PushZip(string, bool)</c> (all three call
    /// <c>GlfStream.PushZip_protected</c> → native <c>AddZipSearchPath</c> and keep the engine's own zip stack in step),
    /// the internal <c>GlfStream_private.PushZipSearchPathFront(string)</c> and <c>AssetStream.ReadAllBytes(string, bool)</c>.
    /// Call it on the game thread, once the engine has created its GameApplication (patch 'graphics' does). Unbound in tests.
    /// </summary>
    internal sealed class EngineFiles : IEngineFiles
    {
        private readonly Type glfStream;
        private readonly Type glfPrivate;
        private readonly Type saveStream;
        private readonly Type cacheStream;
        private readonly Type assetStream;
        private readonly Type application;

        public EngineFiles()
        {
            glfStream = Find("Gameloft.Engine.GlfStream");
            glfPrivate = Find("Gameloft.Engine.GlfStream_private");
            saveStream = Find("Gameloft.Engine.SaveStream");
            cacheStream = Find("Gameloft.Engine.CacheStream");
            assetStream = Find("Gameloft.Engine.AssetStream");
            application = Find("Gameloft.Engine.GameApplication");
        }

        public bool Available
        {
            get { return glfStream != null && assetStream != null && application != null && AppInstance() != null; }
        }

        public string SavePath
        {
            get { return StaticString(glfStream, "SavePath"); }
        }

        public string CachePath
        {
            get { return StaticString(glfStream, "CachePath"); }
        }

        public string DataPath
        {
            get
            {
                try
                {
                    object app = AppInstance();
                    PropertyInfo p = app == null ? null : application.GetProperty("DataPath", BindingFlags.Public | BindingFlags.Instance);
                    return p == null ? null : p.GetValue(app, null) as string;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public bool PushZip(string location, string relativePath)
        {
            try
            {
                if (location == "Save") return Invoke(saveStream, "PushZip", new[] { typeof(string) }, relativePath);
                if (location == "Cache") return Invoke(cacheStream, "PushZip", new[] { typeof(string) }, relativePath);
                if (location == "Asset") return Invoke(assetStream, "PushZip", new[] { typeof(string), typeof(bool) }, relativePath, false);
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public bool PushZipFront(string relativePath)
        {
            try
            {
                MethodInfo m = glfPrivate == null ? null
                    : glfPrivate.GetMethod("PushZipSearchPathFront", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
                if (m == null) return false;
                m.Invoke(null, new object[] { relativePath });
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public byte[] ReadAsset(string logicalPath)
        {
            MethodInfo m = assetStream == null ? null
                : assetStream.GetMethod("ReadAllBytes", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(bool) }, null);
            if (m == null) return null;
            try
            {
                return m.Invoke(null, new object[] { logicalPath, false }) as byte[];
            }
            catch (TargetInvocationException ex)
            {
                throw ex.InnerException ?? ex;
            }
        }

        private object AppInstance()
        {
            try
            {
                PropertyInfo p = application == null ? null : application.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                return p == null ? null : p.GetValue(null, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static bool Invoke(Type type, string name, Type[] parameters, params object[] args)
        {
            MethodInfo m = type == null ? null : type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            if (m == null || m.ReturnType != typeof(bool)) return false;
            return (bool)m.Invoke(null, args);
        }

        private static string StaticString(Type type, string name)
        {
            try
            {
                PropertyInfo p = type == null ? null : type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
                return p == null ? null : p.GetValue(null, null) as string;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Type Find(string name)
        {
            try
            {
                return Type.GetType(name + ", Gameloft.Engine", false);
            }
            catch (Exception)
            {
                return null;
            }
        }

#if !MCVS_EMBEDDED
        /// <summary>Tests (RealGameReflectionTests): every member this class uses, checked against the real Gameloft.Engine types.</summary>
        internal static System.Collections.Generic.List<string> Verify(Func<string, Type> resolve)
        {
            var problems = new System.Collections.Generic.List<string>();
            Type glf = resolve("Gameloft.Engine.GlfStream");
            Type priv = resolve("Gameloft.Engine.GlfStream_private");
            Type save = resolve("Gameloft.Engine.SaveStream");
            Type cache = resolve("Gameloft.Engine.CacheStream");
            Type asset = resolve("Gameloft.Engine.AssetStream");
            Type app = resolve("Gameloft.Engine.GameApplication");
            if (glf == null || priv == null || save == null || cache == null || asset == null || app == null)
            {
                problems.Add("a Gameloft.Engine type is missing");
                return problems;
            }
            foreach (string p in new[] { "SavePath", "CachePath" })
            {
                PropertyInfo prop = glf.GetProperty(p, BindingFlags.Public | BindingFlags.Static);
                if (prop == null || prop.PropertyType != typeof(string)) problems.Add("GlfStream." + p + " : string");
            }
            PropertyInfo instance = app.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instance == null || instance.PropertyType != app) problems.Add("GameApplication.Instance");
            PropertyInfo dataPath = app.GetProperty("DataPath", BindingFlags.Public | BindingFlags.Instance);
            if (dataPath == null || dataPath.PropertyType != typeof(string)) problems.Add("GameApplication.DataPath : string");
            Expect(problems, save, "PushZip", typeof(bool), typeof(string));
            Expect(problems, cache, "PushZip", typeof(bool), typeof(string));
            Expect(problems, asset, "PushZip", typeof(bool), typeof(string), typeof(bool));
            Expect(problems, asset, "ReadAllBytes", typeof(byte[]), typeof(string), typeof(bool));
            MethodInfo front = priv.GetMethod("PushZipSearchPathFront", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (front == null || front.ReturnType != typeof(void)) problems.Add("GlfStream_private.PushZipSearchPathFront(string) : void");
            return problems;
        }

        private static void Expect(System.Collections.Generic.List<string> problems, Type type, string name, Type returns, params Type[] parameters)
        {
            MethodInfo m = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static, null, parameters, null);
            if (m == null || m.ReturnType != returns) problems.Add(type.Name + "." + name + " : " + returns.Name);
        }
#endif
    }
}
