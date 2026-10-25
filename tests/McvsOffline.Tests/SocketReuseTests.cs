using System.Net;
using System.Net.Sockets;
using McvsOffline.Server.Core.Http;
using Xunit;

namespace McvsOffline.Tests
{
    /// <summary>
    /// The libc call the embedded listener makes instead of Socket.SetSocketOption(ReuseAddress), which on Mono (and on
    /// .NET for Linux) also sets SO_REUSEPORT and lets a second listener of the same user share the port.
    /// </summary>
    public class SocketReuseTests
    {
        [Fact]
        public void SetsSoReuseAddrWithoutSoReusePort()
        {
            if (!SocketReuse.IsSupported) return; // the embedded build runs on Android (Linux)
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Assert.Null(SocketReuse.TrySetReuseAddressOnly(socket));
            Assert.NotEqual(0, SocketReuse.GetSocketOption(socket, SocketReuse.SoReuseAddr));
            Assert.Equal(0, SocketReuse.GetSocketOption(socket, SocketReuse.SoReusePort));
        }

        [Fact]
        public void ASecondListenerCannotShareThePortEvenWithSoReusePort()
        {
            if (!SocketReuse.IsSupported) return;
            using var first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            Assert.Null(SocketReuse.TrySetReuseAddressOnly(first));
            first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            first.Listen(8);
            int port = ((IPEndPoint)first.LocalEndPoint).Port;

            // What a second (lingering) game process would do through Mono: ReuseAddress, i.e. SO_REUSEADDR + SO_REUSEPORT.
            using var second = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            second.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            Assert.NotEqual(0, SocketReuse.GetSocketOption(second, SocketReuse.SoReusePort)); // .NET sets it too, like Mono
            Assert.Throws<SocketException>(() => second.Bind(new IPEndPoint(IPAddress.Loopback, port)));
        }

        [Fact]
        public void WithSoReusePortOnBothSidesTheKernelLetsTheSecondListenerIn()
        {
            // The hazard the libc call avoids: the old Socket.SetSocketOption(ReuseAddress) on Mono.
            if (!SocketReuse.IsSupported) return;
            using var first = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            first.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            first.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            first.Listen(8);
            using var second = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            second.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            second.Bind(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)first.LocalEndPoint).Port));
            second.Listen(8);
        }
    }
}
