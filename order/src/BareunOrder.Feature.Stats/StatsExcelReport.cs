using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using BareunOrder.Core.Models;

namespace BareunOrder.Feature.Stats;

/// <summary>
/// 통계를 서식 있는 엑셀 파일(.xlsx)로 저장한다.
/// 외부 패키지 없이 .xlsx(XML 파일 묶음)를 직접 만든다 → Directory.Packages.props(공통)를 고치지 않아도 된다.
/// 값은 모두 StatsCalculator 결과(StatsResult)를 그대로 쓴다 → 화면·보고서와 숫자가 같다.
///
/// 시트 3개
///   - 요약: 기간, 핵심 지표(이번 기간 / 이전 기간 / 증감), 주문 상태별 건수(출고·배송), 상품별 매출, 시간대별 주문 수
///   - 일별: 날짜마다 주문 수·매출·판매 팩·취소 (일일 1줄, 주간 7줄, 월간 28~31줄)
///   - 주문 목록: 주문 한 건이 한 줄. 취소 주문은 회색, 합계에서 뺀다. 연락처는 가린 번호, 주소는 넣지 않는다.
/// </summary>
internal static class StatsExcelReport
{
    public static void Save(string path, PeriodKind kind, StatsResult now, StatsResult before)
    {
        var valid = now.Orders.Where(o => o.Status != OrderStatus.Canceled).ToList();
        var kindName = StatsPeriod.KindName(kind);
        var prevName = StatsPeriod.PreviousName(kind);
        var period = StatsPeriod.RangeText(now.Start, now.End);
        var prevPeriod = StatsPeriod.RangeText(before.Start, before.End);
        var written = $"{DateTime.Now:yyyy-MM-dd HH:mm}";

        // ═══ 시트 1: 요약 ═══
        var sum = new Sheet("요약", 32, 16, 16, 16) { Selected = true };
        sum.Text(1, 1, $"바른전지 주문 통계 보고서 ({kindName})", St.Title);
        sum.Merge(1, 1, 1, 4);
        sum.Height(1, 32);
        sum.Text(3, 1, "기간", St.InfoLabel);
        sum.Text(3, 2, period, St.InfoValue);
        sum.Merge(3, 2, 3, 4);
        sum.Text(4, 1, $"비교 기간 ({prevName})", St.InfoLabel);
        sum.Text(4, 2, prevPeriod, St.InfoValue);
        sum.Merge(4, 2, 4, 4);
        sum.Text(5, 1, "작성 시각", St.InfoLabel);
        sum.Text(5, 2, written, St.InfoValue);
        sum.Merge(5, 2, 5, 4);

        // 핵심 지표: 이번 기간 / 이전 기간 / 증감
        var r = 7;
        sum.Section(r++, "핵심 지표", 4);
        sum.Header(r++, "항목", "이번 기간", prevName, "증감");
        void Kpi(string label, double a, double b, int style, int diffStyle)
        {
            sum.Text(r, 1, label, St.Text);
            sum.Number(r, 2, a, style);
            sum.Number(r, 3, b, style);
            sum.Number(r, 4, Math.Round(a - b, 1), diffStyle);
            r++;
        }
        Kpi("주문 수 (취소 제외)", now.OrderCount, before.OrderCount, St.Count, St.DiffInt);
        Kpi("매출", now.Sales, before.Sales, St.Won, St.DiffWon);
        Kpi("주문당 평균", now.AverageOrder, before.AverageOrder, St.Won, St.DiffWon);
        Kpi("판매 팩 수", now.PackCount, before.PackCount, St.Pack, St.DiffInt);
        Kpi("판매 건전지 수", now.BatteryCount, before.BatteryCount, St.Int, St.DiffInt);
        Kpi("취소 건수", now.CancelCount, before.CancelCount, St.Count, St.DiffInt);
        Kpi("취소율", Math.Round(now.CancelRate, 1), Math.Round(before.CancelRate, 1), St.Pct, St.DiffPct);
        Kpi("출고·배송 건수 (배송 중 + 배송 완료)", now.ShippedCount, before.ShippedCount, St.Count, St.DiffInt);

        // 주문 상태별 건수
        r++;
        sum.Section(r++, "주문 상태별 건수 (취소 포함)", 2);
        sum.Header(r++, "상태", "건수");
        foreach (var st in now.ByStatus)
        {
            sum.Text(r, 1, st.Name, St.Text);
            sum.Number(r, 2, st.Count, St.Count);
            r++;
        }
        sum.Text(r, 1, "합계", St.TotalText);
        sum.Number(r, 2, now.Orders.Count, St.TotalCount);
        r++;

        // 상품별 매출
        r++;
        sum.Section(r++, "상품별 매출 (취소 제외)", 4);
        sum.Header(r++, "상품", "판매 팩", "매출", "비중");
        var totalSales = Math.Max(1, now.Sales);
        foreach (var p in now.ByProduct)
        {
            sum.Text(r, 1, p.Name, St.Text);
            sum.Number(r, 2, p.Packs, St.Pack);
            sum.Number(r, 3, p.Sales, St.Won);
            sum.Number(r, 4, Math.Round(100.0 * p.Sales / totalSales, 1), St.Pct);
            r++;
        }
        sum.Text(r, 1, "합계", St.TotalText);
        sum.Number(r, 2, now.PackCount, St.TotalPack);
        sum.Number(r, 3, now.Sales, St.TotalWon);
        sum.Number(r, 4, now.Sales == 0 ? 0 : 100, St.TotalPct);
        r++;

        // 시간대별 주문 수
        r++;
        sum.Section(r++, "시간대별 주문 수 (취소 제외)", 2);
        sum.Header(r++, "시간", "주문 수");
        foreach (var h in now.ByHour)
        {
            sum.Text(r, 1, $"{h.Hour}시", St.Center);
            sum.Number(r, 2, h.Orders, St.Count);
            r++;
        }
        sum.Text(r, 1, "합계", St.TotalText);
        sum.Number(r, 2, now.ByHour.Sum(h => h.Orders), St.TotalCount);

        // ═══ 시트 2: 일별 ═══
        var day = new Sheet("일별", 16, 8, 13, 16, 12, 10) { FreezeRows = 4 };
        day.Text(1, 1, $"일별 주문·매출 ({kindName})", St.Title);
        day.Merge(1, 1, 1, 6);
        day.Height(1, 32);
        day.Text(2, 1, "기간", St.InfoLabel);
        day.Text(2, 2, period, St.InfoValue);
        day.Merge(2, 2, 2, 6);
        day.Header(4, "날짜", "요일", "주문 수", "매출", "판매 팩", "취소");
        r = 5;
        foreach (var d in now.ByDay)
        {
            var weekend = d.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            day.Text(r, 1, $"{d.Date:yyyy-MM-dd}", weekend ? St.OffCenter : St.Center);
            day.Text(r, 2, StatsPeriod.Weekday(d.Date), weekend ? St.OffCenter : St.Center);
            day.Number(r, 3, d.Orders, St.Count);
            day.Number(r, 4, d.Sales, St.Won);
            day.Number(r, 5, d.Packs, St.Pack);
            day.Number(r, 6, d.Canceled, St.Count);
            r++;
        }
        day.Text(r, 1, "합계", St.TotalText);
        day.Blank(r, 2, St.TotalText);
        day.Merge(r, 1, r, 2);
        day.Number(r, 3, now.OrderCount, St.TotalCount);
        day.Number(r, 4, now.Sales, St.TotalWon);
        day.Number(r, 5, now.PackCount, St.TotalPack);
        day.Number(r, 6, now.CancelCount, St.TotalCount);
        r += 2;
        day.Text(r, 1, "주문 수·매출·판매 팩은 취소 제외. 토·일은 회색 글자.", St.Note);

        // ═══ 시트 3: 주문 목록 ═══
        var list = new Sheet("주문 목록", 19, 17, 11, 10, 15, 44, 9, 11, 13, 13, 16) { Landscape = true, FreezeRows = 4 };
        const int lastCol = 11;
        list.Text(1, 1, "주문 목록", St.Title);
        list.Merge(1, 1, 1, lastCol);
        list.Height(1, 32);
        list.Text(2, 1, "기간", St.InfoLabel);
        list.Text(2, 2, period, St.InfoValue);
        list.Merge(2, 2, 2, 6);
        list.Header(4, "주문번호", "접수 시각", "상태", "고객", "연락처", "상품", "팩 수", "건전지 수", "금액", "택배사", "송장번호");

        r = 5;
        foreach (var o in now.Orders)
        {
            var off = o.Status == OrderStatus.Canceled;
            var text = off ? St.OffText : St.Text;
            var center = off ? St.OffCenter : St.Center;
            list.Text(r, 1, o.No, center);
            list.Text(r, 2, $"{o.ReceivedAt:yyyy-MM-dd HH:mm}", center);
            list.Text(r, 3, o.StatusText, center);
            list.Text(r, 4, o.Name, center);
            list.Text(r, 5, o.MaskedPhone, center);
            list.Text(r, 6, string.Join(" / ", o.Items.Select(i => $"{i.Name} ×{i.Qty}")), text);
            list.Number(r, 7, o.PackCount, off ? St.OffInt : St.Int);
            list.Number(r, 8, o.BatteryCount, off ? St.OffInt : St.Int);
            list.Number(r, 9, o.Total, off ? St.OffWon : St.Won);
            list.Text(r, 10, o.Courier ?? "", center);
            list.Text(r, 11, o.Tracking ?? "", center);   // 글자로 넣으므로 6.12E+11처럼 바뀌지 않는다
            r++;
        }
        list.AutoFilter = $"A4:{Sheet.Ref(Math.Max(4, r - 1), lastCol)}";

        list.Text(r, 1, "합계 (취소 제외)", St.TotalText);
        for (var c = 2; c <= 6; c++) list.Blank(r, c, St.TotalText);
        list.Merge(r, 1, r, 6);
        list.Number(r, 7, now.PackCount, St.TotalInt);
        list.Number(r, 8, now.BatteryCount, St.TotalInt);
        list.Number(r, 9, now.Sales, St.TotalWon);
        list.Blank(r, 10, St.TotalText);
        list.Blank(r, 11, St.TotalText);
        r += 2;
        list.Text(r, 1, $"주문 {now.Orders.Count}건 (취소 {now.CancelCount}건). 취소 주문은 회색으로 표시하고 합계에서 뺐습니다. 연락처는 가운데를 가렸습니다.", St.Note);

        WriteWorkbook(path, new[] { sum, day, list });
    }

