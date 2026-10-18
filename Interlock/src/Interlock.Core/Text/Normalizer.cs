namespace Interlock.Core.Text
{
    public static class Normalizer
    {
        /// <summary>upper(trim(value)); null for null or blank input, so a blank value never matches.</summary>
        public static string Norm(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            return value.Trim().ToUpperInvariant();
        }
    }
}
