using System;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace McvsOffline.Server.Core.Http
{
    /// <summary>
    /// SO_REUSEADDR without SO_REUSEPORT on a listening socket, set through libc.
    /// <para>
    /// The embedded listener wants SO_REUSEADDR so a restarted game can bind 127.0.0.1:51780 again while its previous
    /// connections sit in TIME_WAIT (up to 60 s on Linux). <c>Socket.SetSocketOption(ReuseAddress)</c> cannot be used
    /// for that: Mono 2020-02, the game's runtime, also sets SO_REUSEPORT on every stream or datagram socket when
    /// SO_REUSEADDR is requested (mono/metadata/w32socket-unix.c mono_w32socket_setsockopt, "BSD's and MacOS X multicast
    /// sockets also need SO_REUSEPORT"; present in the shipped lib/arm64-v8a/libmonosgen-2.0.so at 0x11d878-0x11d918).
    /// With SO_REUSEPORT another socket of the same UID that also sets it (a lingering second game process) could bind
    /// the same port and the kernel would split the game's connections between the two. .NET on Linux maps ReuseAddress
    /// to both options as well, which is why the net8 build sets neither (HttpServer.Start).
    /// </para>
    /// The values are Linux's (asm-generic), the same on every Android ABI: SOL_SOCKET 1, SO_REUSEADDR 2,
    /// SO_REUSEPORT 15. On a Mono Unix socket <see cref="Socket.Handle"/> is the file descriptor.
    /// The libc code is compiled into the Android DLL and the net8 build only; the PC DLL keeps the API without a libc
    /// import (docs/design/pc-port.md §3.1) and never calls it (HttpServer sets the option under MCVS_ANDROID).
    /// </summary>
    public static class SocketReuse
    {
        internal const int SolSocket = 1;
        internal const int SoReuseAddr = 2;
        internal const int SoReusePort = 15;

        /// <summary>
        /// True on Unix. The constants are Linux's; on another Unix the call fails with an error and nothing changes.
        /// </summary>
        public static bool IsSupported
        {
            get { return Environment.OSVersion.Platform == PlatformID.Unix; }
        }

        /// <summary>
        /// Sets SO_REUSEADDR=1 (and nothing else) on <paramref name="socket"/> before it is bound. Returns null on
        /// success, otherwise why it was not set (never throws: a missing option only makes a quick restart wait).
        /// </summary>
        public static string TrySetReuseAddressOnly(Socket socket)
        {
            if (socket == null) throw new ArgumentNullException("socket");
#if MCVS_ANDROID || !MCVS_EMBEDDED
            if (!IsSupported) return "not Unix";
            try
            {
                int one = 1;
                if (NativeMethods.setsockopt(Descriptor(socket), SolSocket, SoReuseAddr, ref one, 4) != 0)
                    return "setsockopt(SOL_SOCKET, SO_REUSEADDR) failed, errno " + Marshal.GetLastWin32Error();
                return null;
            }
            catch (Exception ex)
            {
                // DllNotFoundException / EntryPointNotFoundException: no usable libc.
                return ex.GetType().Name + ": " + ex.Message;
            }
#else
            return "not supported on this build";
#endif
        }

        /// <summary>The value of a SOL_SOCKET option (tests and diagnostics), or -1 when it cannot be read.</summary>
        public static int GetSocketOption(Socket socket, int option)
        {
#if MCVS_ANDROID || !MCVS_EMBEDDED
            if (!IsSupported) return -1;
            try
            {
                int value = 0;
                int length = 4;
                return NativeMethods.getsockopt(Descriptor(socket), SolSocket, option, ref value, ref length) == 0 ? value : -1;
            }
            catch (Exception)
            {
                return -1;
            }
#else
            return -1;
#endif
        }

#if MCVS_ANDROID || !MCVS_EMBEDDED
        private static int Descriptor(Socket socket)
        {
            return socket.Handle.ToInt32();
        }

        private static class NativeMethods
        {
            [DllImport("libc", SetLastError = true)]
            public static extern int setsockopt(int sockfd, int level, int optname, ref int optval, int optlen);

            [DllImport("libc", SetLastError = true)]
            public static extern int getsockopt(int sockfd, int level, int optname, ref int optval, ref int optlen);
        }
#endif
    }
}
