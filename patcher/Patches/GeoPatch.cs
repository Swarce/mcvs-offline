using System;
using System.Globalization;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace McvsOffline.Patcher.Patches
{
    /// <summary>
    /// Optional 'geo' patch (ARCHITECTURE §2.3): <c>Gameloft.Util.Geolocalization.GeolocalizationUrl</c> returns the
    /// hard-coded <c>http://ip.ecomapis.gameloft.org/location/?ip=</c>, queried during the TS handshake with a
    /// timeout (100 s) longer than the handshake's own (60 s). Offline that request can hang on DNS; pointed at the
    /// embedded server it answers at once (the MISC group's Geo module serves <c>GET location/?ip=</c>).
    /// <para>Build 26 (port fallback, docs/design/port-fallback.md): the URL follows the port the host bound at this start,
    /// which is not always the one known at patch time:</para>
    /// <code>
    /// get_GeolocalizationUrl: ldstr "http://127.0.0.1:&lt;--port&gt;/location/?ip="
    ///                         call string McvsOffline.PortHooks::GeolocalizationUrl(string)   // added
    ///                         ret
    /// </code>
    /// The hook returns the baked string until the host has decided, so <c>--port</c> remains the fallback value; with the port
    /// free the URL is the same as before. Gameloft.Util.dll references McvsOffline.Server.dll from this build on.
    /// </summary>
    public sealed class GeoPatch : IlPatch
    {
        public const string TypeName = "Gameloft.Util.Geolocalization";
        public const string Getter = "get_GeolocalizationUrl";
        public const string OriginalPrefix = "http://ip.ecomapis.gameloft.org/location/";

        public override string Id => "geo";

        public override string AssemblyFile => "Gameloft.Util.dll";

        public override string Description => "IP geolocation URL -> the embedded server on this start's port (no DNS wait during the TS handshake)";

        public const string HooksType = "McvsOffline.PortHooks";

        public static string ReplacementPrefix(int port)
        {
            return "http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) + "/location/";
        }

        public override void Apply(ModuleDefinition module, PatchContext context)
        {
            PatchSites.RequireBuildAssembly(module, context, Id);
            TypeDefinition type = RequireType(module, TypeName);
            MethodDefinition getter = RequireMethod(type, Getter, m => !m.HasParameters && m.ReturnType.MetadataType == MetadataType.String,
                                                    "string getter");
            var sites = getter.Body.Instructions
                .Where(i => i.OpCode == OpCodes.Ldstr && ((string)i.Operand).StartsWith(OriginalPrefix, StringComparison.Ordinal))
                .ToList();
            if (sites.Count != 1)
            {
                throw new PatchException(type.FullName + "::" + Getter + ": expected one ldstr \"" + OriginalPrefix + "...\", found " +
                                         sites.Count + ":\n" + Dump(getter));
            }
            MethodReference hook = ImportServerMethod(module, context, HooksType, "GeolocalizationUrl", "System.String", "System.String");

            string original = (string)sites[0].Operand;
            string replaced = ReplacementPrefix(context.Port) + original.Substring(OriginalPrefix.Length);
            sites[0].Operand = replaced;
            ILProcessor il = getter.Body.GetILProcessor();
            il.InsertAfter(sites[0], il.Create(OpCodes.Call, hook));
            context.Notes.Add(type.FullName + "::" + Getter + ": \"" + original + "\" -> " + HooksType + ".GeolocalizationUrl(\"" + replaced +
                              "\") (this start's port)");
        }
    }
}
