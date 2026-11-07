using System;
using System.Runtime.InteropServices;
using System.Text;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The Windows ANSI code page (<c>GetACP</c>: 1252 on a Portuguese or English Windows), the encoding of the game's
    /// <c>OutputDebugString</c> text (OutputDebugStringW converts to it, so the DBWIN buffer is never UTF-8) and of what
    /// <c>wevtutil</c> writes to a pipe. .NET 8 has only UTF-8/16/32, ASCII and Latin-1 built in, so the code-page table
    /// (<see cref="CodePagesEncodingProvider"/>) is registered first. Off Windows, or when the code page cannot be loaded,
    /// it is Latin-1 (every byte stays one character; no U+FFFD). WR2: "Refer�ncia" in game-debug, "Informaþ§es" in
    /// windows-events.txt.
    /// </summary>
    public static class AnsiCodePage
    {
        private static Encoding cached;

        /// <summary>The ANSI code page's encoding (never null).</summary>
        public static Encoding Current
        {
            get
            {
                Encoding e = cached;
                if (e != null) return e;
                e = ForCodePage(OperatingSystem.IsWindows() ? SafeGetAcp() : 0);
                cached = e;
                return e;
            }
        }

        /// <summary>The encoding of <paramref name="codePage"/> through the code-page table; Latin-1 when it is 0 or unknown.</summary>
        public static Encoding ForCodePage(int codePage)
        {
            if (codePage > 0)
            {
                try
                {
                    RegisterCodePages();
                    return Encoding.GetEncoding(codePage);
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException)
                {
                }
            }
            return Encoding.Latin1;
        }

        /// <summary>
        /// Makes the Windows code pages (1252, 850, ...) available to <see cref="Encoding.GetEncoding(int)"/>, so a tool's output
        /// read with the console's OEM code page (Process' default) decodes too. Idempotent.
        /// </summary>
        public static void RegisterCodePages()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }

        private static int SafeGetAcp()
        {
            try
            {
                return (int)GetACP();
            }
            catch (Exception ex) when (ex is DllNotFoundException || ex is EntryPointNotFoundException)
            {
                return 0;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern uint GetACP();
    }
}
