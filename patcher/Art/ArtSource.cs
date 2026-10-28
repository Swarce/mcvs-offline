using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;

namespace McvsOffline.Patcher.Art
{
    /// <summary>
    /// Where the project's own art comes from (app-icon-and-launch-screen.md §4, §5.3): the files of <c>art/android/</c>
    /// built into the patcher (the default, so players run the same command as before), or a folder given with
    /// <c>--art &lt;dir&gt;</c> (the artist's test builds without rebuilding the patcher). Paths are relative to the
    /// <c>art/android/</c> folder, with '/' separators: <c>art.json</c>, <c>icon/foreground.png</c>,
    /// <c>icon/xxhdpi/ic_launcher.png</c>...
    /// </summary>
    public abstract class ArtSource
    {
        /// <summary>For the report: "built-in art/android" or the folder.</summary>
        public abstract string Description { get; }

        /// <summary>Every art file (documentation, examples and hidden files left out), sorted.</summary>
        public abstract IReadOnlyList<string> Files { get; }

        public abstract byte[] Read(string path);

        public bool Contains(string path)
        {
            return Files.Contains(path, StringComparer.Ordinal);
        }

        /// <summary>Files that are documentation or examples, never art: README*, *.md, *.example.json, dot files.</summary>
        public static bool IsIgnored(string path)
        {
            string name = path.Substring(path.LastIndexOf('/') + 1);
            return name.StartsWith(".", StringComparison.Ordinal) || name.StartsWith("README", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".example.json", StringComparison.OrdinalIgnoreCase) ||
                   path.Split('/').Any(part => part.StartsWith(".", StringComparison.Ordinal));
        }
    }

    /// <summary>A folder laid out like art/android/ (--art &lt;dir&gt;).</summary>
    public sealed class DirectoryArtSource : ArtSource
    {
        private readonly string root;
        private readonly List<string> files;

        public DirectoryArtSource(string directory)
        {
            root = Path.GetFullPath(directory);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException("--art: folder " + directory + " does not exist");
            files = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
                             .Select(f => Path.GetRelativePath(root, f).Replace(Path.DirectorySeparatorChar, '/'))
                             .Where(f => !IsIgnored(f))
                             .OrderBy(f => f, StringComparer.Ordinal)
                             .ToList();
        }

        public override string Description { get { return root; } }

        public override IReadOnlyList<string> Files { get { return files; } }

