using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using McvsOffline.Server;
using McvsOffline.Server.Core;
using McvsOffline.Server.Core.Content;
using McvsOffline.Server.Core.Http;
using McvsOffline.Server.Core.Json;
using McvsOffline.Server.Core.Routing;
using McvsOffline.Server.Services;
using McvsOffline.Server.Services.Admin;
using McvsOffline.Tests.Content;
using Xunit;
using Xunit.Abstractions;

namespace McvsOffline.Tests.Admin
{
    /// <summary>
    /// The settings page (server/Content/admin/index.html, WP4 of docs/design/settings-page.md). The static checks of §2.3
    /// always run: one self-contained file, every id of §2, an ES5 lint (a tokenizer that skips strings, comments and
    /// regex literals) and a CSS lint. page-smoke.js then runs the page in node (a DOM with exactly the §2.3 surface and a
    /// cookie-aware XHR) against its fixture API and against a live server with every module. node is optional unless
    /// MCVS_REQUIRE_NODE=1, which makes both node runs mandatory.
    /// </summary>
    public class PageSmokeTests
    {
        public const string PageName = "admin/index.html";

        /// <summary>The fixed ids of design §2 (markup); the dynamic ones are checked by their prefix in the script.</summary>
        public static readonly string[] DesignIds =
        {
            "nav", "nav-title", "conn", "nav-status", "nav-settings", "nav-saves", "nav-logs", "nav-actions", "nav-about", "btn-close",
            "banners", "pane", "actionbar", "dialog", "dialog-body", "dialog-ok", "dialog-cancel", "toast",
            "banner-nosession", "banner-viewmode", "banner-error", "banner-staged", "banner-restart", "banner-probation",
            "screen-status", "screen-settings", "screen-saves", "screen-logs", "screen-actions", "screen-about",
            "status-refresh", "status-storage",
            "settings-save", "settings-discard", "settings-showfile", "settings-repair",
            "saves-current", "saves-backups", "saves-accounts", "saves-save-now", "saves-backup-now",
            "export-share", "export-downloads", "export-copy", "export-partsize", "export-copy-next", "export-text",
            "import-text", "import-add", "import-parts", "import-files", "import-summary", "import-confirm", "import-stage",
            "staged-cancel", "staged-undo", "staged-restart",
            "logs-files", "logs-view", "logs-level", "logs-search", "logs-lines", "logs-autorefresh", "logs-copy",
            "logs-scope", "logs-note", "logs-build", "logs-share", "logs-downloads", "logs-share-text",
            "action-files", "action-restart", "action-crm-reload", "action-unlock-now",
            "about-version", "about-ua",
        };

        /// <summary>Ids the script builds: #grp-&lt;group&gt;, #set-&lt;key&gt;, #preset-&lt;id&gt;, #restore-/#delete-&lt;backup&gt;, #relink-&lt;account&gt;.</summary>
        public static readonly string[] DynamicIdPrefixes = { "grp-", "set-", "preset-", "restore-", "delete-", "relink-" };

        /// <summary>Admin routes the live smoke walks through (WP2 and WP3, design §4.2).</summary>
        public static readonly string[] LiveRoutes =
        {
            "admin.ui", "admin.session.open", "admin.ping", "admin.status", "admin.settings.get", "admin.settings.set", "admin.codes",
            "admin.saves.list", "admin.saves.backups", "admin.saves.export", "admin.saves.importFiles", "admin.saves.paste",
            "admin.saves.stage", "admin.saves.unstage", "admin.logs.list", "admin.logs.tail", "admin.logs.zip",
        };

        private readonly ITestOutputHelper output;

        public PageSmokeTests(ITestOutputHelper output)
        {
            this.output = output;
        }

        internal static string Page
        {
            get
            {
                Assert.True(BuiltInContent.All.TryGetValue(PageName, out byte[] bytes), PageName + " is not compiled into the server");
                return new UTF8Encoding(false, true).GetString(bytes);
            }
        }

        internal static string Script => Regex.Match(Page, @"<script>(.*?)</script>", RegexOptions.Singleline).Groups[1].Value;

        internal static string Style => Regex.Match(Page, @"<style>(.*?)</style>", RegexOptions.Singleline).Groups[1].Value;

        /// <summary>The page without the contents of its script, style and comments.</summary>
        internal static string Markup
        {
            get
            {
                string m = Regex.Replace(Page, @"<script>.*?</script>", "<script></script>", RegexOptions.Singleline);
                m = Regex.Replace(m, @"<style>.*?</style>", "<style></style>", RegexOptions.Singleline);
                return Regex.Replace(m, @"<!--.*?-->", "", RegexOptions.Singleline);
            }
        }

        // ------------------------------------------------------------------ static checks (always)

        [Fact]
        public void PageIsCompiledInFromServerContent()
        {
            string source = File.ReadAllText(Path.Combine(ContentTestSupport.RepoRoot, "server", "Content", "admin", "index.html"), Encoding.UTF8);
            Assert.Equal(source, Page);
            Assert.Contains(PageName, new ContentProvider(null, null, Log.Null).ContentNames);
        }

