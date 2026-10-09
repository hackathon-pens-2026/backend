using System.Globalization;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Signatures.Services;

namespace SignIt.Modules.Templates.Services;

// Controlled text/table layout. No HTML, script, external fetch, or user-supplied file paths.
public sealed class PdfSharpLetterTemplateRenderer : ILetterTemplateRenderer
{
    public const string Version = "signit-pdfsharp-v1";
    private static readonly Regex Placeholder = new(@"\{\{([a-z0-9_]+)\}\}", RegexOptions.CultureInvariant);
    private static readonly XColor HeaderNavy = XColor.FromArgb(0x31, 0x4F, 0x7D);
    private static readonly XColor HeaderDark = XColor.FromArgb(0x36, 0x33, 0x34);
    private readonly Lazy<XImage?> headerLogo;

    public PdfSharpLetterTemplateRenderer(IHostEnvironment? environment = null)
    {
        headerLogo = new Lazy<XImage?>(() => LoadHeaderLogo(environment), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public RenderedPreview Render(PreviewRenderInput input, CancellationToken ct)
    {
        if (input.Layout.RendererVersion != Version) throw new InvalidOperationException("Versi renderer tidak tersedia.");
        if (input.Participants.Length is < 5 or > 7 || input.Participants.Select(x => x.Stage.PositionCode).Distinct().Count() != input.Participants.Length)
            throw new InvalidOperationException("Peserta routing tidak valid.");
        SignItFontResolver.EnsureRegistered();
        using var canvas = new LayoutCanvas(input, ct, headerLogo.Value);
        if (input.Layout.CoverTitle is { } cover)
        {
            canvas.Space(130);
            canvas.Paragraph(cover, 17, true, "center");
            canvas.Space(25);
            canvas.Paragraph(Value("nama_kegiatan") + "\n" + Value("tahun_kegiatan"), 15, true, "center");
            canvas.Space(25);
            canvas.Paragraph(Value("nama_divisi_pelaksana"), 12, false, "center");
            canvas.Space(110);
            canvas.Paragraph(Value("nama_ormawa") + "\n" + Value("nama_kampus") + "\n" + Value("tahun_kegiatan"), 12, true, "center");
            canvas.NewPage();
        }
        foreach (var block in input.Layout.Blocks)
        {
            ct.ThrowIfCancellationRequested();
            switch (block.Kind)
            {
                case "pageBreak": canvas.NewPage(); break;
                case "heading": canvas.Heading(Expand(block.Text ?? "")); break;
                case "paragraph": canvas.Paragraph(Expand(block.Text ?? "")); break;
                case "right": canvas.Paragraph(Expand(block.Text ?? ""), alignment: "right"); break;
                case "table": canvas.Table((block.Rows ?? []).Select(row => row.Select(Expand).ToArray()).ToArray()); break;
                default: throw new InvalidOperationException("Jenis blok template tidak didukung.");
            }
        }
        var slots = canvas.SignaturePages();
        return new(canvas.Save(), slots);

        string Value(string key) => input.Fields.GetValueOrDefault(key, "");
        string Expand(string text) => Placeholder.Replace(text, match => input.Fields.TryGetValue(match.Groups[1].Value, out var value)
            ? value : throw new InvalidOperationException($"Field layout {match.Groups[1].Value} belum dipetakan."));
    }

    private static XImage? LoadHeaderLogo(IHostEnvironment? environment)
    {
        foreach (var candidate in HeaderLogoCandidates(environment))
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                return XImage.FromFile(candidate);
            }
            catch
            {
                return null;
            }
        }
        return null;
    }

    private static IEnumerable<string> HeaderLogoCandidates(IHostEnvironment? environment)
    {
        if (environment is not null)
        {
            yield return Path.Combine(environment.ContentRootPath, "templates", "assets", "kop-pens-logo.png");
            yield return Path.Combine(environment.ContentRootPath, "..", "templates", "assets", "kop-pens-logo.png");
        }
        yield return Path.Combine(Directory.GetCurrentDirectory(), "templates", "assets", "kop-pens-logo.png");
        yield return Path.Combine(AppContext.BaseDirectory, "templates", "assets", "kop-pens-logo.png");
    }

    private sealed class LayoutCanvas : IDisposable
    {
        private const double Margin = 48, Bottom = 780, Width = 499;
        private const double HeaderDividerY = 112;
        private readonly PreviewRenderInput input;
        private readonly CancellationToken ct;
        private readonly XImage? headerLogo;
        private readonly PdfDocument pdf = new();
        private XGraphics graphics = null!;
        private double y;

        public LayoutCanvas(PreviewRenderInput input, CancellationToken ct, XImage? headerLogo)
        {
            this.input = input;
            this.ct = ct;
            this.headerLogo = headerLogo;
            pdf.Info.Title = input.Title;
            pdf.Info.Author = "SignIt";
            pdf.Info.Subject = input.TemplateVersionId;
            pdf.Info.CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            pdf.Info.ModificationDate = pdf.Info.CreationDate;
            NewPage();
        }