    // ───────────────────────── 셀 서식 번호 (아래 StylesXml의 cellXfs 순서와 같아야 함) ─────────────────────────
    private static class St
    {
        public const int Title = 1, InfoLabel = 2, InfoValue = 3, Section = 4, Header = 5;
        public const int Text = 6, Center = 7, Int = 8, Won = 9, Pct = 10, Count = 11, Pack = 12;
        public const int TotalText = 13, TotalInt = 14, TotalWon = 15, TotalPct = 16, TotalCount = 17, TotalPack = 18;
        public const int OffText = 19, OffCenter = 20, OffInt = 21, OffWon = 22, Note = 23;
        public const int DiffInt = 24, DiffWon = 25, DiffPct = 26;
    }

    private static readonly string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"7\">" +
        "<numFmt numFmtId=\"164\" formatCode=\"#,##0&quot;원&quot;\"/>" +
        "<numFmt numFmtId=\"165\" formatCode=\"0.0&quot;%&quot;\"/>" +
        "<numFmt numFmtId=\"166\" formatCode=\"#,##0&quot;건&quot;\"/>" +
        "<numFmt numFmtId=\"167\" formatCode=\"#,##0&quot;팩&quot;\"/>" +
        "<numFmt numFmtId=\"168\" formatCode=\"+#,##0;-#,##0;0\"/>" +
        "<numFmt numFmtId=\"169\" formatCode=\"+#,##0&quot;원&quot;;-#,##0&quot;원&quot;;0&quot;원&quot;\"/>" +
        "<numFmt numFmtId=\"170\" formatCode=\"+0.0&quot;%p&quot;;-0.0&quot;%p&quot;;0.0&quot;%p&quot;\"/>" +
        "</numFmts>" +
        "<fonts count=\"5\">" +
        "<font><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"16\"/><color rgb=\"FF1F4E79\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><sz val=\"11\"/><color rgb=\"FF808080\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "</fonts>" +
        "<fills count=\"5\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E79\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>" +
        "<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"27\">" +
        Xf(0, 0, 0, 0, null) +              // 0 기본
        Xf(1, 0, 0, 0, "left") +            // 1 제목
        Xf(2, 0, 0, 0, "left") +            // 2 기간 등 이름
        Xf(0, 0, 0, 0, "left") +            // 3 기간 등 값
        Xf(3, 2, 0, 0, "left") +            // 4 구역 제목 (남색 띠)
        Xf(2, 3, 1, 0, "center") +          // 5 표 머리글
        Xf(0, 0, 1, 0, "left") +            // 6 글자
        Xf(0, 0, 1, 0, "center") +          // 7 가운데 글자
        Xf(0, 0, 1, 3, null) +              // 8 숫자 1,234
        Xf(0, 0, 1, 164, null) +            // 9 금액 1,234원
        Xf(0, 0, 1, 165, null) +            // 10 비율 12.5%
        Xf(0, 0, 1, 166, null) +            // 11 건
        Xf(0, 0, 1, 167, null) +            // 12 팩
        Xf(2, 4, 1, 0, "center") +          // 13 합계 글자
        Xf(2, 4, 1, 3, null) +              // 14 합계 숫자
        Xf(2, 4, 1, 164, null) +            // 15 합계 금액
        Xf(2, 4, 1, 165, null) +            // 16 합계 비율
        Xf(2, 4, 1, 166, null) +            // 17 합계 건
        Xf(2, 4, 1, 167, null) +            // 18 합계 팩
        Xf(4, 0, 1, 0, "left") +            // 19 취소 글자
        Xf(4, 0, 1, 0, "center") +          // 20 취소 가운데 글자
        Xf(4, 0, 1, 3, null) +              // 21 취소 숫자
        Xf(4, 0, 1, 164, null) +            // 22 취소 금액
        Xf(4, 0, 0, 0, "left") +            // 23 안내 문구 (회색, 테두리 없음)
        Xf(0, 0, 1, 168, null) +            // 24 증감 숫자 +3 / -2
        Xf(0, 0, 1, 169, null) +            // 25 증감 금액 +1,000원
        Xf(0, 0, 1, 170, null) +            // 26 증감 비율 +1.5%p
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"표준\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string Xf(int font, int fill, int border, int numFmt, string? align) =>
        $"<xf numFmtId=\"{numFmt}\" fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" xfId=\"0\"" +
        (numFmt != 0 ? " applyNumberFormat=\"1\"" : "") +
        (font != 0 ? " applyFont=\"1\"" : "") +
        (fill != 0 ? " applyFill=\"1\"" : "") +
        (border != 0 ? " applyBorder=\"1\"" : "") +
        $" applyAlignment=\"1\"><alignment vertical=\"center\"{(align == null ? "" : $" horizontal=\"{align}\"")}" +
        (align == "left" && border != 0 ? " indent=\"1\"" : "") + "/></xf>";

    // ───────────────────────── .xlsx 파일 쓰기 ─────────────────────────
    private const string MainNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string XmlHead = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";

    private static void WriteWorkbook(string path, IReadOnlyList<Sheet> sheets)
    {
        var contentTypes = new StringBuilder(XmlHead)
            .Append("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">")
            .Append("<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>")
            .Append("<Default Extension=\"xml\" ContentType=\"application/xml\"/>")
            .Append("<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>")
            .Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
        var workbook = new StringBuilder(XmlHead)
            .Append($"<workbook xmlns=\"{MainNs}\" xmlns:r=\"{RelNs}\"><bookViews><workbookView/></bookViews><sheets>");
        var workbookRels = new StringBuilder(XmlHead).Append($"<Relationships xmlns=\"{PkgRelNs}\">");
        var definedNames = new StringBuilder();

        for (var i = 0; i < sheets.Count; i++)
        {
            var n = i + 1;
            contentTypes.Append($"<Override PartName=\"/xl/worksheets/sheet{n}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
            workbook.Append($"<sheet name=\"{Esc(sheets[i].Name)}\" sheetId=\"{n}\" r:id=\"rId{n}\"/>");
            workbookRels.Append($"<Relationship Id=\"rId{n}\" Type=\"{RelNs}/worksheet\" Target=\"worksheets/sheet{n}.xml\"/>");
            if (sheets[i].AutoFilter is { } af)
            {
                var abs = string.Join(":", af.Split(':').Select(AbsRef));
                definedNames.Append($"<definedName name=\"_xlnm._FilterDatabase\" localSheetId=\"{i}\" hidden=\"1\">{Esc($"'{sheets[i].Name}'!{abs}")}</definedName>");
            }
        }
        contentTypes.Append("</Types>");
        workbook.Append("</sheets>");
        if (definedNames.Length > 0) workbook.Append("<definedNames>").Append(definedNames).Append("</definedNames>");
        workbook.Append("</workbook>");
        workbookRels.Append($"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"{RelNs}/styles\" Target=\"styles.xml\"/></Relationships>");

        var rootRels = XmlHead +
            $"<Relationships xmlns=\"{PkgRelNs}\"><Relationship Id=\"rId1\" Type=\"{RelNs}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>";

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        void Add(string name, string xml)
        {
            using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
            w.Write(xml);
        }
        Add("[Content_Types].xml", contentTypes.ToString());
        Add("_rels/.rels", rootRels);
        Add("xl/workbook.xml", workbook.ToString());
        Add("xl/_rels/workbook.xml.rels", workbookRels.ToString());
        Add("xl/styles.xml", StylesXml);
        for (var i = 0; i < sheets.Count; i++) Add($"xl/worksheets/sheet{i + 1}.xml", sheets[i].ToXml());
    }

    private static string Esc(string s) => SecurityElement.Escape(s) ?? "";

    /// <summary>A4 → $A$4</summary>
    private static string AbsRef(string cell)
    {
        var letters = new string(cell.TakeWhile(char.IsLetter).ToArray());
        return $"${letters}${cell[letters.Length..]}";
    }

    /// <summary>시트 하나. 셀을 채운 뒤 ToXml()로 시트 XML을 만든다. 행·열 번호는 1부터.</summary>
    private sealed class Sheet
    {
        private readonly SortedDictionary<int, SortedDictionary<int, string>> _rows = new();
        private readonly Dictionary<int, double> _heights = new();
        private readonly List<string> _merges = new();
        private readonly double[] _widths;

        public Sheet(string name, params double[] widths)
        {
            Name = name;
            _widths = widths;
        }

        public string Name { get; }
        public bool Selected { get; init; }
        public bool Landscape { get; init; }
        public int FreezeRows { get; init; }
        public string? AutoFilter { get; set; }

        public void Text(int row, int col, string text, int style) =>
            Put(row, col, $"<c r=\"{Ref(row, col)}\" s=\"{style}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{Esc(text)}</t></is></c>");

        public void Number(int row, int col, double value, int style) =>
            Put(row, col, $"<c r=\"{Ref(row, col)}\" s=\"{style}\"><v>{value.ToString(CultureInfo.InvariantCulture)}</v></c>");

        public void Blank(int row, int col, int style) =>
            Put(row, col, $"<c r=\"{Ref(row, col)}\" s=\"{style}\"/>");

        public void Merge(int r1, int c1, int r2, int c2) => _merges.Add($"{Ref(r1, c1)}:{Ref(r2, c2)}");

        public void Height(int row, double height) => _heights[row] = height;

        /// <summary>남색 띠 구역 제목. cols칸을 합친다.</summary>
        public void Section(int row, string title, int cols)
        {
            Text(row, 1, title, St.Section);
            for (var c = 2; c <= cols; c++) Blank(row, c, St.Section);
            if (cols > 1) Merge(row, 1, row, cols);
            Height(row, 22);
        }

        /// <summary>표 머리글 한 줄 (1열부터)</summary>
        public void Header(int row, params string[] labels)
        {
            for (var i = 0; i < labels.Length; i++) Text(row, i + 1, labels[i], St.Header);
            Height(row, 22);
        }

        private void Put(int row, int col, string xml)
        {
            if (!_rows.TryGetValue(row, out var cells)) _rows[row] = cells = new SortedDictionary<int, string>();
            cells[col] = xml;
        }

        public static string Ref(int row, int col)
        {
            var letters = "";
            for (var c = col; c > 0; c = (c - 1) / 26) letters = (char)('A' + (c - 1) % 26) + letters;
            return letters + row.ToString(CultureInfo.InvariantCulture);
        }

        public string ToXml()
        {
            var x = new StringBuilder(XmlHead);
            x.Append($"<worksheet xmlns=\"{MainNs}\" xmlns:r=\"{RelNs}\">");
            x.Append("<sheetPr><pageSetUpPr fitToPage=\"1\"/></sheetPr>");
            x.Append($"<sheetViews><sheetView showGridLines=\"0\"{(Selected ? " tabSelected=\"1\"" : "")} workbookViewId=\"0\">");
            if (FreezeRows > 0)
                x.Append($"<pane ySplit=\"{FreezeRows}\" topLeftCell=\"A{FreezeRows + 1}\" activePane=\"bottomLeft\" state=\"frozen\"/>");
            x.Append("</sheetView></sheetViews>");
            x.Append("<sheetFormatPr defaultRowHeight=\"19\" customHeight=\"1\"/>");
            x.Append("<cols>");
            for (var i = 0; i < _widths.Length; i++)
                x.Append($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{_widths[i].ToString(CultureInfo.InvariantCulture)}\" customWidth=\"1\"/>");
            x.Append("</cols><sheetData>");
            foreach (var (row, cells) in _rows)
            {
                x.Append($"<row r=\"{row}\"");
                if (_heights.TryGetValue(row, out var h))
                    x.Append($" ht=\"{h.ToString(CultureInfo.InvariantCulture)}\" customHeight=\"1\"");
                x.Append('>');
                foreach (var cell in cells.Values) x.Append(cell);
                x.Append("</row>");
            }
            x.Append("</sheetData>");
            if (AutoFilter != null) x.Append($"<autoFilter ref=\"{AutoFilter}\"/>");
            if (_merges.Count > 0)
            {
                x.Append($"<mergeCells count=\"{_merges.Count}\">");
                foreach (var m in _merges) x.Append($"<mergeCell ref=\"{m}\"/>");
                x.Append("</mergeCells>");
            }
            x.Append("<pageMargins left=\"0.5\" right=\"0.5\" top=\"0.6\" bottom=\"0.6\" header=\"0.3\" footer=\"0.3\"/>");
            x.Append($"<pageSetup paperSize=\"9\" orientation=\"{(Landscape ? "landscape" : "portrait")}\" fitToWidth=\"1\" fitToHeight=\"0\"/>");
            x.Append("</worksheet>");
            return x.ToString();
        }
    }
}
