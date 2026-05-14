using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>Excel + PDF for SO outstanding transfer. Column order aligned; transfer sub-rows omit repeated SO/line fields (same as PDF).</summary>
public sealed class SoTransferOutstandingExportBuilder
{
    /// <summary>Banner title on exports (print / PDF).</summary>
    private const string ReportListingTitle = "Outstanding sales order listing";
    /// <summary>PDF table columns (group header row shows SO + company).</summary>
    private const int ColCount = 11;
    private const int ExcelFlatColCount = 13;
    /// <summary>PDF sub-rows: blank SO Doc Date, Seq.–Description, Orig, O/S, delivery; Ext. No + Tfer + Date + Doc No only (line totals carry orig / outstanding).</summary>
    private const int LeftCols = 3;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>PDF columns: no unit price; <c>Ext. No</c> = <c>SL_SO.DOCNOEX</c>; orig / transfer / doc then O/S + delivery after transfer doc no.</summary>
    private static readonly string[] Headers =
    [
        "SO Doc Date",
        "Seq.",
        "Code",
        "Description",
        "Ext. No",
        "Orig Qty",
        "Tfer Qty",
        "Date",
        "Doc No",
        "O/Stding",
        "Delivy date"
    ];

    /// <summary>Excel flat import: same column order as PDF table + leading SO No / Company; total Tfer row then one row per transfer.</summary>
    private static readonly string[] ExcelFlatHeaders =
    [
        "SO No",
        "Company Name",
        "SO Doc Date",
        "Seq.",
        "Code",
        "Description",
        "Ext. No",
        "Orig. Qty",
        "Tfer Qty",
        "Date",
        "Doc No",
        "O/S Qty",
        "Delivy date"
    ];

    public byte[] BuildExcel(IReadOnlyList<SoTransferOutstandingBlock> blocks)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("SO Transfer");

        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

        var r = 1;
        for (var c = 0; c < ExcelFlatHeaders.Length; c++)
            ws.Cell(r, c + 1).Value = ExcelFlatHeaders[c];
        var headerRow = ws.Range(r, 1, r, ExcelFlatColCount);
        headerRow.Style.Font.Bold = true;
        headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        headerRow.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        headerRow.Style.Border.BottomBorder = XLBorderStyleValues.Thin;
        r++;

        foreach (var b in blocks)
        {
            var transfers = b.Transfers;
            if (transfers.Count == 0)
            {
                WriteExcelLineWithoutTransfers(ws, r, b);
                r++;
            }
            else
            {
                WriteExcelTransferTotalRow(ws, r, b, SumTransferQty(transfers));
                r++;
                foreach (var t in transfers)
                {
                    WriteExcelTransferDetailRow(ws, r, b, t);
                    r++;
                }
            }
        }

        ApplyExcelFlatColumnWidths(ws);
        ws.Column(6).Style.Alignment.WrapText = true;
        ws.Column(1).Style.NumberFormat.Format = "@";
        ws.SheetView.FreezeRows(1);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>No transfers: empty Tfer / Date / Doc No.</summary>
    private static void WriteExcelLineWithoutTransfers(IXLWorksheet ws, int r, SoTransferOutstandingBlock b)
    {
        WriteExcelCommonLeadingColumns(ws, r, b);
        ws.Cell(r, 8).Value = b.OrigQty;
        ws.Cell(r, 9).Clear();
        ws.Cell(r, 10).Clear();
        ws.Cell(r, 11).Clear();
        ws.Cell(r, 12).Value = b.OutstandingQty;
        var deliveryCell = ws.Cell(r, 13);
        if (b.DeliveryDate is { } del)
            deliveryCell.Value = del.Date;
        else
            deliveryCell.Clear();
        StyleExcelRow(ws, r);
        ws.Cell(r, 8).Style.Font.Bold = true;
        ws.Cell(r, 12).Style.Font.Bold = true;
    }