        public void NewPage()
        {
            ct.ThrowIfCancellationRequested();
            if (pdf.PageCount >= 30) throw new InvalidOperationException("Preview melebihi batas 30 halaman.");
            graphics?.Dispose();
            var page = pdf.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            graphics = XGraphics.FromPdfPage(page);
            DrawHeader();
            graphics.DrawString("DRAFT - Belum diajukan / belum ditandatangani", Font(8), XBrushes.Gray,
                new XRect(Margin, 802, Width, 12), XStringFormats.TopLeft);
            graphics.DrawString($"{pdf.PageCount}", Font(8), XBrushes.Gray,
                new XRect(Margin, 802, Width, 12), XStringFormats.TopRight);
        }

        // Letterhead resmi PENS mengikuti kop DOCX sumber (logo + teks kementerian/kontak).
        private void DrawHeader()
        {
            if (headerLogo is not null) graphics.DrawImage(headerLogo, 42, 26, 87, 86);
            var headerY = 30d;
            void Line(string text, double size, bool bold, XColor color)
            {
                graphics.DrawString(text, Font(size, bold), new XSolidBrush(color),
                    new XRect(Margin, headerY, Width, size * 1.5), XStringFormats.TopCenter);
                headerY += size * 1.4;
            }
            Line("KEMENTERIAN PENDIDIKAN TINGGI, SAINS, DAN TEKNOLOGI", 12, false, HeaderNavy);
            Line("POLITEKNIK ELEKTRONIKA NEGERI SURABAYA", 12, true, HeaderNavy);
            Line("Jalan Raya ITS, Sukolilo, Surabaya, 60111", 10, false, HeaderDark);
            Line("Telepon: +62-31-5947280 (hunting); Fax: +62-31-5946114", 10, false, HeaderDark);
            Line("Laman: https://www.pens.ac.id; E-mail: info@pens.ac.id", 10, false, HeaderDark);
            graphics.DrawRectangle(new XSolidBrush(HeaderNavy), 28, HeaderDividerY, 539, 1.2);
            y = HeaderDividerY + 18;
        }

        public void Space(double points) { Ensure(points); y += points; }
        private void Ensure(double points) { if (y + points > Bottom) NewPage(); }
        private static XFont Font(double size, bool bold = false) => new("Times New Roman", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);

        public void Heading(string text)
        {
            Ensure(65);
            y += 10;
            Paragraph(text, 12, true);
        }

        public void Paragraph(string text, double size = 11, bool bold = false, string alignment = "left")
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            var font = Font(size, bold);
            var format = alignment switch { "center" => XStringFormats.TopCenter, "right" => XStringFormats.TopRight, _ => XStringFormats.TopLeft };
            foreach (var line in Wrap(text, font, Width))
            {
                ct.ThrowIfCancellationRequested();
                Ensure(size * 1.45);
                graphics.DrawString(line, font, XBrushes.Black, new XRect(Margin, y, Width, size * 1.45), format);
                y += size * 1.45;
            }
            y += 8;
        }

        private List<string> Wrap(string text, XFont font, double width)
        {
            var result = new List<string>();
            foreach (var paragraph in text.Replace("\r", "").Replace("\t", "    ").Split('\n'))
            {
                var line = "";
                foreach (var word in paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var candidate = line.Length == 0 ? word : line + " " + word;
                    if (graphics.MeasureString(candidate, font).Width <= width) { line = candidate; continue; }
                    if (line.Length > 0) { result.Add(line); line = ""; }
                    var elements = StringInfo.GetTextElementEnumerator(word);
                    while (elements.MoveNext())
                    {
                        var element = elements.GetTextElement();
                        if (graphics.MeasureString(line + element, font).Width > width && line.Length > 0)
                        { result.Add(line); line = ""; }
                        line += element;
                    }
                }
                result.Add(line);
            }
            return result;
        }

