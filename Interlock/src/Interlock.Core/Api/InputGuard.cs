using Interlock.Core.Errors;

namespace Interlock.Core.Api
{
    /// <summary>
    /// Cleans the text of an API request. Barcode scanners append CR/LF, so surrounding whitespace is trimmed; a control character
    /// inside the text (NUL, a line break, DEL) is refused, because PostgreSQL rejects NUL and a line break in a target would
    /// corrupt the scan-check message.
    /// </summary>
    public static class InputGuard
    {
        /// <summary>The trimmed value (null stays null). Throws Validation "Invalid character in &lt;field&gt;" for an embedded control character.</summary>
        public static string Clean(string value, string field)
        {
            if (value == null)
                return null;
            var trimmed = value.Trim();
            RejectControl(trimmed, field);
            return trimmed;
        }

        /// <summary>For a secret (a PIN): not trimmed, because trimming would change the secret; control characters are refused anywhere.</summary>
        public static string CleanSecret(string value, string field)
        {
            RejectControl(value, field);
            return value;
        }

        private static void RejectControl(string value, string field)
        {
            if (value == null)
                return;
            foreach (var c in value)
            {
                if (c < 0x20 || c == 0x7F)
                    throw new InterlockException(ErrorKind.Validation, "Invalid character in " + field);
            }
        }
    }
}
