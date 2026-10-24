using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Excalibur.Core.Models;

namespace Excalibur.Core.Services;

public static class CapacityHtmlReportGenerator
{
    public static string Generate(CapacityTestResult r, string? rulesVersion)
    {
        var hasBad = r.Verdict == "Failing";
        var verdictClass = hasBad ? "bad" : (r.Verdict == "Warning" ? "warn" : "ok");
        string capStr = FormatBytes((double)r.DeclaredCapacityBytes);
        string actualStr = r.FullScan ? FormatBytes((double)r.ActualCapacityBytes) : "not measured (marker-only)";
        string elapsed = FormatDuration(TimeSpan.FromSeconds(r.ElapsedSeconds));

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html><html lang=\"en\"><head>");
        html.AppendLine("<meta charset=\"utf-8\"><title>Excalibur capacity test — Disk " + r.DiskNumber + "</title>");
        html.AppendLine("<style>");
        html.AppendLine(GetCss());
        html.AppendLine("</style></head><body><div class=\"wrap\">");
        html.AppendLine("<header><h1>Excalibur <span class=\"v\">v1.0.0-alpha</span></h1>");
        html.AppendLine("<div class=\"verdict " + verdictClass + "\">" + (hasBad ? "FAILING" : r.Verdict.ToUpper()) + "</div></header>");

        html.AppendLine("<section class=\"panel\"><table class=\"info\">");
        Row(html, "Disk", "#" + r.DiskNumber);
        Row(html, "Model", r.Model ?? "—");
        Row(html, "Serial", r.SerialNumber ?? "—");
        Row(html, "Declared", capStr);
        Row(html, "Actual", actualStr);
        Row(html, "Mode", r.FullScan ? "FULL SCAN" : "marker-only");
        Row(html, "Marker size", FormatBytes(r.MarkerSizeBytes));
        Row(html, "Elapsed", elapsed);
        html.AppendLine("</table></section>");

        html.AppendLine("<section class=\"panel stats\">");
        Stat(html, "Write speed", Math.Round(r.WriteSpeedMbps, 2) + " MB/s", "ok");
        Stat(html, "Read speed", Math.Round(r.ReadSpeedMbps, 2) + " MB/s", "ok");
        Stat(html, "Verdict", r.Verdict, verdictClass);
        html.AppendLine("</section>");

        html.AppendLine("<section class=\"panel\"><h2>Markers</h2>");
        html.AppendLine("<table class=\"markers\"><tr><th>#</th><th>Byte offset</th><th>LBA</th><th>Write</th><th>Read</th><th>Hash match</th><th>SHA-256</th></tr>");
        foreach (var m in r.Markers)
        {
            html.AppendLine("<tr><td>" + m.Index + "</td><td>" + m.ByteOffset.ToString("N0") + "</td><td>"
                + m.Lba + "</td><td>" + (m.WriteOk ? "✓" : "✗") + "</td><td>" + (m.ReadOk ? "✓" : "✗")
                + "</td><td>" + (m.HashMatches ? "✓" : "✗")
                + "</td><td class=\"hash\">" + (m.Hash.Length > 16 ? m.Hash[..16] + "..." : m.Hash) + "</td></tr>");
        }
        html.AppendLine("</table>");

        if (r.VerdictReasons.Count > 0)
        {
            html.AppendLine("<h3>Reasons</h3><ul>");
            foreach (var reason in r.VerdictReasons) html.AppendLine("<li>" + HtmlEscape(reason) + "</li>");
            html.AppendLine("</ul>");
        }
        if (r.Warnings.Count > 0)
        {
            html.AppendLine("<h3>Warnings</h3><ul class=\"warn-list\">");
            foreach (var w in r.Warnings) html.AppendLine("<li>" + HtmlEscape(w) + "</li>");
            html.AppendLine("</ul>");
        }
        html.AppendLine("</section>");