        [Fact]
        public void PageIsOneSelfContainedFile()
        {
            string page = Page;
            int bytes = Encoding.UTF8.GetByteCount(page);
            output.WriteLine(PageName + ": " + bytes + " bytes");
            Assert.True(bytes <= 200 * 1024, "the page is " + bytes + " bytes; the limit is 200 KB (§2.3)");
            Assert.StartsWith("<!DOCTYPE html>", page);
            Assert.Contains("<meta charset=\"utf-8\">", page);
            Assert.Contains("<title>MCVS Offline</title>", page);

            string markup = Markup;
            Assert.Single(Regex.Matches(markup, @"<script\b", RegexOptions.IgnoreCase));
            Assert.Single(Regex.Matches(markup, @"<style\b", RegexOptions.IgnoreCase));
            Assert.Contains("<script>", markup);
            Assert.Contains("<style>", markup);
            foreach (string tag in new[] { "link", "img", "iframe", "object", "embed", "base", "form", "frame", "audio", "video", "source" })
                Assert.False(Regex.IsMatch(markup, @"<" + tag + @"\b", RegexOptions.IgnoreCase), "<" + tag + "> in the page");
            Assert.False(Regex.IsMatch(markup, @"\ssrc\s*=", RegexOptions.IgnoreCase), "a src= attribute in the page");
            foreach (Match m in Regex.Matches(markup, @"\shref\s*=\s*""([^""]*)""", RegexOptions.IgnoreCase))
                Assert.True(m.Groups[1].Value == "exit:" || m.Groups[1].Value == "#", "href=\"" + m.Groups[1].Value + "\" (only exit: and #)");
            Assert.False(Regex.IsMatch(markup, @"\son[a-z]+\s*=", RegexOptions.IgnoreCase), "an inline event handler attribute");
            Assert.False(Regex.IsMatch(markup, @"type\s*=\s*""?file", RegexOptions.IgnoreCase), "a file input (the WebView has no file chooser)");
            Assert.False(Regex.IsMatch(markup, @"\sdownload\b", RegexOptions.IgnoreCase), "a download link (the WebView cannot download)");

            foreach (string external in new[] { "http://", "https://", "ws://", "wss://", "//cdn", "localhost" })
                Assert.False(page.IndexOf(external, StringComparison.OrdinalIgnoreCase) >= 0, "'" + external + "' in the page (no external URL, 127.0.0.1 only via relative URLs)");
            Assert.Matches(@"<a id=""btn-close""[^>]*href=""exit:""", markup);
        }

        [Fact]
        public void EveryIdOfTheDesignIsPresentOnce()
        {
            string markup = Markup;
            var ids = Regex.Matches(markup, @"\sid=""([^""]+)""").Select(m => m.Groups[1].Value).ToList();
            foreach (string dup in ids.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key))
                Assert.Fail("id '" + dup + "' is used more than once");
            foreach (string id in DesignIds) Assert.True(ids.Contains(id), "#" + id + " (design §2) is missing");
            string script = Script;
            foreach (string prefix in DynamicIdPrefixes) Assert.True(script.Contains("'" + prefix + "'"), "the script never builds #" + prefix + "…");
            var tokens = PageLint.Tokenize(script, new List<string>());
            foreach (var t in tokens.Where(t => t.Kind == PageLint.Kind.String && Regex.IsMatch(t.Text, @"^[a-z]+(-[a-z]+)+$")))
            {
                // Every literal id the script looks up exists in the markup (or is a known dynamic one).
                int k = tokens.IndexOf(t);
                if (k >= 2 && tokens[k - 1].Text == "(" && tokens[k - 2].Text == "$")
                    Assert.True(ids.Contains(t.Text), "the script uses #" + t.Text + ", which the markup does not have");
            }

            // The fixed option sets of §2.2.
            Assert.Equal(new[] { "60000", "3900", "1900" }, OptionsOf(markup, "export-partsize"));
            Assert.Equal(new[] { "trace", "info", "warn", "error" }, OptionsOf(markup, "logs-level"));
            Assert.Equal(new[] { "200", "500", "2000" }, OptionsOf(markup, "logs-lines"));
            Assert.Equal(new[] { "recent", "all" }, OptionsOf(markup, "logs-scope"));
        }

        private static string[] OptionsOf(string markup, string id)
        {
            var select = Regex.Match(markup, @"<select id=""" + id + @"""[^>]*>(.*?)</select>", RegexOptions.Singleline);
            Assert.True(select.Success, "<select id=\"" + id + "\"> missing");
            return Regex.Matches(select.Groups[1].Value, @"<option value=""([^""]*)""").Select(m => m.Groups[1].Value).ToArray();
        }

        [Fact]
        public void ScriptIsEs5()
        {
            var problems = PageLint.LintScript(Script);
            foreach (string p in problems) output.WriteLine(p);
            Assert.True(problems.Count == 0, "ES5 lint (§2.3):\n" + string.Join("\n", problems));
        }

