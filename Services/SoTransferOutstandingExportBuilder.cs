using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>Excel + PDF for SO outstanding transfer. PDF omits Seq.; Excel matches PDF columns (no Seq.).</summary>
public sealed class SoTransferOutstandingExportBuilder
{
    /// <summary>PDF/Excel: <b>Transfer doc date</b> = target <c>DOCDATE</c> (<see cref="SoTransferDocumentLine.TransferDocDate"/>). Delivery is not shown as a column.</summary>
    private const int ColCount = 9;
    private const int ExcelFlatColCount = 11;
    /// <summary>PDF transfer sub-rows: blank SO date + Code + Description (indented block).</summary>
    private const int LeftCols = 2;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>Title line for email subject, PDF header, and Excel (matches user-facing wording).</summary>
    public static string FormatOutstandingReportTitle(DateOnly asOf) =>
        $"Outstanding Sales Orders as of {asOf.ToString("dddd, d MMMM yyyy", DisplayCulture)}";

    private static readonly string[] PdfColumnHeaders =
    [
        "SO Doc Date",
        "Code",
        "Description",
        "Ext. No",
        "Orig Qty",
        "Tfer Qty",
        "O/Stding",
        "Transfer doc date",
        "Doc No"
    ];

    /// <summary>Excel flat import: same columns as PDF + leading SO No / Company.</summary>
    private static readonly string[] ExcelFlatHeaders =
    [
        "SO No",
        "Company Name",
        "SO Doc Date",
        "Code",
        "Description",
        "Ext. No",
        "Orig. Qty",
        "Tfer Qty",
        "O/S Qty",
        "Transfer doc date",
        "Doc No"
    ];

    public byte[] BuildExcel(IReadOnlyList<SoTransferOutstandingBlock> blocks, DateOnly asOfDate)
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("SO Transfer");

        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

        var r = 1;
        ws.Range(r, 1, r, ExcelFlatColCount).Merge();
        ws.Cell(r, 1).Value = FormatOutstandingReportTitle(asOfDate);
        ws.Cell(r, 1).Style.Font.Bold = true;
        ws.Cell(r, 1).Style.Font.FontSize = 14;
        ws.Cell(r, 1).Style.Alignment.WrapText = true;
        ws.Cell(r, 1).Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
        ws.Row(r).Height = 36;
        r++;

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
        ws.Column(5).Style.Alignment.WrapText = true;
        ws.Column(11).Style.Alignment.WrapText = true;
        ws.Column(1).Style.NumberFormat.Format = "@";
        ws.SheetView.FreezeRows(2);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>No transfers: empty Tfer / Doc No.</summary>
    private static void WriteExcelLineWithoutTransfers(IXLWorksheet ws, int r, SoTransferOutstandingBlock b)
    {
        WriteExcelCommonLeadingColumns(ws, r, b);
        ws.Cell(r, 7).Value = b.OrigQty;
        ws.Cell(r, 8).Clear();
        ws.Cell(r, 9).Value = b.OutstandingQty;
        ws.Cell(r, 10).Clear();
        ws.Cell(r, 11).Clear();
        StyleExcelRow(ws, r);
        ws.Cell(r, 7).Style.Font.Bold = true;
        ws.Cell(r, 8).Style.Font.Bold = true;
        ws.Cell(r, 9).Style.Font.Bold = true;
    }

