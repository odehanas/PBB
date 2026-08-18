namespace GovBudget.Utils
{
    /// <summary>
    /// RAK Department of Finance (DOF) brand palette shared across ClosedXML exports.
    /// Keep these in sync with the CSS custom properties in wwwroot/css/site.css
    /// (--app-brand-header, --app-primary, --app-accent).
    /// </summary>
    public static class BrandColors
    {
        public const string HeaderHex = "#0A5B43";   // DOF dark green (table/section headers)
        public const string HeaderAltHex = "#0F7B5B"; // DOF green
        public const string HeaderFgHex = "#FFFFFF";
        public const string AccentHex = "#C9A227";   // DOF gold
        public const string SubtotalHex = "#DDEEE8"; // light green tint for subtotal/total rows
    }
}