        [Fact]
        public void CssFollowsTheRules()
        {
            var problems = PageLint.LintCss(Style);
            foreach (Match m in Regex.Matches(Markup, @"\sstyle=""([^""]*)""")) problems.AddRange(PageLint.LintDeclarations("style attribute", m.Groups[1].Value));
            foreach (string p in problems) output.WriteLine(p);
            Assert.True(problems.Count == 0, "CSS lint (§2.3):\n" + string.Join("\n", problems));
            Assert.True(PageLint.HasTouchManipulation(Style), "no rule gives every control touch-action: manipulation (§2.1)");
        }

        [Theory]
        [InlineData("let a = 1;", "'let'")]
        [InlineData("const a = 1;", "'const'")]
        [InlineData("class A {}", "'class'")]
        [InlineData("var f = (a) => a;", "'=>'")]
        [InlineData("var s = `x${a}`;", "template literal")]
        [InlineData("f(...args);", "'...'")]
        [InlineData("for (var x of xs) {}", "for-of")]
        [InlineData("async function f() { await g(); }", "'async'")]
        [InlineData("function f(a, b = 2) {}", "default parameter")]
        [InlineData("var o = { m() { return 1; } };", "shorthand method")]
        [InlineData("new Promise(function () {});", "'Promise'")]
        [InlineData("fetch('/x');", "'fetch'")]
        [InlineData("var m = new Map();", "'Map'")]
        [InlineData("var s = new Set();", "'Set'")]
        [InlineData("var y = Symbol('x');", "'Symbol'")]
        [InlineData("Object.assign({}, a);", "Object.assign")]
        [InlineData("Object.entries(a);", "Object.entries")]
        [InlineData("Object.values(a);", "Object.values")]
        [InlineData("Array.from(a);", "Array.from")]
        [InlineData("s.includes('x');", ".includes")]
        [InlineData("s.startsWith('x');", ".startsWith")]
        [InlineData("s.repeat(2);", ".repeat")]
        [InlineData("s.padStart(2, '0');", ".padStart")]
        [InlineData("a.find(function () {});", ".find")]
        [InlineData("a.findIndex(function () {});", ".findIndex")]
        [InlineData("a.fill(0);", ".fill")]
        [InlineData("new URLSearchParams(q);", "'URLSearchParams'")]
        [InlineData("localStorage.x = 1;", "'localStorage'")]
        [InlineData("sessionStorage.x = 1;", "'sessionStorage'")]
        [InlineData("indexedDB.open('x');", "'indexedDB'")]
        [InlineData("alert('x');", "'alert'")]
        [InlineData("if (confirm('x')) {}", "'confirm'")]
        [InlineData("prompt('x');", "'prompt'")]
        [InlineData("eval('1');", "'eval'")]
        [InlineData("var f = new Function('return 1');", "'Function'")]
        [InlineData("el.innerHTML = '<b>x</b>';", "innerHTML")]
        [InlineData("var h = el.innerHTML;", "innerHTML")]
        [InlineData("document.write('x');", "document.write")]
        [InlineData("p.then(f);", ".then")]
        [InlineData("var a = b ?? c;", "'??'")]
        [InlineData("var a = b?.c;", "'?.'")]
        [InlineData("var a = 2 ** 3;", "'**'")]
        [InlineData("var r = /x/u;", "regex flag")]
        [InlineData("var n = 0b101;", "binary or octal")]
        [InlineData("var n = 1_000;", "numeric separator")]
        [InlineData("var s = '\\u{1F600}';", "\\u{")]
        [InlineData("f(a, b,);", "trailing comma")]
        [InlineData("el.setAttribute('src', 'x.png');", "src")]
        [InlineData("el.setAttribute('href', 'http://x');", "href")]
        public void Es5LintRejects(string script, string expected)
        {
            var problems = PageLint.LintScript(script);
            Assert.True(problems.Any(p => p.Contains(expected)), "expected a problem with " + expected + ", got: " + string.Join(" | ", problems));
        }

        [Theory]
        [InlineData("var s = 'let const class => ... `x` fetch Promise ** ?? ?.';")]
        [InlineData("// let x = () => 1; fetch(`x`)\nvar a = 1; /* const b = 2; Map */")]
        [InlineData("var r = /=>|`|let/g; var d = a / b / c; var e = (a) / 2;")]
        [InlineData("var o = { confirm: true, 'default': 1, find: 2, values: {} }; o.confirm = o['default']; x.at = o.values; el.innerHTML = '';")]
        [InlineData("if (navigator.clipboard && typeof navigator.clipboard.writeText === 'function') { navigator.clipboard.writeText(t).then(ok, fail); }")]
        [InlineData("var x = { get y() { return 1; } }; for (var k in x) { if (x.hasOwnProperty(k)) {} }")]
        [InlineData("var c = a ? .5 : b; var h = 0x1F; var e = 1e-3;")]
        [InlineData("link.setAttribute('href', API + 'logs/bundle'); a.setAttribute('href', 'exit:');")]
        public void Es5LintAccepts(string script)
        {
            var problems = PageLint.LintScript(script);
            Assert.True(problems.Count == 0, string.Join(" | ", problems));
        }