        public override byte[] Read(string path)
        {
            return File.ReadAllBytes(Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// The art built into the patcher: McvsOffline.Patcher.csproj embeds <c>art/android/**</c> as manifest resources named
    /// <c>art/&lt;path&gt;</c>. With no art in the repository there are no such resources, and the patcher keeps the stock icon.
    /// </summary>
    public sealed class EmbeddedArtSource : ArtSource
    {
        public const string Prefix = "art/";

        private readonly Assembly assembly;
        private readonly Dictionary<string, string> resources;   // relative path -> manifest resource name

        public EmbeddedArtSource(Assembly assembly = null)
        {
            this.assembly = assembly ?? typeof(EmbeddedArtSource).Assembly;
            resources = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string name in this.assembly.GetManifestResourceNames())
            {
                string path = name.Replace('\\', '/');
                if (!path.StartsWith(Prefix, StringComparison.Ordinal)) continue;
                path = path.Substring(Prefix.Length);
                if (!IsIgnored(path)) resources[path] = name;
            }
        }

        public override string Description { get { return "built-in art/android"; } }

        public override IReadOnlyList<string> Files { get { return resources.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(); } }

        public override byte[] Read(string path)
        {
            using (Stream stream = assembly.GetManifestResourceStream(resources[path]))
            using (var copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                return copy.ToArray();
            }
        }
    }

    /// <summary>
    /// art.json (app-icon-and-launch-screen.md §4.2). Every key is optional; an unknown key is an error, so a typo does not
    /// silently keep the stock look. Colours are "#RRGGBB" only: the framework treats a colour of 0 as "not set"
    /// (<c>peekWindowBGColor</c>), so alpha is always FF.
    /// <code>
    /// {
    ///   "icon":   { "background": "#10181F" },
    ///   "splash": { "background": "#10181F", "backgroundNight": "#000000", "iconBackground": null,
    ///               "iconBackgroundNight": null, "iconPreferred": true, "windowBackground": false }
    /// }
    /// </code>
    /// </summary>
    public sealed class ArtSettings
    {
        public const string FileName = "art.json";

        /// <summary>The adaptive icon's background layer as a flat colour (instead of icon/background.png), RGB.</summary>
        public uint? IconBackground { get; private set; }

        public SplashSettings Splash { get; private set; }

        public sealed class SplashSettings
        {
            /// <summary>windowSplashScreenBackground (required for a splash change), RGB.</summary>
            public uint Background { get; set; }

            /// <summary>The same in night mode; null = <see cref="Background"/>.</summary>
            public uint? BackgroundNight { get; set; }

            /// <summary>windowSplashScreenIconBackgroundColor: a disc behind the icon; null = not set.</summary>
            public uint? IconBackground { get; set; }

            public uint? IconBackgroundNight { get; set; }

            /// <summary>windowSplashScreenBehavior=icon_preferred (API 33+): the icon also on a relaunch from a notification or alarm.</summary>
            public bool IconPreferred { get; set; }

            /// <summary>Also set windowBackground to the splash colour (risk R4: it changes the settings page's and installer's window background).</summary>
            public bool WindowBackground { get; set; }

            /// <summary>True when night mode needs its own values (a night-v31 configuration).</summary>
            public bool HasNight
            {
                get
                {
                    return BackgroundNight.HasValue && BackgroundNight.Value != Background ||
                           IconBackgroundNight.HasValue && IconBackgroundNight != IconBackground;
                }
            }
        }

        private static readonly string[] TopKeys = { "icon", "splash" };
        private static readonly string[] IconKeys = { "background" };
        private static readonly string[] SplashKeys = { "background", "backgroundNight", "iconBackground", "iconBackgroundNight", "iconPreferred", "windowBackground" };

        public static ArtSettings Parse(byte[] json)
        {
            var settings = new ArtSettings();
            // Some editors (older Windows Notepad) save UTF-8 with a byte-order mark, which JsonDocument does not skip.
            ReadOnlyMemory<byte> text = json.Length >= 3 && json[0] == 0xEF && json[1] == 0xBB && json[2] == 0xBF ? json.AsMemory(3) : json;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException(FileName + ": not valid JSON: " + ex.Message);
            }
            using (doc)
            {
                JsonElement root = doc.RootElement;
                Expect(root, JsonValueKind.Object, FileName);
                CheckKeys(root, TopKeys, "");
                if (root.TryGetProperty("icon", out JsonElement icon) && icon.ValueKind != JsonValueKind.Null)
                {
                    Expect(icon, JsonValueKind.Object, "icon");
                    CheckKeys(icon, IconKeys, "icon.");
                    settings.IconBackground = Color(icon, "background", "icon.");
                }
                if (root.TryGetProperty("splash", out JsonElement splash) && splash.ValueKind != JsonValueKind.Null)
                {
                    Expect(splash, JsonValueKind.Object, "splash");
                    CheckKeys(splash, SplashKeys, "splash.");
                    uint? background = Color(splash, "background", "splash.");
                    if (!background.HasValue) throw new InvalidDataException(FileName + ": splash.background is required when there is a splash section");
                    settings.Splash = new SplashSettings
                    {
                        Background = background.Value,
                        BackgroundNight = Color(splash, "backgroundNight", "splash."),
                        IconBackground = Color(splash, "iconBackground", "splash."),
                        IconBackgroundNight = Color(splash, "iconBackgroundNight", "splash."),
                        IconPreferred = Bool(splash, "iconPreferred", "splash."),
                        WindowBackground = Bool(splash, "windowBackground", "splash."),
                    };
                    if (settings.Splash.IconBackgroundNight.HasValue && !settings.Splash.IconBackground.HasValue)
                        throw new InvalidDataException(FileName + ": splash.iconBackgroundNight needs splash.iconBackground");
                }
            }
            return settings;
        }

        /// <summary>"#RRGGBB" -> 0xRRGGBB.</summary>
        public static uint ParseColor(string text, string what)
        {
            if (text == null || text.Length != 7 || text[0] != '#' ||
                !uint.TryParse(text.Substring(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint rgb))
                throw new InvalidDataException(FileName + ": " + what + " must be a colour \"#RRGGBB\", not " + (text == null ? "null" : "\"" + text + "\""));
            return rgb;
        }

        public static string FormatColor(uint rgb)
        {
            return "#" + rgb.ToString("X6", CultureInfo.InvariantCulture);
        }

        private static uint? Color(JsonElement parent, string key, string prefix)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return null;
            if (value.ValueKind != JsonValueKind.String) throw new InvalidDataException(FileName + ": " + prefix + key + " must be a string \"#RRGGBB\"");
            return ParseColor(value.GetString(), prefix + key);
        }

        private static bool Bool(JsonElement parent, string key, string prefix)
        {
            if (!parent.TryGetProperty(key, out JsonElement value) || value.ValueKind == JsonValueKind.Null) return false;
            if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                throw new InvalidDataException(FileName + ": " + prefix + key + " must be true or false");
            return value.GetBoolean();
        }

        private static void Expect(JsonElement e, JsonValueKind kind, string what)
        {
            if (e.ValueKind != kind) throw new InvalidDataException(FileName + ": " + what + " must be a JSON " + kind.ToString().ToLowerInvariant());
        }

        private static void CheckKeys(JsonElement obj, string[] known, string prefix)
        {
            foreach (JsonProperty p in obj.EnumerateObject())
            {
                if (!known.Contains(p.Name, StringComparer.Ordinal))
                    throw new InvalidDataException(FileName + ": unknown key " + prefix + p.Name + " (known: " + string.Join(", ", known.Select(k => prefix + k)) + ")");
            }
        }
    }
}
