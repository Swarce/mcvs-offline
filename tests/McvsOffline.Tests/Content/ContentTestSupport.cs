using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;

namespace McvsOffline.Tests.Content
{
    /// <summary>Helpers for the CONTENT group's tests (CRM, matchmaker, assets, generated content).</summary>
    internal static class ContentTestSupport
    {
        public const string ClientId = "2620:66102:1.17.32:android:googleplay";

        /// <summary>The player's on-device Data folder when available (MCVS_DATAPACK or the work tree default).</summary>
        public static string RealDataPack
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_DATAPACK");
                if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/datapack/1.17.32/ETC/files/Data";
                return Directory.Exists(dir) && File.Exists(Path.Combine(dir, "MCVS.game")) ? dir : null;
            }
        }

        /// <summary>The decoded data pack (tools/datapack output) when available.</summary>
        public static string DecodedDataPack
        {
            get
            {
                string dir = Environment.GetEnvironmentVariable("MCVS_DATAPACK_DECODED");
                if (string.IsNullOrEmpty(dir)) dir = "/home/user/mcvs-work/datapack-decoded";
                return File.Exists(Path.Combine(dir, "_catalog.json")) ? dir : null;
            }
        }

        /// <summary>The repository root (the folder with McvsOffline.sln).</summary>
        public static string RepoRoot
        {
            get
            {
                string dir = AppContext.BaseDirectory;
                while (dir != null && !File.Exists(Path.Combine(dir, "McvsOffline.sln"))) dir = Path.GetDirectoryName(dir);
                if (dir == null) throw new InvalidOperationException("repository root not found from " + AppContext.BaseDirectory);
                return dir;
            }
        }

        /// <summary>A composed server (all modules) that is not listening; requests go through <see cref="McvsServer.Handle"/>.</summary>
        public static McvsServer CreateServer(string dataDir, string dataPackDir = null, MemoryLogSink logs = null, bool lan = false)
        {
            var options = new ServerOptions
            {
                DataDir = dataDir,
                DataPackDir = dataPackDir,
                Port = 51780,
                ConsoleLog = false,
                FileLog = false,
                LanMode = lan,
            };
            if (logs != null) options.ExtraSinks.Add(logs);
            return McvsServer.Create(options);
        }

        public static HttpResponse Get(McvsServer server, string target, string host = "127.0.0.1:51780", string range = null)
        {
            var headers = new HttpHeaders();
            headers.Add("Host", host);
            if (range != null) headers.Add("Range", range);
            return server.Handle(new HttpRequest("GET", target, headers, null));
        }

        public static HttpResponse Post(McvsServer server, string target, string form)
        {
            return server.Handle(HttpRequest.Create("POST", target, form));
        }

        public static object Json(HttpResponse response)
        {
            return MiniJson.Parse(response.BodyText);
        }

        public static Dictionary<string, object> JsonObject(HttpResponse response)
        {
            return MiniJson.ParseObject(response.BodyText);
        }

        /// <summary>
        /// A minimal on-device style data pack: the MCVS.game marker plus <c>Config/crmpreconfiguration.json</c> stored
        /// raw under its crc32 name (DataPack returns non-GCBF files unchanged).
        /// </summary>
        public static string CreateFakeDataPack(string dir, string crmJson)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "MCVS.game"), "fake");
            string hashed = Path.Combine(dir, DataPack.HashedPath("Config/crmpreconfiguration.json").Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(hashed));
            File.WriteAllText(hashed, crmJson, new UTF8Encoding(false));
            return dir;
        }

        /// <summary>A small preconfiguration with the same shape as the shipped one (report 04 §5, verify-C2 §1.7).</summary>
        public static string SamplePreconfiguration()
        {
            return @"{
  ""pointcut_actions"": [
    { ""id"": ""ads_test"", ""type"": ""show_ad_button"", ""frequency"": 1, ""limit"": [], ""pointcuts"": [ { ""name"": ""enter_section"", ""conditions"": [ ""section == timed_crate"" ] } ] },
    { ""id"": ""keep_me"", ""type"": ""log"", ""frequency"": 1, ""limit"": [], ""pointcuts"": [] }
  ],
  ""game"": {
    ""AgeGateParams"": { ""Ignore"": false },
    ""CrashlyticsParams"": { ""Enable"": true },
    ""_OverrideTables"": {
      ""MCVS_Gameplay"": { ""Hacker"": { ""Id@DroneHealth"": {}, ""Id@NextHackDelay"": {} } },
      ""MCVS_Meta"": { ""MercsList"": { ""Id@TrialDays"": { ""3"": ""7"" } } }
    },
    ""expiry"": null,
    ""tags"": [ ""WORLD_GAME_Default"" ]
  },
  ""offline_store"": {
    ""prices"": [
      { ""item"": ""UnlockMerc_1"", ""quantity"": 1, ""category"": [], ""hidden"": false,
        ""billing_methods"": [ { ""name"": ""offline"", ""type"": ""offline"", ""price"": [ { ""currency"": ""AgentsCurrency"", ""price"": 3000 } ], ""replaced_price"": null } ] },
      { ""item"": ""SoftCurrency_Pack1"", ""quantity"": 1, ""category"": [], ""hidden"": false,
        ""billing_methods"": [ { ""name"": ""offline"", ""type"": ""offline"", ""price"": [ { ""currency"": ""HardCurrency"", ""price"": 50 } ], ""replaced_price"": null } ] }
    ],
    ""expiry"": null,
    ""tags"": []
  },
  ""iap"": {
    ""en"": { ""prices"": [ { ""_id"": ""Pack_A"", ""hidden"": true, ""quantity"": 1, ""item"": ""HardCurrency"", ""billing_methods"": [ { ""price"": 0.99 } ] } ] },
    ""fr"": { ""prices"": [] }
  }
}";
        }
    }

    /// <summary>A composed, non-listening server in a temporary directory, with captured logs.</summary>
    internal sealed class McvsServerHandle : IDisposable
    {
        private readonly TempDir dir = new TempDir();

        public McvsServerHandle(bool lan = false)
        {
            Logs = new MemoryLogSink();
            Server = ContentTestSupport.CreateServer(dir.Combine("data"), null, Logs, lan);
        }

        public McvsServer Server { get; }

        public MemoryLogSink Logs { get; }

        public string Combine(params string[] parts) => dir.Combine(parts);

        public void Dispose()
        {
            Server.Dispose();
            dir.Dispose();
        }
    }
}
