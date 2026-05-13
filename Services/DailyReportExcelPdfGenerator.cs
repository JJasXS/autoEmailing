using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

public sealed class DailyReportExcelPdfGenerator
{
    public IReadOnlyList<EmailAttachment> BuildAttachments(
        IReadOnlyList<DailyReportRow> rows,
        DailyAttachmentReportOptions options,
        DateOnly scheduleDate)
    {
        var stamp = scheduleDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var baseName = $"Outstanding_{stamp}";
        var xlsx = BuildExcel(rows, options, stamp);
        var pdf = BuildPdf(rows, options, stamp);
        return
        [
            new EmailAttachment($"{baseName}.xlsx",
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx),
            new EmailAttachment($"{baseName}.pdf", "application/pdf", pdf)
        ];
    }

    private static byte[] BuildExcel(IReadOnlyList<DailyReportRow> rows, DailyAttachmentReportOptions options, string stamp)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Outstanding");
        ws.Cell(1, 1).Value = options.ReportTitle;
        ws.Range(1, 1, 1, 4).Merge();
        ws.Cell(2, 1).Value = string.IsNullOrWhiteSpace(options.CompanyName)
            ? $"As of {stamp}"
            : $"{options.CompanyName} · As of {stamp}";
        ws.Range(2, 1, 2, 4).Merge();

        var r = 4;
        ws.Cell(r, 1).Value = "Document";
        ws.Cell(r, 2).Value = "Date";
        ws.Cell(r, 3).Value = "Age (days)";
        ws.Cell(r, 4).Value = $"Outstanding ({options.CurrencyLabel})";
        ws.Range(r, 1, r, 4).Style.Font.Bold = true;
        ws.Range(r, 1, r, 4).Style.Border.BottomBorder = XLBorderStyleValues.Thin;

        r++;
        foreach (var row in rows)
        {
            ws.Cell(r, 1).Value = row.Document;
            ws.Cell(r, 2).Value = row.DocDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            ws.Cell(r, 3).Value = row.AgeDays;
            ws.Cell(r, 4).Value = row.Amount;
            ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
            r++;
        }

        var total = rows.Sum(x => x.Amount);
        ws.Cell(r, 3).Value = $"Total {options.CurrencyLabel}";
        ws.Cell(r, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
        ws.Cell(r, 3).Style.Font.Bold = true;
        ws.Cell(r, 4).Value = total;
        ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 4).Style.Font.Bold = true;

        ws.Columns().AdjustToContents();
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static byte[] BuildPdf(IReadOnlyList<DailyReportRow> rows, DailyAttachmentReportOptions options, string stamp)
    {
        var title = options.ReportTitle;
        var subtitle = string.IsNullOrWhiteSpace(options.CompanyName)
            ? $"As of {stamp}"
            : $"{options.CompanyName} · As of {stamp}";
        var total = rows.Sum(x => x.Amount);
        var cur = options.CurrencyLabel;

        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(36);
                page.Size(PageSizes.A4);
                page.DefaultTextStyle(x => x.FontSize(10));

                page.Header().Column(col =>
                {
                    col.Item().Text(title).SemiBold().FontSize(16).FontColor(Colors.Blue.Darken4);
                    col.Item().PaddingTop(4).Text(subtitle).FontColor(Colors.Grey.Darken2);
                });

                page.Content().PaddingTop(16).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.RelativeColumn(2.2f);
                        c.RelativeColumn(1.2f);
                        c.RelativeColumn(0.9f);
                        c.RelativeColumn(1.2f);
                    });

                    table.Header(h =>
                    {
                        static IContainer CellStyle(IContainer c) =>
                            c.DefaultTextStyle(x => x.SemiBold()).PaddingVertical(6).BorderBottom(1).BorderColor(Colors.Blue.Darken2);

                        h.Cell().Element(CellStyle).Text("Document");
                        h.Cell().Element(CellStyle).Text("Date");
                        h.Cell().Element(CellStyle).AlignRight().Text("Age (days)");
                        h.Cell().Element(CellStyle).AlignRight().Text($"Outstanding ({cur})");
                    });

                    foreach (var row in rows)
                    {
                        table.Cell().PaddingVertical(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                            .Text(row.Document);
                        table.Cell().PaddingVertical(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                            .Text(row.DocDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "—");
                        table.Cell().PaddingVertical(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                            .AlignRight().Text(row.AgeDays.ToString(CultureInfo.InvariantCulture));
                        table.Cell().PaddingVertical(4).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2)
                            .AlignRight().Text(row.Amount.ToString("N2", CultureInfo.InvariantCulture));
                    }

                    table.Cell().ColumnSpan(3).AlignRight().PaddingTop(8).DefaultTextStyle(x => x.Bold())
                        .Text($"Total {cur}");
                    table.Cell().AlignRight().PaddingTop(8).DefaultTextStyle(x => x.Bold())
                        .Text(total.ToString("N2", CultureInfo.InvariantCulture));
                });
            });
        });

        return doc.GeneratePdf();
    }
}
