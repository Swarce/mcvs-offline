namespace McvsOffline.PcSpike
{
    /// <summary>
    /// The log the helpers shared by the spike and the PC kit write to (<see cref="TelemetrySampler"/>,
    /// <see cref="DebugOutputCapture"/>): <see cref="SpikeLog"/> here, the kit's own log in tools/pc-kit (which links these
    /// files and must not depend on spike-only types).
    /// </summary>
    public interface IToolLog
    {
        void Info(string message);

        void Warn(string message);
    }
}
