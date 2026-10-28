using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace McvsOffline.PcSpike
{
    /// <summary>
    /// One stop request for 'start' and 'telemetry', from Ctrl+C, from closing the console window, and from a shutdown
    /// or kill, so the commands dispose the debug-log capture, telemetry and server and the logs are complete.
    /// <para>
    /// On Windows .NET maps CTRL_CLOSE_EVENT (the window's close button) to <see cref="PosixSignal.SIGHUP"/> and
    /// CTRL_SHUTDOWN_EVENT to <see cref="PosixSignal.SIGTERM"/> (dotnet/runtime#55333; Ctrl+C is CTRL_C_EVENT, handled through
    /// <see cref="Console.CancelKeyPress"/>). Windows ends the process as soon as the close handler returns, and anyway
    /// about 5 s after the event, so <see cref="OnSignal"/> holds the handler until the command has finished
    /// (<see cref="Dispose"/>), for at most <see cref="CloseGrace"/>. On Linux the cancelled signal leaves the process
    /// running; <see cref="Closing"/> tells the menu to end then instead of waiting for input.
    /// </para>
    /// </summary>
    public sealed class StopSignal : IDisposable
    {
        /// <summary>The signals registered besides Ctrl+C: closing the window (SIGHUP) and shutdown or kill (SIGTERM).</summary>
        public static readonly PosixSignal[] CloseSignals = { PosixSignal.SIGHUP, PosixSignal.SIGTERM };

        /// <summary>How long a close handler waits for the command to finish; below Windows' 5 s limit.</summary>
        public static readonly TimeSpan DefaultCloseGrace = TimeSpan.FromSeconds(4);

        private readonly CancellationTokenSource requested = new CancellationTokenSource();
        private readonly ManualResetEventSlim finished = new ManualResetEventSlim(false);
        private readonly List<PosixSignalRegistration> registrations = new List<PosixSignalRegistration>();
        private readonly bool console;
        private int closing;

        /// <param name="register">false: no Ctrl+C or signal registration (tests call <see cref="OnSignal"/> directly).</param>
        public StopSignal(bool register = true, TimeSpan? closeGrace = null)
        {
            CloseGrace = closeGrace ?? DefaultCloseGrace;
            if (!register) return;
            console = true;
            Console.CancelKeyPress += OnCancelKeyPress;
            foreach (PosixSignal signal in CloseSignals)
            {
                try
                {
                    registrations.Add(PosixSignalRegistration.Create(signal, OnSignal));
                }
                catch (PlatformNotSupportedException)
                {
                }
            }
        }

        public TimeSpan CloseGrace { get; }

        public CancellationToken Token => requested.Token;

        public bool IsRequested => requested.IsCancellationRequested;

        /// <summary>True once the window was closed or the process was told to end (not after Ctrl+C).</summary>
        public bool Closing => Volatile.Read(ref closing) != 0;

        /// <summary>Waits for a stop request; false when <paramref name="milliseconds"/> passed first.</summary>
        public bool Wait(int milliseconds = Timeout.Infinite) => requested.Token.WaitHandle.WaitOne(milliseconds);

        public void Request()
        {
            try
            {
                requested.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        /// <summary>The close/shutdown handler: requests the stop and holds until the command is done (or the grace ran out).</summary>
        public void OnSignal(PosixSignalContext context)
        {
            context.Cancel = true;
            Interlocked.Exchange(ref closing, 1);
            Request();
            finished.Wait(CloseGrace);
        }

        private void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            e.Cancel = true;
            Request();
        }

        /// <summary>The command is done: releases a waiting close handler, then unregisters.</summary>
        public void Dispose()
        {
            finished.Set();
            if (console) Console.CancelKeyPress -= OnCancelKeyPress;
            foreach (PosixSignalRegistration registration in registrations) registration.Dispose();
            registrations.Clear();
        }
    }
}