        public void Table(string[][] rows)
        {
            if (rows.Length < 2 || rows.Any(row => row.Length != 5)) throw new InvalidOperationException("Schema tabel barang tidak valid.");
            double[] widths = [25, 156, 54, 132, 132];
            var font = Font(10);
            void Draw(string[][] lines, int start, int count, bool header)
            {
                var height = count * 14 + 12;
                var x = Margin;
                for (var col = 0; col < 5; col++)
                {
                    graphics.DrawRectangle(new XPen(XColors.LightGray, .6), header ? XBrushes.LightGray : XBrushes.White, x, y, widths[col], height);
                    for (var index = 0; index < count && start + index < lines[col].Length; index++)
                        graphics.DrawString(lines[col][start + index], font, XBrushes.Black,
                            new XRect(x + 5, y + 6 + index * 14, widths[col] - 10, 14), XStringFormats.TopLeft);
                    x += widths[col];
                }
                y += height;
            }
            var headers = rows[0].Select((cell, col) => Wrap(cell, font, widths[col] - 10).ToArray()).ToArray();
            var headerLines = headers.Max(x => x.Length);
            Ensure(headerLines * 14 + 45);
            Draw(headers, 0, headerLines, true);
            foreach (var row in rows.Skip(1).Where(row => row.Skip(1).Any(cell => !string.IsNullOrWhiteSpace(cell))))
            {
                var lines = row.Select((cell, col) => Wrap(cell, font, widths[col] - 10).ToArray()).ToArray();
                var total = lines.Max(x => x.Length);
                var offset = 0;
                while (offset < total)
                {
                    ct.ThrowIfCancellationRequested();
                    var capacity = (int)((Bottom - y - 12) / 14);
                    if (capacity < 1) { NewPage(); Draw(headers, 0, headerLines, true); continue; }
                    var count = Math.Min(capacity, total - offset);
                    Draw(lines, offset, count, false);
                    offset += count;
                }
            }
            y += 12;
        }

        public SignatureSlot[] SignaturePages()
        {
            NewPage();
            Heading("LEMBAR PENGESAHAN");
            Paragraph(input.Title, 12, true);
            Paragraph(input.Fields.GetValueOrDefault("kota_surat", "") + ", " +
                input.Fields.GetValueOrDefault("tanggal_pengesahan", input.Fields.GetValueOrDefault("tanggal_surat", "")), alignment: "right");
            var slots = new List<SignatureSlot>();
            var rowY = y;
            foreach (var row in input.Participants.OrderBy(p => p.Stage.Order).Chunk(2))
            {
                ct.ThrowIfCancellationRequested();
                var font = Font(10);
                var cards = row.Select(participant => new { Participant = participant,
                    Roles = Wrap(participant.Stage.PositionName, font, 225), Names = Wrap(participant.Stage.Name, font, 225),
                    Identity = Wrap(participant.NimNip is null ? "NRP/NIP belum tersedia" : "NRP/NIP: " + participant.NimNip, Font(8), 225) }).ToArray();
                var rowHeight = cards.Max(card => 16 + card.Roles.Count * 12 + 8 + 110 + 8 + card.Names.Count * 12 + card.Identity.Count * 12 + 12);
                if (rowY + rowHeight > Bottom) { NewPage(); Heading("LEMBAR PENGESAHAN LANJUTAN"); rowY = y; }
                if (rowY + rowHeight > Bottom) throw new InvalidOperationException("Identitas peserta melebihi kapasitas halaman.");
                for (var column = 0; column < cards.Length; column++)
                {
                    var card = cards[column];
                    var participant = card.Participant;
                    var x = Margin + column * 255;
                    var label = participant.Stage.Order <= 2 ? "Mengajukan" : "Mengetahui / Menyetujui";
                    graphics.DrawString($"{participant.Stage.Order}. {label}", font, XBrushes.Black, new XRect(x, rowY, 225, 14), XStringFormats.TopLeft);
                    for (var i = 0; i < card.Roles.Count; i++)
                        graphics.DrawString(card.Roles[i], font, XBrushes.Black, new XRect(x, rowY + 16 + i * 12, 225, 12), XStringFormats.TopLeft);
                    var slot = new SignatureSlot(participant.Stage.PositionCode, pdf.PageCount - 1, x, rowY + 16 + card.Roles.Count * 12 + 8, 225, 110);
                    slots.Add(slot);
                    graphics.DrawRectangle(new XPen(XColors.LightGray, .8) { DashStyle = XDashStyle.Dash }, slot.X, slot.Y, slot.Width, slot.Height);
                    graphics.DrawString("Menunggu tanda tangan", Font(9), XBrushes.Gray,
                        new XRect(slot.X, slot.Y, slot.Width, slot.Height), XStringFormats.Center);
                    var nameY = slot.Y + slot.Height + 8;
                    for (var i = 0; i < card.Names.Count; i++)
                        graphics.DrawString(card.Names[i], font, XBrushes.Black, new XRect(x, nameY + i * 12, 225, 12), XStringFormats.TopLeft);
                    for (var i = 0; i < card.Identity.Count; i++)
                        graphics.DrawString(card.Identity[i], Font(8), XBrushes.Gray,
                            new XRect(x, nameY + card.Names.Count * 12 + 4 + i * 12, 225, 12), XStringFormats.TopLeft);
                }
                rowY += rowHeight;
            }
            return slots.ToArray();
        }

        public byte[] Save()
        {
            graphics.Dispose();
            using var stream = new MemoryStream();
            pdf.Save(stream, false);
            return stream.ToArray();
        }
        public void Dispose() { graphics.Dispose(); pdf.Dispose(); }
    }
}
