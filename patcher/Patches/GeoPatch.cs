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
    /// </summary>
    public sealed class GeoPatch : IlPatch
    {
        public const string TypeName = "Gameloft.Util.Geolocalization";
        public const string Getter = "get_GeolocalizationUrl";
        public const string OriginalPrefix = "http://ip.ecomapis.gameloft.org/location/";

        public override string Id => "geo";

        public override string AssemblyFile => "Gameloft.Util.dll";

        public override string Description => "IP geolocation URL -> the embedded server (no DNS wait during the TS handshake)";

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
            string original = (string)sites[0].Operand;
            string replaced = ReplacementPrefix(context.Port) + original.Substring(OriginalPrefix.Length);
            sites[0].Operand = replaced;
            context.Notes.Add(type.FullName + "::" + Getter + ": \"" + original + "\" -> \"" + replaced + "\"");
        }
    }
}
