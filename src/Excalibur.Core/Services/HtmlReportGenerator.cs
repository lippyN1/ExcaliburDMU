using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Excalibur.Core.Models;

namespace Excalibur.Core.Services;

public static class HtmlReportGenerator
{
    public static string Generate(SurfaceTestResult result, string? rulesVersion)
    {
        var buckets = result.Map?.Buckets ?? new List<BucketStats>();
        var bucketData = buckets.Select(b => new object?[]
        {
            b.Index,
            b.LbaStart,
            b.LbaEnd,
            b.HasData,
            b.BlocksRead,
            Math.Round(b.MinSpeedMbps, 1),
            Math.Round(b.MaxSpeedMbps, 1),
            Math.Round(b.AvgSpeedMbps, 1),
            b.BadSectors
        }).ToList();

        var bucketJson = JsonSerializer.Serialize(bucketData);
        var hasBad = result.TotalBadSectors > 0;
        var verdictClass = hasBad ? "bad" : (result.Status == "cancelled" ? "warn" : "ok");
        var verdictText = hasBad ? "BAD SECTORS FOUND"
            : result.Status == "cancelled" ? "CANCELLED"
            : result.Status == "completed-with-errors" ? "COMPLETED WITH ERRORS"
            : "PASSED";

        string nowIso = result.FinishedAt.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        string capStr = FormatBytes((double)(result.TotalBytes));
        string elapsed = FormatDuration(TimeSpan.FromSeconds(result.ElapsedSeconds));

        var html = new StringBuilder();
        html.AppendLine("<!DOCTYPE html>");
        html.AppendLine("<html lang=\"en\"><head>");
        html.AppendLine("<meta charset=\"utf-8\">");
        html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
        html.AppendLine("<title>Excalibur surface test — Disk " + result.DiskNumber + "</title>");
        html.AppendLine("<style>");
        html.AppendLine(GetCss());
        html.AppendLine("</style></head><body>");

        html.AppendLine("<div class=\"wrap\">");
        html.AppendLine("<header>");
        html.AppendLine("<h1>Excalibur <span class=\"v\">v1.0.0-alpha</span></h1>");
        html.AppendLine("<div class=\"verdict " + verdictClass + "\">" + verdictText + "</div>");
        html.AppendLine("</header>");

        html.AppendLine("<section class=\"panel\">");
        html.AppendLine("<table class=\"info\">");
        Row(html, "Disk", "#" + result.DiskNumber);
        Row(html, "Model", result.Model ?? "—");
        Row(html, "Serial", result.SerialNumber ?? "—");
        Row(html, "Capacity", capStr);
        Row(html, "Sector size", (result.LogicalSectorSize?.ToString() ?? "?") + " B");
        Row(html, "Block size", FormatBytes(result.BlockSizeBytes));
        Row(html, "Range", "LBA " + result.StartLba + ".." + result.EndLba + " (" + FormatBytes((double)result.TotalBytes) + ")");
        Row(html, "Started", result.StartedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture));
        Row(html, "Elapsed", elapsed);
        html.AppendLine("</table>");
        html.AppendLine("</section>");

        html.AppendLine("<section class=\"panel stats\">");
        Stat(html, "Average", Math.Round(result.AverageSpeedMbps, 1) + " MB/s", "ok");
        Stat(html, "Min (bucket)", Math.Round(result.MinSpeedMbps, 1) + " MB/s", "ok");
        Stat(html, "Max (bucket)", Math.Round(result.MaxSpeedMbps, 1) + " MB/s", "ok");
        var worstCls = "ok";
        if (result.MedianSpeedMbps > 0)
        {
            var worstRatio = result.WorstSpeedMbps / result.MedianSpeedMbps;
            if (worstRatio < 0.30) worstCls = "bad";
            else if (worstRatio < 0.70) worstCls = "warn";
        }
        Stat(html, "Worst LBA", result.WorstLba + " @ " + Math.Round(result.WorstSpeedMbps, 1) + " MB/s", worstCls);
        if (result.SlowBucketCount > 0)
            Stat(html, "Slow buckets", result.SlowBucketCount.ToString() + " / " + (result.Map?.BucketCount ?? 0), "warn");
        Stat(html, "Bad sectors", result.TotalBadSectors.ToString(), hasBad ? "bad" : "ok");
        html.AppendLine("</section>");

