using System.Globalization;
using ClosedXML.Excel;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using SqlAccountingEmailWorker.Models;

namespace SqlAccountingEmailWorker.Services;

/// <summary>Excel + PDF for SO outstanding transfer. PDF keeps ERP-style group rows; Excel is flat (one row per transfer) for import.</summary>
public sealed class SoTransferOutstandingExportBuilder
{
    /// <summary>Banner title on exports (print / PDF).</summary>
    private const string ReportListingTitle = "Outstanding sales order listing";
    private const int ColCount = 10;
    private const int ExcelFlatColCount = 9;
    private const int LeftCols = 7;
    private static readonly CultureInfo DisplayCulture = CultureInfo.GetCultureInfo("en-GB");

    /// <summary>PDF table columns (grouped layout).</summary>
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

    /// <summary>Excel import layout: one row per transfer; columns match import spec (no sequence).</summary>
    private static readonly string[] ExcelFlatHeaders =
    [
        "Date",
        "Company Name",
        "Item Code",
        "Description",
        "Ext. No",
        "Orig. Qty",
        "Transfer Qty",
        "O/S Qty",
        "Delivery Date"
    ];

    public byte[] BuildExcel(IReadOnlyList<SoTransferOutstandingBlock> blocks)
    {
        var asAt = DateTime.Today.ToString("dd/MM/yyyy", DisplayCulture);
        var runAt = DateTime.Now.ToString("dd/MM/yyyy HH:mm:ss", DisplayCulture);

        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("SO Transfer");
        ws.Cell(1, 1).Value = ReportListingTitle;
        ws.Range(1, 1, 1, ExcelFlatColCount).Merge();
        ws.Row(1).Style.Font.Bold = true;
        ws.Row(1).Style.Font.FontSize = 12;
        ws.Row(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Cell(2, 1).Value = $"As at {asAt}";
        ws.Range(2, 1, 2, ExcelFlatColCount).Merge();
        ws.Row(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;

        ws.Cell(3, 1).Value = $"Run (generated): {runAt}";
        ws.Range(3, 1, 3, ExcelFlatColCount).Merge();
        ws.Row(3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
        ws.Row(3).Style.Font.FontColor = XLColor.DimGray;

        ws.PageSetup.Footer.Left.AddText($"As at {asAt}", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Center.AddText($"Run {runAt}", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText("Page ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.PageNumber, XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(" of ", XLHFOccurrence.AllPages);
        ws.PageSetup.Footer.Right.AddText(XLHFPredefinedText.NumberOfPages, XLHFOccurrence.AllPages);

        var r = 5;
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
                WriteExcelFlatRow(ws, r, b, null);
                r++;
            }
            else
            {
                foreach (var t in transfers)
                {
                    WriteExcelFlatRow(ws, r, b, t);
                    r++;
                }
            }
        }

        ApplyExcelFlatColumnWidths(ws);
        ws.Column(4).Style.Alignment.WrapText = true;
        ws.SheetView.FreezeRows(5);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>One import row per transfer. <paramref name="t"/> null = blank transfer date/qty. Ext. No = SO document number.</summary>
    private static void WriteExcelFlatRow(IXLWorksheet ws, int r, SoTransferOutstandingBlock b, SoTransferDocumentLine? t)
    {
        var c = 1;
        var dateCell = ws.Cell(r, c++);
        if (t?.TransferDocDate is { } transferDate)
            dateCell.Value = transferDate.Date;
        else
            dateCell.Clear();

        ws.Cell(r, c++).Value = b.CompanyName;
        ws.Cell(r, c++).Value = b.ItemCode;
        ws.Cell(r, c++).Value = b.Description;
        ws.Cell(r, c++).Value = b.SoDocNo;
        ws.Cell(r, c++).Value = b.OrigQty;
        ws.Cell(r, c++).Value = t is null ? (decimal?)null : t.TransferQty;
        ws.Cell(r, c++).Value = b.OutstandingQty;

        var deliveryCell = ws.Cell(r, c);
        if (b.DeliveryDate is { } del)
            deliveryCell.Value = del.Date;
        else
            deliveryCell.Clear();

        StyleExcelFlatNumericCols(ws, r);
        StyleExcelFlatDateCols(ws, r);
        ws.Range(r, 1, r, ExcelFlatColCount).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Left;
    }

    private static void StyleExcelFlatNumericCols(IXLWorksheet ws, int r)
    {
        ws.Cell(r, 6).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 7).Style.NumberFormat.Format = "#,##0.00";
        ws.Cell(r, 8).Style.NumberFormat.Format = "#,##0.00";
    }

    private static void StyleExcelFlatDateCols(IXLWorksheet ws, int r)
    {
        const string dFmt = "dd/MM/yyyy";
        ws.Cell(r, 1).Style.DateFormat.Format = dFmt;
        ws.Cell(r, 9).Style.DateFormat.Format = dFmt;
    }

    /// <summary>Minimum widths so price/date/qty columns do not visually merge (flat import layout).</summary>
    private static void ApplyExcelFlatColumnWidths(IXLWorksheet ws)
    {
        // ClosedXML column width is roughly “characters” for Calibri 11.
        double[] mins = [11, 28, 14, 40, 14, 11, 11, 11, 12];
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