    /// <summary>Sum of transfer qty; blank transfer doc date/no (matches PDF total row).</summary>
    private static void WriteExcelTransferTotalRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, decimal totalTransferQty)
    {
        WriteExcelCommonLeadingColumns(ws, r, b);
        ws.Cell(r, 8).Value = b.OrigQty;
        ws.Cell(r, 9).Value = totalTransferQty;
        ws.Cell(r, 10).Clear();
        ws.Cell(r, 11).Clear();
        ws.Cell(r, 12).Value = b.OutstandingQty;
        var deliveryCell = ws.Cell(r, 13);
        if (b.DeliveryDate is { } del)
            deliveryCell.Value = del.Date;
        else
            deliveryCell.Clear();
        StyleExcelRow(ws, r);
        ws.Cell(r, 8).Style.Font.Bold = true;
        ws.Cell(r, 12).Style.Font.Bold = true;
    }

    private static void WriteExcelTransferDetailRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, SoTransferDocumentLine t)
    {
        WriteExcelCommonLeadingColumns(ws, r, b);
        for (var c = 1; c <= 6; c++)
            ws.Cell(r, c).Clear();
        ws.Cell(r, 7).Value = b.SoDocNoEx;
        ws.Cell(r, 8).Clear();
        ws.Cell(r, 9).Value = t.TransferQty;
        var td = ws.Cell(r, 10);
        if (t.TransferDocDate is { } dtd)
            td.Value = dtd.Date;
        else
            td.Clear();
        ws.Cell(r, 11).Value = t.TransferDocNo;
        ws.Cell(r, 12).Clear();
        ws.Cell(r, 13).Clear();
        ws.Range(r, 1, r, ExcelFlatColCount).Style.Fill.BackgroundColor = XLColor.FromArgb(248, 250, 252);
        StyleExcelRow(ws, r);
    }

    private static void WriteExcelCommonLeadingColumns(IXLWorksheet ws, int r, SoTransferOutstandingBlock b)
    {
        var c = 1;
        ws.Cell(r, c++).Value = b.SoDocNo;
        ws.Cell(r, c++).Value = b.CompanyName;
        var soDateCell = ws.Cell(r, c++);
        if (b.SoDocDate is { } sd)
            soDateCell.Value = sd.Date;
        else
            soDateCell.Clear();
        ws.Cell(r, c++).Value = b.LineSeq;
        ws.Cell(r, c++).Value = b.ItemCode;
        ws.Cell(r, c++).Value = b.Description;
        ws.Cell(r, c++).Value = b.SoDocNoEx;
    }

    private static void StyleExcelRow(IXLWorksheet ws, int r)
    {
        StyleExcelFlatNumericCols(ws, r);
        StyleExcelFlatDateCols(ws, r);
        ws.Range(r, 1, r, ExcelFlatColCount).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static void StyleExcelFlatNumericCols(IXLWorksheet ws, int r)
    {
        ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 12).Style.NumberFormat.Format = "#,##0.00";
    }

    private static void StyleExcelFlatDateCols(IXLWorksheet ws, int r)
    {
        const string dFmt = "dd/MM/yyyy";
        ws.Cell(r, 3).Style.DateFormat.Format = dFmt;
        ws.Cell(r, 10).Style.DateFormat.Format = dFmt;
        ws.Cell(r, 13).Style.DateFormat.Format = dFmt;
    }

    /// <summary>Minimum widths so price/date/qty columns do not visually merge (flat import layout).</summary>
    private static void ApplyExcelFlatColumnWidths(IXLWorksheet ws)
    {
        // ClosedXML column width is roughly “characters” for Calibri 11.
        double[] mins = [12, 28, 11, 5, 14, 40, 16, 11, 11, 11, 18, 11, 12];
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
                        c.ConstantColumn(44);
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
                                .AlignLeft()
                                .DefaultTextStyle(s => s.SemiBold().FontSize(9))
                                .Text($"{b.SoDocNo}    {b.CompanyName}"));
                            lastSoKey = b.SoDocKey;
                        }

                        var transfers = b.Transfers;
                        if (transfers.Count == 0)
                            WritePdfLineWithoutTransfers(table, b);
                        else
                        {
                            WritePdfTransferTotalRow(table, b, SumTransferQty(transfers));
                            foreach (var t in transfers)
                                WritePdfTransferDetailRow(table, b, t);
                        }
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
            .Background(Colors.Grey.Lighten3)
            .BorderBottom(0.5f)
            .BorderColor(Colors.Grey.Medium);

    private static decimal SumTransferQty(IReadOnlyList<SoTransferDocumentLine> transfers)
    {
        decimal s = 0;
        foreach (var t in transfers)
            s += t.TransferQty;
        return s;
    }

    /// <summary>SO line with no transfer rows — empty transfer columns.</summary>
    private static void WritePdfLineWithoutTransfers(TableDescriptor table, SoTransferOutstandingBlock b)
    {
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.SoDocDate));
        table.Cell().Element(BodyCell).Text(b.LineSeq.ToString(DisplayCulture));
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.SoDocNoEx);
        PdfQtyCellSemiBold(table, b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
        PdfQtyCellSemiBold(table, b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.DeliveryDate));
    }

    /// <summary>First row under the line when transfers exist: same line fields, Tfer = sum of all transfer qty, no transfer doc date/no.</summary>
    private static void WritePdfTransferTotalRow(TableDescriptor table, SoTransferOutstandingBlock b, decimal totalTransferQty)
    {
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.SoDocDate));
        table.Cell().Element(BodyCell).Text(b.LineSeq.ToString(DisplayCulture));
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.SoDocNoEx);
        PdfQtyCellSemiBold(table, b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(totalTransferQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
        PdfQtyCellSemiBold(table, b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.DeliveryDate));
    }

    private static void WritePdfTransferDetailRow(TableDescriptor table, SoTransferOutstandingBlock b, SoTransferDocumentLine t)
    {
        table.Cell().Element(SubCell).Text("");
        for (var i = 0; i < LeftCols; i++)
            table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text(b.SoDocNoEx);
        table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text(t.TransferQty.ToString("N2", DisplayCulture));
        table.Cell().Element(SubCell).Text(FormatShortDate(t.TransferDocDate));
        table.Cell().Element(SubCell).Text(t.TransferDocNo);
        table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text("");
    }

    private static void PdfQtyCellSemiBold(TableDescriptor table, string formattedQty) =>
        table.Cell().Element(BodyCell).Text(t =>
        {
            t.DefaultTextStyle(s => s.SemiBold());
            t.Span(formattedQty);
        });

    private static IContainer BodyCell(IContainer x) =>
        x.AlignLeft().PaddingVertical(2);

    private static IContainer SubCell(IContainer x) =>
        x.AlignLeft().Background(Colors.Grey.Lighten4).PaddingVertical(2);

    private static string Truncate(string s, int max)
    {
        if (string.IsNullOrEmpty(s) || s.Length <= max)
            return s;
        return s[..(max - 1)] + "…";
    }
}