        if (result.Calibration != null && result.Calibration.Performed && result.Calibration.Samples.Count > 0)
        {
            html.AppendLine("<section class=\"panel\">");
            html.AppendLine("<h2>Calibration</h2>");
            html.AppendLine("<table class=\"calib\"><tr><th>Block size</th><th>Speed</th><th>OK</th></tr>");
            foreach (var s in result.Calibration.Samples)
            {
                html.AppendLine("<tr><td>" + FormatBytes(s.BlockSizeBytes) + "</td><td>"
                    + Math.Round(s.SpeedMbps, 1) + " MB/s</td><td>"
                    + (s.Success ? "✓" : "✗") + "</td></tr>");
            }
            html.AppendLine("</table>");
            html.AppendLine("<div class=\"chosen\">Chosen: <b>" + FormatBytes(result.Calibration.ChosenBlockSizeBytes) + "</b> @ " + Math.Round(result.Calibration.ChosenSpeedMbps, 1) + " MB/s</div>");
            html.AppendLine("</section>");
        }

        html.AppendLine("<section class=\"panel\">");
        html.AppendLine("<h2>Block map <span class=\"hint\">(click a cell for details)</span></h2>");
        html.AppendLine("<canvas id=\"map\" width=\"1200\" height=\"80\"></canvas>");
        html.AppendLine("<div id=\"hover\" class=\"hover\"></div>");
        html.AppendLine("<div id=\"detail\" class=\"detail\"></div>");
        html.AppendLine("</section>");

        html.AppendLine("<section class=\"panel\">");
        html.AppendLine("<h2>Speed chart</h2>");
        html.AppendLine("<svg id=\"chart\" width=\"1200\" height=\"200\"></svg>");
        html.AppendLine("</section>");

        if (result.BadBlocks.Count > 0)
        {
            html.AppendLine("<section class=\"panel\">");
            html.AppendLine("<h2>Bad blocks (" + result.BadBlocks.Count + ")</h2>");
            html.AppendLine("<table class=\"bad\"><tr><th>LBA start</th><th>LBA end</th><th>Sectors</th><th>Error</th><th>When</th></tr>");
            foreach (var b in result.BadBlocks.Take(200))
            {
                html.AppendLine("<tr><td>" + b.LbaStart + "</td><td>" + b.LbaEnd + "</td><td>"
                    + b.SectorCount + "</td><td>" + HtmlEscape(b.ErrorName)
                    + "</td><td>" + b.Timestamp.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "</td></tr>");
            }
            html.AppendLine("</table>");
            if (result.BadBlocks.Count > 200)
                html.AppendLine("<div class=\"hint\">Showing first 200 of " + result.BadBlocks.Count + "</div>");
            html.AppendLine("</section>");
        }

        html.AppendLine("<footer>");
        html.AppendLine("<div class=\"meta\">Generated " + nowIso + " by Excalibur v1.0.0-alpha · Rules " + (rulesVersion ?? "?") + "</div>");
        html.AppendLine("<div class=\"hash\" id=\"hash\">SHA-256: computing...</div>");
        html.AppendLine("</footer>");

        html.AppendLine("</div>");

        html.AppendLine("<script>");
        html.AppendLine("const BUCKETS = " + bucketJson + ";");
        html.AppendLine("const DISK_NUM = " + result.DiskNumber + ";");
        html.AppendLine("const BUCKET_MB = " + (result.Map?.BucketSizeMb ?? 0) + ";");
        html.AppendLine("const SECTOR = " + (result.LogicalSectorSize ?? 512) + ";");
        html.AppendLine(GetJs());
        html.AppendLine("</script>");
        html.AppendLine("</body></html>");

        var bodyHtml = html.ToString();
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(bodyHtml));
        var hashHex = Convert.ToHexString(hashBytes).ToLowerInvariant();
        bodyHtml = bodyHtml.Replace("SHA-256: computing...", "SHA-256: " + hashHex);
        return bodyHtml;
    }

    private static void Row(StringBuilder sb, string label, string value)
    {
        sb.AppendLine("<tr><th>" + HtmlEscape(label) + "</th><td>" + HtmlEscape(value) + "</td></tr>");
    }

    private static void Stat(StringBuilder sb, string label, string value, string cls)
    {
        sb.AppendLine("<div class=\"stat " + cls + "\"><div class=\"l\">" + HtmlEscape(label) + "</div><div class=\"v\">" + HtmlEscape(value) + "</div></div>");
    }

    private static string HtmlEscape(string? s) =>
        string.IsNullOrEmpty(s) ? "" : System.Net.WebUtility.HtmlEncode(s);

    private static string FormatBytes(double bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        while (bytes >= 1024 && i < units.Length - 1) { bytes /= 1024; i++; }
        return bytes.ToString("N2", CultureInfo.InvariantCulture) + " " + units[i];
    }

    private static string FormatDuration(TimeSpan t)
    {
        if (t.TotalHours >= 1) return string.Format(CultureInfo.InvariantCulture, "{0:0}h{1:00}m{2:00}s", (int)t.TotalHours, t.Minutes, t.Seconds);
        if (t.TotalMinutes >= 1) return string.Format(CultureInfo.InvariantCulture, "{0:0}m{1:00}s", (int)t.TotalMinutes, t.Seconds);
        return string.Format(CultureInfo.InvariantCulture, "{0:0.0}s", t.TotalSeconds);
    }

    private static string GetCss() => @"
