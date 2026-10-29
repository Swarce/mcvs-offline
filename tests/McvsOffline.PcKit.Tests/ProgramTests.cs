using System;
using System.IO;
using Xunit;

namespace McvsOffline.PcKit.Tests
{
    /// <summary>Day-0 placeholder: the skeleton kit runs and says it is not implemented yet.</summary>
    public class ProgramTests
    {
        [Fact]
        public void TheSkeletonSaysNotImplemented()
        {
            TextWriter original = Console.Out;
            var output = new StringWriter();
            Console.SetOut(output);
            try
            {
                Assert.Equal(2, Program.Main(new string[0]));
            }
            finally
            {
                Console.SetOut(original);
            }
            Assert.Contains("not implemented", output.ToString());
        }
    }
}
