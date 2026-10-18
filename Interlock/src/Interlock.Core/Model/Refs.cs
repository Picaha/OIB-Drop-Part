namespace Interlock.Core.Model
{
    /// <summary>A station known to Setup Center, with the line it belongs to.</summary>
    public sealed class StationRef
    {
        public string LinePath { get; set; }
        public string StationPath { get; set; }
    }
}
