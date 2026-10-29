using System;

namespace McvsOffline.PcKit
{
    /// <summary>
    /// The PC kit's entry point (docs/design/pc-port.md §9.2). Day-0 skeleton: every command answers "not implemented".
    /// </summary>
    public static class Program
    {
        /// <summary>The line every command prints until WP3 implements the kit.</summary>
        internal const string NotImplemented = "McvsOffline.PcKit: not implemented";

        public static int Main(string[] args)
        {
            Console.WriteLine(NotImplemented);
            return 2;
        }
    }
}
