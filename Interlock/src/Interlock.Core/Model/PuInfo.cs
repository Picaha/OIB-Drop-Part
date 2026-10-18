namespace Interlock.Core.Model
{
    /// <summary>The scanned reel (PU) as reported by Setup Center's External Control call.</summary>
    public sealed class PuInfo
    {
        public string Uid { get; set; }
        public string ComponentName { get; set; }
        public string Mpn { get; set; }
        public string Supplier { get; set; }
        public string LinePath { get; set; }
    }
}
