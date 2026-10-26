using System.Text.RegularExpressions;

namespace MapCue.Application.Agent;

/// <summary>
/// Refuses free text that looks like something executable (architecture §9.3 step 3): flow YAML or Maestro commands, shell
/// commands, file paths and URLs. The schema already forbids control characters (so no multi-line YAML); this catches single-line
/// forms. Free text is never executed anyway; an answer that contains such text is rejected as a whole as untrustworthy.
/// </summary>
public static partial class ContentGuard
{
    /// <summary>Name of the first rule <paramref name="text"/> violates, or null.</summary>
    public static string? Find(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        try
        {
            if (Url().IsMatch(text))
            {
                return "url";
            }

            if (YamlOrFlowCommand().IsMatch(text))
            {
                return "yaml";
            }

            if (Shell().IsMatch(text))
            {
                return "command";
            }

            return Path().IsMatch(text) ? "path" : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return "timeout";
        }
    }

    [GeneratedRegex(@"(?i)\b[a-z][a-z0-9+.-]{1,15}://|\bwww\.[a-z0-9-]+\.|\b(?:mailto|intent|javascript|data|file):", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Url();

    // Maestro/TapCue commands as YAML keys, YAML list items with a key, document markers and flow mappings.
    [GeneratedRegex(@"(?i)\b(?:appId|tapOn|doubleTapOn|longPressOn|inputText|inputRandom\w*|eraseText|launchApp|stopApp|killApp|clearState|clearKeychain|runFlow|runScript|evalScript|openLink|pressKey|scrollUntilVisible|swipe|back|hideKeyboard|takeScreenshot|assertVisible|assertNotVisible|setLocation|addMedia|travel|repeat|retry|onFlowStart|onFlowComplete|env)\s*:|(?:^|\s)-\s+[A-Za-z_][\w-]*\s*:|^\s*(?:---|\.\.\.)\s*$|\{\s*[\w""']+\s*:", RegexOptions.CultureInvariant, 250)]
    private static partial Regex YamlOrFlowCommand();

    [GeneratedRegex(@"`|\$\(|\$\{|&&|\|\||\|\s*(?:sh|bash|zsh)\b|(?i:\b(?:sudo|rm\s+-|chmod|chown|curl|wget|adb\s|bash\s|sh\s+-c|powershell|cmd(?:\.exe)?\s*/c|tapcue\s|maestro\s|xcrun|docker\s|kubectl)\b)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Shell();

    // Absolute and home paths (/etc/x, ~/x), traversal (../), Windows drives (C:\) and UNC shares.
    [GeneratedRegex(@"(?:^|[\s""'(=])(?:/[\w.-]+/[\w./-]*|~/|\.\./|\.\.\\|[A-Za-z]:\\|\\\\[\w.-]+\\)", RegexOptions.CultureInvariant, 250)]
    private static partial Regex Path();
}