        [Theory]
        [InlineData("a { color: var(--x); }", "custom propert")]
        [InlineData(":root { --x: 1px; }", "custom propert")]
        [InlineData("a { display: grid; }", "grid")]
        [InlineData("a { grid-template-columns: 1fr; }", "grid")]
        [InlineData("a { position: sticky; top: 0; }", "sticky")]
        [InlineData("a { display: flex; gap: 4px; }", "gap")]
        [InlineData("a { min-height: 50vh; }", "viewport unit")]
        [InlineData("p { height: 20px; }", "fixed height")]
        [InlineData("a { background: url(http://x/y.png); }", "url(")]
        [InlineData("@import 'x.css';", "@import")]
        [InlineData("a:has(b) { color: red; }", ":has(")]
        public void CssLintRejects(string css, string expected)
        {
            var problems = PageLint.LintCss(css);
            Assert.True(problems.Any(p => p.Contains(expected)), "expected a problem with " + expected + ", got: " + string.Join(" | ", problems));
        }

        [Fact]
        public void CssLintAccepts()
        {
            Assert.Empty(PageLint.LintCss("html, body { height: 100%; } /* height: 5px; var(--x) */ p { min-height: 20px; line-height: 1.2; max-height: 100%; } "
                                          + "a { display: flex; flex: 1 1 auto; } b { background: url(data:image/png;base64,AAAA); height: auto; }"));
        }

        // ------------------------------------------------------------------ node smoke

        [Fact]
        public void NodeSmokeAgainstTheFixtureApi()
        {
            string node = FindNode();
            if (node == null) return;
            using var dir = new TempDir();
            string page = dir.Combine("index.html");
            File.WriteAllBytes(page, BuiltInContent.All[PageName]);
            var result = RunNode(node, SmokeScript, "--fixtures", page);
            output.WriteLine(result.Item2);
            Assert.True(result.Item1 == 0 && result.Item2.Contains("SMOKE OK"), "page-smoke.js --fixtures failed:\n" + result.Item2);
        }

        /// <summary>
        /// The smoke against a real server with every module of ServiceRegistry (the admin modules included), listening on a
        /// free port, as the game's WebView sees it. Mandatory with MCVS_REQUIRE_NODE=1 (scripts/build.sh's full check):
        /// nothing here skips except a missing node when it is not required.
        /// </summary>
        [Fact]
        public void NodeSmokeAgainstALiveServer()
        {
            string node = FindNode();
            if (node == null) return;
            using var dir = new TempDir();
            using McvsServer live = McvsServer.Create(new ServerOptions
            {
                DataDir = dir.Combine("data"),
                Port = 0,
                ConsoleLog = false,
                FileLog = true,
                RegisterServices = ServiceRegistry.RegisterAll,
            });
            var have = new HashSet<string>(live.Router.Routes.Select(r => r.Name), StringComparer.Ordinal);
            Assert.Empty(LiveRoutes.Where(r => !have.Contains(r)));
            live.Start();
            SeedPlayer(live);
            IAdminPortal portal = live.Context.Features.Get<IAdminPortal>();
            Assert.NotNull(portal);
            string url = portal.MintPageUrl(AdminTriggers.Test, null);
            Assert.StartsWith("http://127.0.0.1:" + live.Port + "/_mcvs/ui/#t=", url);

            var result = RunNode(node, SmokeScript, url);
            output.WriteLine(result.Item2);
            Assert.True(result.Item1 == 0 && result.Item2.Contains("SMOKE OK"), "page-smoke.js against the live server failed:\n" + result.Item2);

            // The page's Save reached settings.json (the smoke turned instantBots off).
            var settings = MiniJson.ParseObject(File.ReadAllText(Path.Combine(live.Context.DataDir, Settings.FileName)));
            Assert.Equal(false, settings["instantBots"]);
        }

        /// <summary>
        /// A player as the game makes one: a direct anonymous grant of the game's client and a stored game profile. The
        /// dump carries random text so its export needs several Discord-sized parts (the smoke pastes them back in two goes).
        /// </summary>
        private static void SeedPlayer(McvsServer server)
        {
            const string credential = "anonymous:pagesmoke0123456789";
            var account = server.Context.Accounts.GetOrCreate(credential, "page-smoke-device");
            string token = server.Context.Tokens.Issue(account.Id, credential, McvsConstants.ClientId, new[] { "auth", "storage" }, "page-smoke-device",
                                                        playerLogin: true).Token;
            var dump = new StringBuilder("{\"@version\":\"0.1\",\"$default\":{\"/Profile\":{\"@type\":\"Game.PlayerProfileComponent\",\"Version\":57,\"GameVersion\":\"1.17.32\",\"PlayerName\":\"smoke\",\"Items\":[");
            for (int i = 0; i < 300; i++) dump.Append(i == 0 ? "" : ",").Append('"').Append(Convert.ToHexString(RandomNumberGenerator.GetBytes(16))).Append('"');
            dump.Append("]}}}");
            string form = "visibility=restricted&object=" + UrlEncoding.Encode(dump.ToString()) + "&access_token=" + token;
            var response = server.Handle(HttpRequest.Create("POST", "/profiles/me/myprofile/_mcvs.gameProfile", form));
            Assert.Equal(200, response.StatusCode);
        }

