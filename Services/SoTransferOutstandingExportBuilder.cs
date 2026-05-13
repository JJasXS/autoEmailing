using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>Excel + PDF for SO outstanding transfer — layout matches ERP reference (group header + transfer sub-rows).</summary>
public sealed class SoTransferOutstandingExportBuilder
{
    /// <summary>Banner title on exports (print / PDF).</summary>
    private const string ReportListingTitle = "Outstanding sales order listing";
    private const int ColCount = 10;
    private const int LeftCols = 7;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-GB");

    private static readonly string[] Headers =
    [
        "Seq.",
        "Code",
        "Description",
        "U/Price",
        "Delivy date",
        "Orig Qty",
        "O/Stding",
        "Date",
        "Doc No",
        "Tfer Qty"
    ];

    public byte[] BuildExcel(IReadOnlyList<SoTransferOutstandingBlock> blocks)
    {
        var asAt = DateTime.Today.ToString("dd/MM/yyyy", DisplayCulture);
        var runAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", DisplayCulture);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("SO Transfer");
        ws.Cell(1, 1).Value = ReportListingTitle;
        ws.Range(1, 1, 1, ColCount).Merge();
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Font.FontSize = 12;
        ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Cell(2, 1).Value = $"As at {asAt}";
        ws.Range(2, 1, 2, ColCount).Merge();
        ws.Row(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Cell(3, 1).Value = $"Run (generated): {runAt}";
        ws.Range(3, 1, 3, ColCount).Merge();
        ws.Row(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Row(3).Style.Font.FontColor = XLColor.DimGray;

        ws.PageSetup.Footer.Left.AddText($"As at {asAt}", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Center.AddText($"Run {runAt}", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

        var r = 5;
        for (var c = 0; c < Headers.Length; c++)
            ws.Cell(r, c + 1).Value = Headers[c];
        var headerRow = ws.Range(r, 1, r, ColCount);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        headerRow.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headerRow.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        r++;

        string? lastSoKey = null;
        foreach (var b in blocks)
        {
            if (!string.Equals(b.SoDocKey, lastSoKey, StringComparison.Ordinal))
            {
                ws.Range(r, 1, r, ColCount).Merge();
                ws.Cell(r, 1).Value = $"{b.SoDocNo}    {b.CompanyName}";
                ws.Cell(r, 1).Style.Font.Bold = true;
                ws.Cell(r, 1).Style.Font.FontSize = 11;
                ws.Cell(r, 1).Style.Fill.BackgroundColor = XLColor.FromArgb(235, 242, 252);
                ws.Cell(r, 1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
                r++;
                lastSoKey = b.SoDocKey;
            }

            var transfers = b.Transfers;
            var first = transfers.Count > 0 ? transfers[0] : null;
            WriteMainRow(ws, r, b, first);
            r++;

            for (var i = 1; i < transfers.Count; i++)
            {
                WriteSubRow(ws, r, transfers[i]);
                r++;
            }
        }

        ApplyExcelColumnWidths(ws);
        ws.Column(3).Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(5);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteMainRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, SoTransferDocumentLine? first)
    {
        var c = 1;
        ws.Cell(r, c++).Value = b.LineSeq;
        ws.Cell(r, c++).Value = b.ItemCode;
        ws.Cell(r, c++).Value = b.Description;
        ws.Cell(r, c++).Value = b.UnitPrice;
        ws.Cell(r, c++).Value = FormatShortDate(b.DeliveryDate);
        ws.Cell(r, c++).Value = b.OrigQty;
        ws.Cell(r, c++).Value = b.OutstandingQty;
        ws.Cell(r, c++).Value = FormatShortDate(first?.TransferDocDate);
        ws.Cell(r, c++).Value = first?.TransferDocNo ?? "";
        ws.Cell(r, c++).Value = first?.TransferQty ?? (decimal?)null;
        StyleNumericCols(ws, r);
        ws.Range(r, 1, r, ColCount).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static void WriteSubRow(IXLWorksheet ws, int r, SoTransferDocumentLine t)
    {
        for (var col = 1; col <= LeftCols; col++)
        {
            ws.Cell(r, col).Clear();
            ws.Cell(r, col).Style.Fill.BackgroundColor = XLColor.FromArgb(248, 250, 252);
        }

        ws.Cell(r, 8).Value = FormatShortDate(t.TransferDocDate);
        ws.Cell(r, 9).Value = t.TransferDocNo;
        ws.Cell(r, 10).Value = t.TransferQty;
        ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
        ws.Range(r, 1, r, ColCount).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static void StyleNumericCols(IXLWorksheet ws, int r)
    {
        ws.Cell(r, 4).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 10).Style.NumberFormat.Format = "#,##0.00";
    }

    /// <summary>Minimum widths so price/date/qty columns do not visually merge.</summary>
    private static void ApplyExcelColumnWidths(IXLWorksheet ws)
    {
        // ClosedXML column width is roughly “characters” for Calibri 11.
        double[] mins = [5, 12, 36, 11, 11, 11, 11, 11, 16, 11];
        for (var i = 0; i < mins.Length; i++)
        {
            var col = ws.Column(i + 1);
            col.Width = Math.Max(col.Width, mins[i]);
        }
    }

    private static string FormatShortDate(DateTime? d) =>
        d is { } dt ? dt.ToString("dd/MM/yy", DisplayCulture) : "";

    public byte[] BuildPdf(IReadOnlyList<SoTransferOutstandingBlock> blocks)
    {
        var asAt = DateTime.Today.ToString("dd/MM/yyyy", DisplayCulture);
        var runAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", DisplayCulture);
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(20);
                page.Size(PageSizes.A4.Landscape());
                page.DefaultTextStyle(x => x.FontSize(7));

                page.Header().Column(col =>
                {
                    col.Item().Text(ReportListingTitle).SemiBold().FontSize(11).AlignLeft();
                    col.Item().Text($"As at {asAt}").FontSize(9).AlignLeft();
                });

                page.Footer().PaddingTop(4).Row(row =>
                {
                    row.RelativeItem().AlignLeft().Text($"Run (generated): {runAt}")
                        .FontSize(8).FontColor(Colors.Grey.Darken2);
                    row.ConstantItem(100).AlignRight().Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8).FontColor(Colors.Grey.Darken2));
                        t.Span("Page ");
                        t.CurrentPageNumber();
                        t.Span(" of ");
                        t.TotalPages();
                    });
                });

                page.Content().PaddingTop(6).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(28);
                        c.ConstantColumn(64);
                        c.RelativeColumn(2.4f);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(52);
                        c.ConstantColumn(64);
                        c.ConstantColumn(52);
                    });

                    table.Header(h =>
                    {
                        foreach (var name in Headers)
                            h.Cell().Element(HCell).Text(name);
                    });

                    string? lastSoKey = null;
                    foreach (var b in blocks)
                    {
                        if (!string.Equals(b.SoDocKey, lastSoKey, StringComparison.Ordinal))
                        {
                            table.Cell().ColumnSpan(ColCount).Element(x => x
                                .Background(Colors.Grey.Lighten3)
                                .PaddingVertical(4)
                                .BorderBottom(0.5f)
                                .BorderColor(Colors.Grey.Lighten1)
                                .AlignLeft()
                                .DefaultTextStyle(s => s.SemiBold().FontSize(9))
                                .Text($"{b.SoDocNo}    {b.CompanyName}"));
                            lastSoKey = b.SoDocKey;
                        }

                        var transfers = b.Transfers;
                        var first = transfers.Count > 0 ? transfers[0] : null;
                        WritePdfMainRow(table, b, first);
                        for (var i = 1; i < transfers.Count; i++)
                            WritePdfSubRow(table, transfers[i]);
                    }
                });
            });
        });

        return doc.GeneratePdf();
    }

    private static IContainer HCell(IContainer x) =>
        x.DefaultTextStyle(s => s.SemiBold())
            .AlignLeft()
            .PaddingVertical(3)
            .BorderBottom(1)
            .BorderColor(Colors.Blue.Darken4);

    private static void WritePdfMainRow(TableDescriptor table, SoTransferOutstandingBlock b, SoTransferDocumentLine? first)
    {
        table.Cell().Element(BodyCell).Text(b.LineSeq.ToString(DisplayCulture));
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.UnitPrice.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.DeliveryDate));
        table.Cell().Element(BodyCell).Text(b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(FormatShortDate(first?.TransferDocDate));
        table.Cell().Element(BodyCell).Text(first?.TransferDocNo ?? "");
        table.Cell().Element(BodyCell).Text(first is null ? "" : first.TransferQty.ToString("N2", DisplayCulture));
    }

    private static void WritePdfSubRow(TableDescriptor table, SoTransferDocumentLine t)
    {
        for (var i = 0; i < LeftCols; i++)
            table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text(FormatShortDate(t.TransferDocDate));
        table.Cell().Element(SubCell).Text(t.TransferDocNo);
        table.Cell().Element(SubCell).Text(t.TransferQty.ToString("N2", DisplayCulture));
    }

    private static IContainer BodyCell(IContainer x) =>
        x.AlignLeft().PaddingVertical(2).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

    private static IContainer SubCell(IContainer x) =>
        x.AlignLeft().Background(Colors.Grey.Lighten4).PaddingVertical(2).BorderBottom(0.5f).BorderColor(Colors.Grey.Lighten2);

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return s[..(max - 1)] + "…";
    }
}
