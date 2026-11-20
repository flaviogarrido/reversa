using System.Globalization;
using System.Text;

namespace SkillStudio.Core;

public static class TextUtil
{
    public static string RemoveDiacritics(string s)
    {
        var normalized = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
