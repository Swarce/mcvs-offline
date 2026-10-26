namespace McvsOffline.Server.Core
{
    /// <summary>
    /// What the host knows about the game client it serves (docs/design/economy-collector.md §2.13). Registered in
    /// <c>context.Features</c> by <see cref="McvsServer.Create"/> from <see cref="ServerOptions.ClientCapabilities"/>
    /// (always present; null options are "unknown"). The embedded host fills it from the loaded <c>Game</c> assembly
    /// (<c>GamePatchProbe</c>); the standalone host cannot see the client and leaves everything unknown.
    /// </summary>
    public sealed class ClientCapabilities
    {
        /// <summary>
        /// The patcher's default <c>store</c> patch is in the client's Game.dll (it adds <c>Game.McvsStoreSafety</c>): a
        /// kill-signature sound or a "nothing left" random avatar/background no longer throws in <c>GetAvailableId</c>.
        /// null: unknown (treated as present). The collector is never active when it is false.
        /// </summary>
        public bool? StorePatch { get; set; }
    }
}