:root{--bg:#0f1116;--panel:#181b22;--border:#232838;--text:#e6e9ef;--dim:#8b93a7;--ok:#3ec97d;--warn:#f0b944;--bad:#e5484d;--accent:#5b8cff}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.4 -apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif}
.wrap{max-width:1280px;margin:0 auto;padding:24px}
header{display:flex;justify-content:space-between;align-items:center;margin-bottom:20px}
h1{margin:0;font-size:22px;font-weight:600}
h1 .v{font-size:12px;color:var(--dim);font-weight:400;margin-left:8px}
h2{font-size:15px;font-weight:600;margin:0 0 12px;color:var(--dim);text-transform:uppercase;letter-spacing:.5px}
h2 .hint{font-size:11px;text-transform:none;font-weight:400;letter-spacing:0}
.verdict{padding:8px 16px;border-radius:6px;font-weight:600;font-size:13px;letter-spacing:.5px}
.verdict.ok{background:rgba(62,201,125,.15);color:var(--ok)}
.verdict.warn{background:rgba(240,185,68,.15);color:var(--warn)}
.verdict.bad{background:rgba(229,72,77,.15);color:var(--bad)}
.panel{background:var(--panel);border:1px solid var(--border);border-radius:8px;padding:16px 20px;margin-bottom:16px}
table.info{width:100%;border-collapse:collapse}
table.info th{text-align:left;padding:4px 12px 4px 0;color:var(--dim);font-weight:400;width:160px;font-size:13px}
table.info td{padding:4px 0;font-family:'Consolas',monospace;font-size:13px}
.stats{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px}
.stat{background:#0e1014;border-radius:6px;padding:10px 14px}
.stat .l{color:var(--dim);font-size:11px;text-transform:uppercase;letter-spacing:.5px}
.stat .v{font-size:18px;font-weight:600;margin-top:4px}
.stat.ok .v{color:var(--ok)}
.stat.warn .v{color:var(--warn)}
.stat.bad .v{color:var(--bad)}
canvas{width:100%;height:80px;display:block;border-radius:4px;background:#0e1014;image-rendering:pixelated;cursor:crosshair}
svg{width:100%;height:200px;display:block;background:#0e1014;border-radius:4px}
.hover{font-size:12px;color:var(--dim);min-height:18px;margin-top:6px;font-family:'Consolas',monospace}
.detail{margin-top:8px;font-size:13px;font-family:'Consolas',monospace;color:var(--text);min-height:18px}
table.calib,table.bad{width:100%;border-collapse:collapse;font-family:'Consolas',monospace;font-size:13px}
table.calib th,table.bad th{text-align:left;color:var(--dim);font-weight:400;padding:6px 12px 6px 0;border-bottom:1px solid var(--border);font-size:12px;text-transform:uppercase;letter-spacing:.5px}
table.calib td,table.bad td{padding:4px 12px 4px 0;border-bottom:1px solid rgba(35,40,56,.5)}
table.bad td{color:var(--bad)}
.chosen{margin-top:8px;font-size:13px;color:var(--dim)}
.chosen b{color:var(--text)}
.hint{color:var(--dim);font-size:12px;margin-top:6px}
footer{margin-top:24px;padding-top:16px;border-top:1px solid var(--border);font-size:12px;color:var(--dim);font-family:'Consolas',monospace}
.hash{margin-top:4px;word-break:break-all}
";

    private static string GetJs() => @"
(function(){
  const canvas = document.getElementById('map');
  const ctx = canvas.getContext('2d');
  const hover = document.getElementById('hover');
  const detail = document.getElementById('detail');
  const chart = document.getElementById('chart');

  let maxAvg = 0;
  for (const b of BUCKETS) if (b[3] && b[7] > maxAvg) maxAvg = b[7];
  if (maxAvg <= 0) maxAvg = 1;

  function colorFor(b) {
    if (!b[3]) return '#2a2f3d';
    if (b[8] > 0) return '#5b8cff';
    const ratio = b[7] / maxAvg;
    if (ratio < 0.30) return '#e5484d';
    if (ratio < 0.60) return '#f0b944';
    return '#3ec97d';
  }

  function renderMap() {
    const W = canvas.width = Math.min(canvas.clientWidth * 2, 4000);
    const H = canvas.height = 160;
    const n = BUCKETS.length;
    if (n === 0) { ctx.fillStyle='#0e1014'; ctx.fillRect(0,0,W,H); return; }
    const cellW = W / n;
    for (let i = 0; i < n; i++) {
      const b = BUCKETS[i];
      ctx.fillStyle = colorFor(b);
      const x = i * cellW;
      const w = Math.max(1, cellW + 0.5);
      ctx.fillRect(x, 0, w, H);
    }
  }

  function formatBytes(bytes) {
    const u = ['B','KB','MB','GB','TB','PB'];
    let i = 0;
    while (bytes >= 1024 && i < u.length-1) { bytes /= 1024; i++; }
    return bytes.toFixed(2) + ' ' + u[i];
  }

  function findBucket(clientX) {
    const rect = canvas.getBoundingClientRect();
    const x = clientX - rect.left;
    const idx = Math.floor((x / rect.width) * BUCKETS.length);
    return BUCKETS[Math.max(0, Math.min(BUCKETS.length - 1, idx))];
  }

  canvas.addEventListener('mousemove', (ev) => {
    const b = findBucket(ev.clientX);
    if (!b[3]) {
      hover.textContent = 'Bucket ' + b[0] + ' · LBA ' + b[1] + '..' + b[2] + ' · not read';
    } else {
      hover.textContent = 'Bucket ' + b[0] + ' · LBA ' + b[1] + '..' + b[2]
        + ' · avg ' + b[7].toFixed(1) + ' MB/s'
        + (b[8] > 0 ? ' · BAD ' + b[8] + ' sectors' : '');
    }
  });
  canvas.addEventListener('click', (ev) => {
    const b = findBucket(ev.clientX);
    if (!b[3]) {
      detail.textContent = 'Bucket ' + b[0] + ' was not read';
      return;
    }
    const bytesStart = b[1] * SECTOR;
    const bytesEnd = (b[2] + 1) * SECTOR;
    detail.textContent = 'Bucket ' + b[0]
      + ' · LBA ' + b[1] + '..' + b[2]
      + ' · offset ' + formatBytes(bytesStart) + '..' + formatBytes(bytesEnd)
      + ' · avg ' + b[7].toFixed(2) + ' MB/s'
      + ' · blocks ' + b[4]
      + (b[8] > 0 ? ' · ' + b[8] + ' bad sectors' : '');
  });

  function renderChart() {
    const W = chart.clientWidth * 2;
    const H = 400;
    chart.setAttribute('viewBox', '0 0 ' + W + ' ' + H);
    chart.innerHTML = '';
    const n = BUCKETS.length;
    if (n === 0) return;

    let minV = Infinity, maxV = 0;
    for (const b of BUCKETS) if (b[3]) { if (b[7] < minV) minV = b[7]; if (b[7] > maxV) maxV = b[7]; }
    if (!isFinite(minV)) { minV = 0; maxV = 1; }
    const pad = 20;
    const usableW = W - pad * 2;
    const usableH = H - pad * 2;

    const ns = 'http://www.w3.org/2000/svg';
    const path = document.createElementNS(ns, 'path');
    let d = '';
    for (let i = 0; i < n; i++) {
      const b = BUCKETS[i];
      if (!b[3]) continue;
      const x = pad + (i / Math.max(1, n - 1)) * usableW;
      const y = pad + usableH - ((b[7] - minV) / Math.max(0.0001, maxV - minV)) * usableH;
      d += (d === '' ? 'M' : 'L') + x.toFixed(1) + ',' + y.toFixed(1) + ' ';
    }
    path.setAttribute('d', d);
    path.setAttribute('fill', 'none');
    path.setAttribute('stroke', '#5b8cff');
    path.setAttribute('stroke-width', '1.5');
    chart.appendChild(path);

    const t1 = document.createElementNS(ns, 'text');
    t1.setAttribute('x', '4'); t1.setAttribute('y', pad + 10);
    t1.setAttribute('fill', '#8b93a7'); t1.setAttribute('font-size', '11');
    t1.textContent = maxV.toFixed(1) + ' MB/s';
    chart.appendChild(t1);
    const t2 = document.createElementNS(ns, 'text');
    t2.setAttribute('x', '4'); t2.setAttribute('y', H - 4);
    t2.setAttribute('fill', '#8b93a7'); t2.setAttribute('font-size', '11');
    t2.textContent = minV.toFixed(1) + ' MB/s';
    chart.appendChild(t2);
  }

  function render() { renderMap(); renderChart(); }
  render();
  window.addEventListener('resize', render);
})();
";
}