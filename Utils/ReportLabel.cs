using System;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Html;

namespace GovBudget.Utils
{
    /// <summary>
    /// Renders a reference-code + name pair as two stacked lines instead of one
    /// long "CODE - Name" string.
    ///
    /// Activity and programme names are long and often Arabic, so on one line they
    /// wrap into a tall ragged block that squeezes the numeric columns beside them -
    /// badly on screen, worse in print. Splitting them lets the code stay on a single
    /// unwrapped line for scanning, while the name wraps beneath it in smaller text.
    /// </summary>
    public static class ReportLabel
    {
        /// <summary>
        /// Code above, name below. Either part may be blank; when both are, the
        /// optional placeholder is shown instead.
        /// </summary>
        public static IHtmlContent CodeName(string? code, string? name, string placeholder = "")
        {
            var c = (code ?? "").Trim();
            var n = (name ?? "").Trim();

            if (c.Length == 0 && n.Length == 0)
            {
                return new HtmlString(HtmlEncoder.Default.Encode(placeholder));
            }

            var sb = new StringBuilder(c.Length + n.Length + 80);
            sb.Append("<span class=\"rpt-label\">");

            if (c.Length > 0)
            {
                sb.Append("<span class=\"rpt-code\">")
                  .Append(HtmlEncoder.Default.Encode(c))
                  .Append("</span>");
            }

            if (n.Length > 0)
            {
                // dir="auto" lets the browser take the base direction from the first
                // strong character, so an Arabic name lays out right-to-left and an
                // English one left-to-right without the caller having to know which.
                // Mixed names ("متحف Al Qasimi") then punctuate correctly instead of
                // stranding the trailing bracket or dash on the wrong side.
                sb.Append("<span class=\"rpt-name\" dir=\"auto\"");
                if (HasArabic(n))
                {
                    sb.Append(" lang=\"ar\"");
                }
                sb.Append('>')
                  .Append(HtmlEncoder.Default.Encode(n))
                  .Append("</span>");
            }

            sb.Append("</span>");
            return new HtmlString(sb.ToString());
        }

        /// <summary>
        /// True when the text contains a character from the Arabic block. Used only to
        /// tag the element for styling - direction is handled by dir="auto".
        /// </summary>
        private static bool HasArabic(string text)
        {
            // Compared as integer code points rather than char literals. The ranges
            // include invisible characters (U+FEFF among them), so written as literal
            // glyphs this block would be impossible to review or edit safely.
            foreach (var ch in text)
            {
                int cp = ch;
                if ((cp >= 0x0600 && cp <= 0x06FF) ||   // Arabic
                    (cp >= 0x0750 && cp <= 0x077F) ||   // Arabic Supplement
                    (cp >= 0x08A0 && cp <= 0x08FF) ||   // Arabic Extended-A
                    (cp >= 0xFB50 && cp <= 0xFDFF) ||   // Presentation Forms-A
                    (cp >= 0xFE70 && cp <= 0xFEFF))     // Presentation Forms-B
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Same treatment for a value that has already been joined into "CODE - Name",
        /// which is how the Report Builder hands over its dimension labels.
        ///
        /// Splits on the FIRST " - " only, so a name that itself contains a dash -
        /// "Al Qasimi - Heritage Wing" - keeps the remainder intact instead of losing
        /// everything after the second separator. A value with no separator is left
        /// alone rather than guessed at.
        /// </summary>
        public static IHtmlContent SplitCodeName(string? combined, string placeholder = "")
        {
            var value = (combined ?? "").Trim();
            if (value.Length == 0)
            {
                return new HtmlString(HtmlEncoder.Default.Encode(placeholder));
            }

            var sep = value.IndexOf(" - ", StringComparison.Ordinal);
            if (sep <= 0)
            {
                return new HtmlString(HtmlEncoder.Default.Encode(value));
            }

            return CodeName(value[..sep], value[(sep + 3)..]);
        }
    }
}