        html.AppendLine("<footer><div class=\"meta\">Generated " + r.FinishedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") + " by Excalibur v1.0.0-alpha" + (rulesVersion != null ? " · Rules " + rulesVersion : "") + "</div>");
        html.AppendLine("<div class=\"hash\" id=\"hash\">SHA-256: computing...</div></footer>");
        html.AppendLine("</div></body></html>");

        var body = html.ToString();
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(body));
        return body.Replace("SHA-256: computing...", "SHA-256: " + Convert.ToHexString(hashBytes).ToLowerInvariant());
    }

    private static void Row(StringBuilder sb, string label, string value) =>
        sb.AppendLine("<tr><th>" + HtmlEscape(label) + "</th><td>" + HtmlEscape(value) + "</td></tr>");

    private static void Stat(StringBuilder sb, string label, string value, string cls) =>
        sb.AppendLine("<div class=\"stat " + cls + "\"><div class=\"l\">" + HtmlEscape(label) + "</div><div class=\"v\">" + HtmlEscape(value) + "</div></div>");

    private static string HtmlEscape(string? s) => string.IsNullOrEmpty(s) ? "" : System.Net.WebUtility.HtmlEncode(s);

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return bytes.ToString("N2", CultureInfo.InvariantCulture) + " " + units[i];
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return string.Format("{0:0}h{1:00}m{2:00}s", (int)t.TotalHours, t.Minutes, t.Seconds);
        if (t.TotalMinutes >= 1) return string.Format("{0:0}m{1:00}s", (int)t.TotalMinutes, t.Seconds);
        return string.Format("{0:0.0}s", t.TotalSeconds);
    }

    private static string GetCss() => @"
:root{--bg:#0f1116;--panel:#181b22;--border:#232838;--text:#e6e9ef;--dim:#8b93a7;--ok:#3ec97d;--warn:#f0b944;--bad:#e5484d}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.4 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif}
.wrap{max-width:1280px;margin:0 auto;padding:24px}
header{display:flex;justify-content:space-between;align-items:center;margin-bottom:20px}
h1{margin:0;font-size:22px;font-weight:600}
h1 .v{font-size:12px;color:var(--dim);font-weight:400;margin-left:8px}
h2{font-size:15px;font-weight:600;margin:0 0 12px;color:var(--dim);text-transform:uppercase;letter-spacing:.5px}
h3{font-size:14px;margin:16px 0 8px;color:var(--dim);font-weight:600}
.verdict{padding:8px 16px;border-radius:6px;font-weight:600;font-size:13px;letter-spacing:.5px}
.verdict.ok{background:rgba(62,201,125,.15);color:var(--ok)}
.verdict.warn{background:rgba(240,185,68,.15);color:var(--warn)}
.verdict.bad{background:rgba(229,72,77,.15);color:var(--bad)}
.panel{background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:16px 20px;margin-bottom:16px}
table.info{width:100%;border-collapse:collapse}
table.info th{text-align:left;padding:4px 12px 4px 0;color:var(--dim);font-weight:400;width:160px;font-size:13px}
table.info td{padding:4px 0;font-family:'Consolas',monospace;font-size:13px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(180px,1fr));gap:12px}
.stat{background:#0e1014;border-radius:6px;padding:10px 14px}
.stat .l{color:var(--dim);font-size:11px;text-transform:uppercase;letter-spacing:.5px}
.stat .v{font-size:18px;font-weight:600;margin-top:4px}
.stat.ok .v{color:var(--ok)}
.stat.warn .v{color:var(--warn)}
.stat.bad .v{color:var(--bad)}
table.markers{width:100%;border-collapse:collapse;font-family:'Consolas',monospace;font-size:12px}
table.markers th{text-align:left;color:var(--dim);font-weight:400;padding:6px 12px 6px 0;border-bottom:1px solid var(--border);font-size:11px;text-transform:uppercase}
table.markers td{padding:4px 12px 4px 0;border-bottom:1px solid rgba(35,40,56,.5)}
table.markers td.hash{color:var(--dim);font-size:11px}
ul{padding-left:20px;margin:8px 0}
ul.warn-list li{color:var(--warn)}
footer{margin-top:24px;padding-top:16px;border-top:1px solid var(--border);font-size:12px;color:var(--dim);font-family:'Consolas',monospace}
.hash{margin-top:4px;word-break:break-all}
";
}