        private static string SmokeScript => Path.Combine(ContentTestSupport.RepoRoot, "tests", "McvsOffline.Tests", "Admin", "page-smoke.js");

        private static bool NodeRequired => Environment.GetEnvironmentVariable("MCVS_REQUIRE_NODE") == "1";

        private string FindNode()
        {
            foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (dir.Length == 0) continue;
                foreach (string name in new[] { "node", "node.exe" })
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
            Assert.False(NodeRequired, "MCVS_REQUIRE_NODE=1 but node is not on PATH");
            output.WriteLine("node is not on PATH: the page smoke is skipped (the static checks still ran)");
            return null;
        }

        private static Tuple<int, string> RunNode(string node, params string[] args)
        {
            var psi = new ProcessStartInfo(node)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                WorkingDirectory = ContentTestSupport.RepoRoot,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(180000))
            {
                try { p.Kill(true); } catch (Exception) { }
                return Tuple.Create(-1, "timed out after 180 s\n" + stdout.Result + stderr.Result);
            }
            return Tuple.Create(p.ExitCode, stdout.Result + stderr.Result);
        }
    }

    /// <summary>
    /// The lints of design §2.3: an ES5 lint over a small JavaScript tokenizer (strings, comments, regex literals and
    /// template literals are tokens of their own, so their contents never count) and a CSS lint over declarations.
    /// </summary>
    internal static class PageLint
    {
        internal enum Kind { Ident, Punct, String, Number, Regex, Template }

        internal sealed class Token
        {
            public Token(Kind kind, string text, int line)
            {
                Kind = kind;
                Text = text;
                Line = line;
            }

            public Kind Kind { get; }

            public string Text { get; }

            public int Line { get; }

            public override string ToString() => Text;
        }

        private static readonly string[] Punctuators =
        {
            ">>>=", "...", "===", "!==", "**=", "<<=", ">>=", ">>>", "&&=", "||=", "??=", "=>", "**", "?.", "??", "==", "!=", "<=", ">=",
            "&&", "||", "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "<<", ">>",
            "{", "}", "(", ")", "[", "]", ";", ",", "<", ">", "+", "-", "*", "/", "%", "&", "|", "^", "!", "~", "?", ":", "=", ".", "#", "@",
        };

        private static readonly HashSet<string> BannedPunctuators = new HashSet<string>(StringComparer.Ordinal)
        {
            "=>", "...", "?.", "??", "??=", "**", "**=", "&&=", "||=", "#", "@",
        };

        private static readonly HashSet<string> BannedKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "let", "const", "class", "async", "await", "yield", "import", "export", "extends", "super",
        };

        private static readonly HashSet<string> BannedGlobals = new HashSet<string>(StringComparer.Ordinal)
        {
            "Promise", "fetch", "Map", "Set", "WeakMap", "WeakSet", "Symbol", "Proxy", "Reflect", "BigInt", "globalThis",
            "URLSearchParams", "Intl", "localStorage", "sessionStorage", "indexedDB", "alert", "confirm", "prompt", "eval", "Function",
        };

        /// <summary>Methods newer than ES5 (banned when called, whatever the object).</summary>
        private static readonly HashSet<string> BannedCalls = new HashSet<string>(StringComparer.Ordinal)
        {
            "includes", "startsWith", "endsWith", "repeat", "padStart", "padEnd", "trimStart", "trimEnd", "codePointAt", "normalize",
            "find", "findIndex", "findLast", "findLastIndex", "fill", "copyWithin", "flat", "flatMap", "at", "replaceAll", "matchAll",
        };

        private static readonly Dictionary<string, HashSet<string>> BannedStatics = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["Object"] = new HashSet<string>(StringComparer.Ordinal) { "assign", "entries", "values", "fromEntries", "is", "getOwnPropertySymbols", "setPrototypeOf" },
            ["Array"] = new HashSet<string>(StringComparer.Ordinal) { "from", "of" },
            ["Number"] = new HashSet<string>(StringComparer.Ordinal) { "isNaN", "isInteger", "isFinite", "isSafeInteger", "EPSILON", "parseFloat", "parseInt" },
            ["Math"] = new HashSet<string>(StringComparer.Ordinal) { "trunc", "sign", "cbrt", "hypot", "log10", "log2", "imul", "fround", "clz32" },
            ["String"] = new HashSet<string>(StringComparer.Ordinal) { "raw", "fromCodePoint" },
            ["document"] = new HashSet<string>(StringComparer.Ordinal) { "write", "writeln" },
        };

        private static readonly HashSet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "catch", "with", "function", "return", "typeof", "instanceof", "in", "new", "delete", "void",
            "throw", "case", "do", "else", "var", "try", "finally", "break", "continue", "default", "this", "null", "true", "false",
        };

        private static readonly HashSet<string> RegexAfterWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "return", "typeof", "instanceof", "in", "of", "new", "delete", "void", "throw", "case", "do", "else",
        };

        private static string At(int line) => "line " + line + ": ";

        private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '$';

        private static bool IsIdentPart(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '$';

        private static bool RegexAllowed(List<Token> tokens)
        {
            if (tokens.Count == 0) return true;
            Token prev = tokens[tokens.Count - 1];
            if (prev.Kind == Kind.Punct) return prev.Text != ")" && prev.Text != "]" && prev.Text != "}";
            return prev.Kind == Kind.Ident && RegexAfterWords.Contains(prev.Text);
        }

        public static List<Token> Tokenize(string src, List<string> problems)
        {
            var tokens = new List<Token>();
            int i = 0, line = 1, n = src.Length;
            while (i < n)
            {
                char c = src[i];
                if (c == '\n')
                {
                    line++;
                    i++;
                    continue;
                }
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && src[i + 1] == '/')
                {
                    while (i < n && src[i] != '\n') i++;
                    continue;
                }
                if (c == '/' && i + 1 < n && src[i + 1] == '*')
                {
                    int end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0)
                    {
                        problems.Add(At(line) + "unterminated comment");
                        break;
                    }
                    for (int k = i; k < end; k++) if (src[k] == '\n') line++;
                    i = end + 2;
                    continue;
                }
                int startLine = line;
                if (c == '"' || c == '\'')
                {
                    int s = ++i;
                    while (i < n && src[i] != c)
                    {
                        if (src[i] == '\\')
                        {
                            if (i + 2 < n && src[i + 1] == 'u' && src[i + 2] == '{') problems.Add(At(line) + "\\u{…} escape (ES2015)");
                            if (i + 1 < n && src[i + 1] == '\n') line++;
                            i += 2;
                            continue;
                        }
                        if (src[i] == '\n')
                        {
                            problems.Add(At(line) + "unterminated string");
                            break;
                        }
                        i++;
                    }
                    tokens.Add(new Token(Kind.String, src.Substring(s, Math.Min(i, n) - s), startLine));
                    i++;
                    continue;
                }
                if (c == '`')
                {
                    problems.Add(At(line) + "template literal (ES2015)");
                    i++;
                    while (i < n && src[i] != '`')
                    {
                        if (src[i] == '\\') i++;
                        else if (src[i] == '\n') line++;
                        i++;
                    }
                    i++;
                    tokens.Add(new Token(Kind.Template, "`…`", startLine));
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && i + 1 < n && char.IsDigit(src[i + 1])))
                {
                    int s = i;
                    bool hex = c == '0' && i + 1 < n && (src[i + 1] == 'x' || src[i + 1] == 'X');
                    while (i < n && (char.IsLetterOrDigit(src[i]) || src[i] == '.' || src[i] == '_'
                                     || (!hex && (src[i] == '+' || src[i] == '-') && (src[i - 1] == 'e' || src[i - 1] == 'E')))) i++;
                    string num = src.Substring(s, i - s);
                    if (num.Length > 1 && num[0] == '0' && "bBoO".IndexOf(num[1]) >= 0) problems.Add(At(line) + "binary or octal literal " + num + " (ES2015)");
                    if (num.IndexOf('_') >= 0) problems.Add(At(line) + "numeric separator in " + num + " (ES2021)");
                    if (!hex && num.EndsWith("n", StringComparison.Ordinal)) problems.Add(At(line) + "BigInt literal " + num);
                    tokens.Add(new Token(Kind.Number, num, startLine));
                    continue;
                }
                if (IsIdentStart(c))
                {
                    int s = i;
                    while (i < n && IsIdentPart(src[i])) i++;
                    tokens.Add(new Token(Kind.Ident, src.Substring(s, i - s), startLine));
                    continue;
                }
                if (c == '/' && RegexAllowed(tokens))
                {
                    int s = i++;
                    bool inClass = false;
                    while (i < n)
                    {
                        char d = src[i];
                        if (d == '\\')
                        {
                            i += 2;
                            continue;
                        }
                        if (d == '\n')
                        {
                            problems.Add(At(line) + "unterminated regex literal");
                            break;
                        }
                        if (inClass)
                        {
                            if (d == ']') inClass = false;
                        }
                        else if (d == '[') inClass = true;
                        else if (d == '/') break;
                        i++;
                    }
                    i++;
                    int f = i;
                    while (i < n && IsIdentPart(src[i])) i++;
                    string flags = src.Substring(f, i - f);
                    if (flags.Trim('g', 'i', 'm').Length > 0) problems.Add(At(line) + "regex flag '" + flags + "' (ES5 has g, i, m)");
                    tokens.Add(new Token(Kind.Regex, src.Substring(s, i - s), startLine));
                    continue;
                }
                string punct = null;
                foreach (string p in Punctuators)
                {
                    if (string.CompareOrdinal(src, i, p, 0, p.Length) != 0) continue;
                    if (p == "?." && i + 2 < n && char.IsDigit(src[i + 2])) continue; // a ? .5 : b
                    punct = p;
                    break;
                }
                if (punct == null)
                {
                    problems.Add(At(line) + "unexpected character '" + c + "'");
                    i++;
                    continue;
                }
                tokens.Add(new Token(Kind.Punct, punct, startLine));
                i += punct.Length;
            }
            return tokens;
        }

        /// <summary>Index of the bracket closing the one at <paramref name="open"/>, or the last token.</summary>
        private static int Closing(List<Token> t, int open)
        {
            string o = t[open].Text;
            string c = o == "(" ? ")" : o == "[" ? "]" : "}";
            int depth = 0;
            for (int k = open; k < t.Count; k++)
            {
                if (t[k].Kind != Kind.Punct) continue;
                if (t[k].Text == o) depth++;
                else if (t[k].Text == c && --depth == 0) return k;
            }
            return t.Count - 1;
        }

        private static bool Is(List<Token> t, int k, string text) => k >= 0 && k < t.Count && t[k].Kind != Kind.String && t[k].Text == text;

        public static List<string> LintScript(string src)
        {
            var problems = new List<string>();
            var t = Tokenize(src, problems);
            bool clipboardFeatureTest = false;
            for (int k = 0; k + 6 < t.Count; k++)
            {
                if (Is(t, k, "typeof") && Is(t, k + 1, "navigator") && Is(t, k + 2, ".") && Is(t, k + 3, "clipboard") && Is(t, k + 4, ".") && Is(t, k + 5, "writeText"))
                    clipboardFeatureTest = true;
            }
            for (int k = 0; k < t.Count; k++)
            {
                Token tok = t[k];
                string where = At(tok.Line);
                if (tok.Kind == Kind.Punct)
                {
                    if (BannedPunctuators.Contains(tok.Text)) problems.Add(where + "'" + tok.Text + "' (newer than ES5)");
                    if (tok.Text == "," && Is(t, k + 1, ")")) problems.Add(where + "trailing comma in a call or parameter list (ES2017)");
                    continue;
                }
                if (tok.Kind != Kind.Ident) continue;
                bool member = Is(t, k - 1, ".");
                bool propertyKey = !member && Is(t, k + 1, ":") && (Is(t, k - 1, "{") || Is(t, k - 1, ","));
                bool call = Is(t, k + 1, "(");
                if (member)
                {
                    string owner = k >= 2 && t[k - 2].Kind == Kind.Ident ? t[k - 2].Text : null;
                    if (call && BannedCalls.Contains(tok.Text)) problems.Add(where + "." + tok.Text + "() (newer than ES5)");
                    if (owner != null && BannedStatics.TryGetValue(owner, out var statics) && statics.Contains(tok.Text))
                        problems.Add(where + owner + "." + tok.Text + " (not allowed, §2.3)");
                    if (tok.Text == "innerHTML")
                    {
                        bool clearing = Is(t, k + 1, "=") && k + 2 < t.Count && t[k + 2].Kind == Kind.String && t[k + 2].Text.Length == 0
                                        && (k + 3 >= t.Count || Is(t, k + 3, ";") || Is(t, k + 3, ",") || Is(t, k + 3, ")") || Is(t, k + 3, "}"));
                        if (!clearing) problems.Add(where + "innerHTML other than el.innerHTML = '' (§2.3)");
                    }
                    if (tok.Text == "outerHTML" || tok.Text == "insertAdjacentHTML") problems.Add(where + tok.Text + " (§2.3)");
                    if (tok.Text == "then")
                    {
                        bool afterWriteText = false;
                        for (int b = Math.Max(0, k - 30); b < k; b++) if (Is(t, b, "writeText")) afterWriteText = true;
                        if (!(clipboardFeatureTest && afterWriteText)) problems.Add(where + ".then outside the navigator.clipboard feature test (§2.3)");
                    }
                    if (tok.Text == "setAttribute" && call && k + 2 < t.Count && t[k + 2].Kind == Kind.String)
                    {
                        string attr = t[k + 2].Text.ToLowerInvariant();
                        Token value = k + 4 < t.Count ? t[k + 4] : null;
                        if (attr == "src") problems.Add(where + "setAttribute('src', …): no external resources (§2.3)");
                        if (attr == "href" && !(value != null && ((value.Kind == Kind.Ident && value.Text == "API") || (value.Kind == Kind.String && (value.Text == "exit:" || value.Text == "#")))))
                            problems.Add(where + "setAttribute('href', …) to something other than exit:, # or an API path");
                        if (attr == "download" || attr.StartsWith("on", StringComparison.Ordinal)) problems.Add(where + "setAttribute('" + attr + "', …)");
                        if (attr == "type" && value != null && value.Kind == Kind.String && value.Text == "file") problems.Add(where + "a file input (no file chooser, D4)");
                    }
                    continue;
                }
                if (propertyKey) continue;
                if (BannedKeywords.Contains(tok.Text)) problems.Add(where + "'" + tok.Text + "' (newer than ES5)");
                if (BannedGlobals.Contains(tok.Text)) problems.Add(where + "'" + tok.Text + "' (not allowed, §2.3)");
                if (tok.Text == "for" && call)
                {
                    int close = Closing(t, k + 1);
                    int depth = 0;
                    for (int j = k + 1; j < close; j++)
                    {
                        if (Is(t, j, "(") || Is(t, j, "[") || Is(t, j, "{")) depth++;
                        else if (Is(t, j, ")") || Is(t, j, "]") || Is(t, j, "}")) depth--;
                        else if (depth == 1 && t[j].Kind == Kind.Ident && t[j].Text == "of" && !Is(t, j - 1, ".")) problems.Add(where + "for-of (ES2015)");
                    }
                }
                if (tok.Text == "function")
                {
                    int open = t.Count > k + 1 && t[k + 1].Kind == Kind.Ident ? k + 2 : k + 1;
                    if (Is(t, open, "("))
                    {
                        int close = Closing(t, open);
                        for (int j = open + 1; j < close; j++)
                        {
                            if (Is(t, j, "=")) problems.Add(where + "default parameter (ES2015)");
                            if (Is(t, j, "{") || Is(t, j, "[")) problems.Add(where + "destructuring parameter (ES2015)");
                        }
                    }
                }
                if (call && !Keywords.Contains(tok.Text) && (Is(t, k - 1, "{") || Is(t, k - 1, ",")))
                {
                    int close = Closing(t, k + 1);
                    if (Is(t, close + 1, "{")) problems.Add(where + "shorthand method '" + tok.Text + "() {…}' (ES2015)");
                }
            }
            return problems;
        }

        // ------------------------------------------------------------------ CSS

        public static List<string> LintCss(string css)
        {
            var problems = new List<string>();
            css = Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (Match at in Regex.Matches(css, @"@(import|font-face|supports|container|layer|property)\b"))
                problems.Add("@" + at.Groups[1].Value + " (§2.3: one inline style, nothing external)");
            foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                string selector = rule.Groups[1].Value.Trim();
                foreach (string pseudo in new[] { ":has(", ":is(", ":where(" })
                    if (selector.Contains(pseudo)) problems.Add(selector + ": " + pseudo + ") selector (too new for old WebViews)");
                problems.AddRange(LintDeclarations(selector, rule.Groups[2].Value));
            }
            return problems;
        }

        public static List<string> LintDeclarations(string where, string declarations)
        {
            var problems = new List<string>();
            foreach (string decl in declarations.Split(';'))
            {
                int colon = decl.IndexOf(':');
                if (colon < 0) continue;
                string name = decl.Substring(0, colon).Trim().ToLowerInvariant();
                string value = decl.Substring(colon + 1).Trim().ToLowerInvariant();
                string at = where + " { " + name + ": " + value + " }: ";
                if (name.StartsWith("--", StringComparison.Ordinal) || value.Contains("var(")) problems.Add(at + "custom properties (§2.3)");
                if (name.StartsWith("grid", StringComparison.Ordinal) || (name == "display" && value.Contains("grid"))) problems.Add(at + "grid (§2.3)");
                if (name == "position" && value.Contains("sticky")) problems.Add(at + "position: sticky (§2.3)");
                if (name == "gap" || name == "row-gap" || name == "column-gap") problems.Add(at + "flex gap (§2.3)");
                if (Regex.IsMatch(value, @"\d(vh|vw|vmin|vmax|dvh|svh|lvh|dvw|svw|lvw)\b")) problems.Add(at + "viewport unit (§2.3)");
                if (name == "height" && !(value.EndsWith("%", StringComparison.Ordinal) || value == "auto" || value == "0"))
                    problems.Add(at + "fixed height (text rows use min-height, §2.3)");
                if (value.Contains("url(") && !Regex.IsMatch(value, @"url\(\s*[""']?data:")) problems.Add(at + "url( to an external resource (§2.1)");
                if (name == "inset" || name == "aspect-ratio" || value.Contains("env(")) problems.Add(at + "too new for old WebViews");
            }
            return problems;
        }

        /// <summary>A rule covering buttons, inputs, selects and textareas sets touch-action: manipulation (§2.1).</summary>
        public static bool HasTouchManipulation(string css)
        {
            foreach (Match rule in Regex.Matches(css, @"([^{}]+)\{([^{}]*)\}"))
            {
                var selectors = new HashSet<string>(rule.Groups[1].Value.Split(',').Select(s => s.Trim()), StringComparer.Ordinal);
                if (selectors.Contains("button") && selectors.Contains("input") && selectors.Contains("select") && selectors.Contains("textarea")
                    && Regex.IsMatch(rule.Groups[2].Value, @"touch-action\s*:\s*manipulation"))
                    return true;
            }
            return false;
        }
    }
}