    /// <summary>Sum of transfer qty; blank transfer doc no (matches PDF total row).</summary>
    private static void WriteExcelTransferTotalRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, decimal totalTransferQty)
    {
        WriteExcelCommonLeadingColumns(ws, r, b);
        ws.Cell(r, 7).Value = b.OrigQty;
        ws.Cell(r, 8).Value = totalTransferQty;
        ws.Cell(r, 9).Value = b.OutstandingQty;
        ws.Cell(r, 10).Clear();
        ws.Cell(r, 11).Clear();
        StyleExcelRow(ws, r);
        ws.Cell(r, 7).Style.Font.Bold = true;
        ws.Cell(r, 8).Style.Font.Bold = true;
        ws.Cell(r, 9).Style.Font.Bold = true;
    }

    private static void WriteExcelTransferDetailRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, SoTransferDocumentLine t)
    {
        WriteExcelCommonLeadingColumns(ws, r, b);
        for (var c = 1; c <= 5; c++)
            ws.Cell(r, c).Clear();
        ws.Cell(r, 6).Value = b.SoDocNoEx;
        ws.Cell(r, 7).Clear();
        ws.Cell(r, 8).Value = t.TransferQty;
        ws.Cell(r, 9).Value = 0m;
        SetExcelSoDeliveryDateCell(ws, r, 10, t.TransferDocDate);
        ws.Cell(r, 11).Value = t.TransferDocNo;
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
        ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 9).Style.NumberFormat.Format = "#,##0.00";
    }

    private static void StyleExcelFlatDateCols(IXLWorksheet ws, int r)
    {
        const string dFmt = "dd/MM/yyyy";
        ws.Cell(r, 3).Style.DateFormat.Format = dFmt;
        ws.Cell(r, 10).Style.DateFormat.Format = dFmt;
    }

    /// <summary>Minimum widths so price/date/qty columns do not visually merge (flat import layout).</summary>
    private static void ApplyExcelFlatColumnWidths(IXLWorksheet ws)
    {
        // ClosedXML column width is roughly “characters” for Calibri 11.
        double[] mins = [12, 28, 11, 14, 36, 16, 11, 11, 11, 11, 20];
        for (var i = 0; i < mins.Length; i++)
        {
            var col = ws.Column(i + 1);
            col.Width = Math.Max(col.Width, mins[i]);
        }
    }

    private static string FormatShortDate(DateTime? d) =>
        d is { } dt ? dt.ToString("dd/MM/yy", DisplayCulture) : "";

    /// <summary>Writes a nullable date into an Excel cell (used for <see cref="SoTransferDocumentLine.TransferDocDate"/>).</summary>
    private static void SetExcelSoDeliveryDateCell(IXLWorksheet ws, int r, int col, DateTime? deliveryDate)
    {
        var cell = ws.Cell(r, col);
        if (deliveryDate is { } dd)
            cell.Value = dd.Date;
        else
            cell.Clear();
    }

    public byte[] BuildPdf(IReadOnlyList<SoTransferOutstandingBlock> blocks, DateOnly asOfDate)
    {
        var runAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", DisplayCulture);
        var reportTitle = FormatOutstandingReportTitle(asOfDate);
        var doc = Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Margin(20);
                page.Size(PageSizes.A4.Landscape());
                page.DefaultTextStyle(x => x.FontSize(7));

                page.Header().Column(col =>
                {
                    col.Item().Text(reportTitle).SemiBold().FontSize(11).AlignLeft();
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
                        c.ConstantColumn(42);
                        c.ConstantColumn(58);
                        c.RelativeColumn(2.35f);
                        c.ConstantColumn(48);
                        c.ConstantColumn(48);
                        c.ConstantColumn(48);
                        c.ConstantColumn(48);
                        c.ConstantColumn(46);
                        c.ConstantColumn(58);
                    });

                    table.Header(h =>
                    {
                        foreach (var name in PdfColumnHeaders)
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
            .Background(Colors.Grey.Lighten3);

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
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.SoDocNoEx);
        PdfQtyCellSemiBold(table, b.OrigQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        PdfQtyCellSemiBold(table, b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
    }

    /// <summary>First row under the line when transfers exist: same line fields, Tfer = sum of all transfer qty, no transfer doc no.</summary>
    private static void WritePdfTransferTotalRow(TableDescriptor table, SoTransferOutstandingBlock b, decimal totalTransferQty)
    {
        table.Cell().Element(BodyCell).Text(FormatShortDate(b.SoDocDate));
        table.Cell().Element(BodyCell).Text(b.ItemCode);
        table.Cell().Element(BodyCell).Text(Truncate(b.Description, 72));
        table.Cell().Element(BodyCell).Text(b.SoDocNoEx);
        PdfQtyCellSemiBold(table, b.OrigQty.ToString("N2", DisplayCulture));
        PdfQtyCellSemiBold(table, totalTransferQty.ToString("N2", DisplayCulture));
        PdfQtyCellSemiBold(table, b.OutstandingQty.ToString("N2", DisplayCulture));
        table.Cell().Element(BodyCell).Text("");
        table.Cell().Element(BodyCell).Text("");
    }

    private static void WritePdfTransferDetailRow(TableDescriptor table, SoTransferOutstandingBlock b, SoTransferDocumentLine t)
    {
        table.Cell().Element(SubCell).Text("");
        for (var i = 0; i < LeftCols; i++)
            table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text(b.SoDocNoEx);
        table.Cell().Element(SubCell).Text("");
        table.Cell().Element(SubCell).Text(t.TransferQty.ToString("N2", DisplayCulture));
        PdfQtySubCellSemiBold(table, "0.00");
        table.Cell().Element(SubCell).Text(FormatShortDate(t.TransferDocDate));
        table.Cell().Element(SubCell).Text(t.TransferDocNo);
    }

    private static void PdfQtyCellSemiBold(TableDescriptor table, string formattedQty) =>
        table.Cell().Element(BodyCell).Text(t =>
        {
            t.DefaultTextStyle(s => s.SemiBold());
            t.Span(formattedQty);
        });

    private static void PdfQtySubCellSemiBold(TableDescriptor table, string formattedQty) =>
        table.Cell().Element(SubCell).Text(t =>
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
