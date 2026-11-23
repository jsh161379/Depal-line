using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using DepalAdmin.Core.Auth;

namespace DepalAdmin.Feature.Auth.Audit;

/// <summary>
/// 작업 기록을 서식 있는 엑셀 파일(.xlsx)로 저장한다.
/// 외부 패키지 없이 .xlsx(XML 파일 묶음)를 직접 만든다 → Directory.Packages.props(공통)를 고치지 않아도 된다.
///
/// 시트 2개
///   - 작업 기록: 한 줄에 기록 하나. 날짜·시각은 진짜 날짜 값이라 정렬·필터가 된다. 요일은 글자로 따로 (엑셀 언어와 상관없이 "월")
///                로그인 실패·비상정지·계정 삭제 같은 주의할 기록은 종류 칸을 주황색으로 표시
///   - 요약: 종류별 건수(비율) · 사람별 건수(마지막 작업) · 날짜별 건수
/// </summary>
internal static class AuditExcelReport
{
    /// <summary>종류 이름에 이 글자가 들어 있으면 주의할 기록으로 표시</summary>
    private static readonly string[] WarnWords = { "실패", "비상정지", "삭제", "사용 중지", "권한", "초기화" };

    public static bool IsWarn(string category) => WarnWords.Any(w => category.Contains(w, StringComparison.Ordinal));

    public static void Save(string path, IReadOnlyList<AuditEntry> rows, DateTime from, DateTime to,
                            string category, string userText, string madeBy)
    {
        var days = (to.Date - from.Date).Days + 1;
        var period = $"{from:yyyy-MM-dd} ~ {to:yyyy-MM-dd} ({days}일)";
        var condition = $"종류: {category} · 사람: {(string.IsNullOrWhiteSpace(userText) ? "전체" : userText.Trim())}";
        var written = $"{madeBy} · {DateTime.Now:yyyy-MM-dd HH:mm}";
        var warnCount = rows.Count(e => IsWarn(e.Category));

        // ═══ 시트 1: 작업 기록 ═══
        const int last = 8;
        var log = new Sheet("작업 기록", 7, 13, 6, 11, 13, 12, 16, 58) { Selected = true, FreezeRows = 6, Landscape = true };
        log.Text(1, 1, "관리자 프로그램 작업 기록", St.Title);
        log.Merge(1, 1, 1, last);
        log.Height(1, 32);
        log.Text(2, 1, "기간", St.InfoLabel);
        log.Merge(2, 1, 2, 2);
        log.Text(2, 3, period, St.InfoValue);
        log.Merge(2, 3, 2, 6);
        log.Text(2, 7, "작성", St.InfoLabel);
        log.Text(2, 8, written, St.InfoValue);
        log.Text(3, 1, "조건", St.InfoLabel);
        log.Merge(3, 1, 3, 2);
        log.Text(3, 3, condition, St.InfoValue);
        log.Merge(3, 3, 3, 6);
        log.Text(3, 7, "건수", St.InfoLabel);
        log.Text(3, 8, $"{rows.Count}건 (주의 {warnCount}건)", St.InfoValue);

        log.Header(6, "번호", "날짜", "요일", "시각", "아이디", "이름", "종류", "내용");
        var r = 7;
        var no = 1;
        foreach (var e in rows)
        {
            var warn = IsWarn(e.Category);
            log.Number(r, 1, no++, St.Center);
            log.Number(r, 2, Serial(e.Time.Date), St.Date);
            log.Text(r, 3, Weekday(e.Time), St.Center);
            log.Number(r, 4, Serial(e.Time) - Serial(e.Time.Date), St.Time);
            log.Text(r, 5, e.UserId, St.Center);
            log.Text(r, 6, e.UserName, St.Center);
            log.Text(r, 7, e.Category, warn ? St.Warn : St.Center);
            log.Text(r, 8, e.Message, St.Wrap);
            r++;
        }
        if (rows.Count == 0)
        {
            log.Text(r, 1, "이 기간에 작업 기록이 없습니다.", St.Text);
            for (var c = 2; c <= last; c++) log.Blank(r, c, St.Text);
            log.Merge(r, 1, r, last);
            r++;
        }
        else
        {
            log.AutoFilter = $"A6:{Sheet.Ref(r - 1, last)}";
        }
        r++;
        log.Text(r, 1, "주황색 종류 = 주의할 기록 (로그인 실패, 비상정지, 권한 변경, 비밀번호 초기화, 계정 사용 중지·삭제). 머리글의 ▼로 거를 수 있습니다.", St.Note);

        // ═══ 시트 2: 요약 ═══
        const int R = 6;   // 오른쪽 묶음 시작 열 (F)
        var sum = new Sheet("요약", 20, 7, 10, 10, 3, 14, 12, 10, 19);
        sum.Text(1, 1, "작업 기록 요약", St.Title);
        sum.Merge(1, 1, 1, 9);
        sum.Height(1, 32);
        sum.Text(2, 1, "기간", St.InfoLabel);
        sum.Text(2, 2, period, St.InfoValue);
        sum.Merge(2, 2, 2, 9);
        sum.Text(3, 1, "조건", St.InfoLabel);
        sum.Text(3, 2, condition, St.InfoValue);
        sum.Merge(3, 2, 3, 9);

        // 종류별 (왼쪽)
        var q = 5;
        sum.Section(q++, "종류별 건수", 4);
        sum.Header(q, "종류", "", "건수", "비율");
        sum.Merge(q, 1, q, 2);
        q++;
        var total = Math.Max(1, rows.Count);
        foreach (var g in rows.GroupBy(e => e.Category).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            var warn = IsWarn(g.Key);
            sum.Text(q, 1, g.Key, warn ? St.WarnLeft : St.Text);
            sum.Blank(q, 2, warn ? St.WarnLeft : St.Text);
            sum.Merge(q, 1, q, 2);
            sum.Number(q, 3, g.Count(), St.Count);
            sum.Number(q, 4, Math.Round(100.0 * g.Count() / total, 1), St.Pct);
            q++;
        }
        sum.Text(q, 1, "합계", St.TotalText);
        sum.Blank(q, 2, St.TotalText);
        sum.Merge(q, 1, q, 2);
        sum.Number(q, 3, rows.Count, St.TotalCount);
        sum.Number(q, 4, rows.Count == 0 ? 0 : 100, St.TotalPct);
        q += 2;

        // 날짜별 (왼쪽, 기간의 모든 날 — 기록 없는 날은 0)
        sum.Section(q++, "날짜별 건수", 4);
        sum.Header(q++, "날짜", "요일", "건수", "주의");
        var byDay = rows.GroupBy(e => e.Time.Date).ToDictionary(g => g.Key, g => g.ToList());
        for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
        {
            var list = byDay.TryGetValue(d, out var l) ? l : new List<AuditEntry>();
            sum.Number(q, 1, Serial(d), St.Date);
            sum.Text(q, 2, Weekday(d), St.Center);
            sum.Number(q, 3, list.Count, St.Count);
            sum.Number(q, 4, list.Count(e => IsWarn(e.Category)), St.Count);
            q++;
        }
        sum.Text(q, 1, "합계", St.TotalText);
        sum.Blank(q, 2, St.TotalText);
        sum.Merge(q, 1, q, 2);
        sum.Number(q, 3, rows.Count, St.TotalCount);
        sum.Number(q, 4, warnCount, St.TotalCount);

        // 사람별 (오른쪽)
        var p = 5;
        sum.Section(p++, "사람별 건수", 4, R);
        sum.HeaderAt(p++, R, "아이디", "이름", "건수", "마지막 작업");
        foreach (var g in rows.GroupBy(e => e.UserId).OrderByDescending(g => g.Count()).ThenBy(g => g.Key))
        {
            var latest = g.MaxBy(e => e.Time)!;
            sum.Text(p, R, g.Key, St.Center);
            sum.Text(p, R + 1, latest.UserName, St.Center);
            sum.Number(p, R + 2, g.Count(), St.Count);
            sum.Number(p, R + 3, Serial(latest.Time), St.DateAndTime);
            p++;
        }
        sum.Text(p, R, "합계", St.TotalText);
        sum.Blank(p, R + 1, St.TotalText);
        sum.Merge(p, R, p, R + 1);
        sum.Number(p, R + 2, rows.Count, St.TotalCount);
        sum.Blank(p, R + 3, St.TotalText);

        WriteWorkbook(path, new[] { log, sum });
    }

    /// <summary>엑셀 날짜 값 (1899-12-30부터 지난 날 수). 소수 부분이 시각</summary>
    private static double Serial(DateTime t) => (t - new DateTime(1899, 12, 30)).TotalDays;

    private static string Weekday(DateTime t) => "일월화수목금토"[(int)t.DayOfWeek].ToString();

    // ───────────────────────── 셀 서식 번호 (아래 StylesXml의 cellXfs 순서와 같아야 함) ─────────────────────────
    private static class St
    {
        public const int Title = 1, InfoLabel = 2, InfoValue = 3, Section = 4, Header = 5;
        public const int Text = 6, Center = 7, Count = 8, Pct = 9, Date = 10, Time = 11, DateAndTime = 12, Wrap = 13;
        public const int Warn = 14, WarnLeft = 15, TotalText = 16, TotalCount = 17, TotalPct = 18, Note = 19;
    }

    private static readonly string StylesXml =
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" +
        "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">" +
        "<numFmts count=\"5\">" +
        "<numFmt numFmtId=\"164\" formatCode=\"#,##0&quot;건&quot;\"/>" +
        "<numFmt numFmtId=\"165\" formatCode=\"0.0&quot;%&quot;\"/>" +
        "<numFmt numFmtId=\"166\" formatCode=\"yyyy\\-mm\\-dd\"/>" +
        "<numFmt numFmtId=\"167\" formatCode=\"hh:mm:ss\"/>" +
        "<numFmt numFmtId=\"168\" formatCode=\"yyyy\\-mm\\-dd\\ hh:mm\"/>" +
        "</numFmts>" +
        "<fonts count=\"6\">" +
        "<font><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"16\"/><color rgb=\"FF1F4E79\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"11\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><sz val=\"10\"/><color rgb=\"FF808080\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "<font><b/><sz val=\"11\"/><color rgb=\"FF9C3D00\"/><name val=\"맑은 고딕\"/><family val=\"3\"/><charset val=\"129\"/></font>" +
        "</fonts>" +
        "<fills count=\"6\">" +
        "<fill><patternFill patternType=\"none\"/></fill>" +
        "<fill><patternFill patternType=\"gray125\"/></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF1F4E79\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFDDEBF7\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFF2F2F2\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFFCE4D6\"/><bgColor indexed=\"64\"/></patternFill></fill>" +
        "</fills>" +
        "<borders count=\"2\">" +
        "<border><left/><right/><top/><bottom/><diagonal/></border>" +
        "<border><left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left><right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>" +
        "<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top><bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom><diagonal/></border>" +
        "</borders>" +
        "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>" +
        "<cellXfs count=\"20\">" +
        Xf(0, 0, 0, 0, null) +              // 0 기본
        Xf(1, 0, 0, 0, "left") +            // 1 제목
        Xf(2, 4, 1, 0, "center") +          // 2 기간 등 이름 (회색 칸)
        Xf(0, 0, 1, 0, "left") +            // 3 기간 등 값
        Xf(3, 2, 0, 0, "left") +            // 4 구역 제목 (남색 띠)
        Xf(2, 3, 1, 0, "center") +          // 5 표 머리글
        Xf(0, 0, 1, 0, "left") +            // 6 글자
        Xf(0, 0, 1, 0, "center") +          // 7 가운데 글자
        Xf(0, 0, 1, 164, null) +            // 8 건
        Xf(0, 0, 1, 165, null) +            // 9 비율
        Xf(0, 0, 1, 166, "center") +        // 10 날짜 2026-11-23
        Xf(0, 0, 1, 167, "center") +        // 11 시각 10:10:05
        Xf(0, 0, 1, 168, "center") +        // 12 날짜+시각
        Xf(0, 0, 1, 0, "left", wrap: true) + // 13 내용 (줄바꿈)
        Xf(5, 5, 1, 0, "center") +          // 14 주의 종류 (주황)
        Xf(5, 5, 1, 0, "left") +            // 15 주의 종류 (왼쪽)
        Xf(2, 4, 1, 0, "center") +          // 16 합계 글자
        Xf(2, 4, 1, 164, null) +            // 17 합계 건
        Xf(2, 4, 1, 165, null) +            // 18 합계 비율
        Xf(4, 0, 0, 0, "left") +            // 19 안내 문구 (회색, 테두리 없음)
        "</cellXfs>" +
        "<cellStyles count=\"1\"><cellStyle name=\"표준\" xfId=\"0\" builtinId=\"0\"/></cellStyles>" +
        "</styleSheet>";

    private static string Xf(int font, int fill, int border, int numFmt, string? align, bool wrap = false) =>
        $"<xf numFmtId=\"{numFmt}\" fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" xfId=\"0\"" +
        (numFmt != 0 ? " applyNumberFormat=\"1\"" : "") +
        (font != 0 ? " applyFont=\"1\"" : "") +
        (fill != 0 ? " applyFill=\"1\"" : "") +
        (border != 0 ? " applyBorder=\"1\"" : "") +
        $" applyAlignment=\"1\"><alignment vertical=\"center\"{(align == null ? "" : $" horizontal=\"{align}\"")}" +
        (align == "left" && border != 0 ? " indent=\"1\"" : "") +
        (wrap ? " wrapText=\"1\"" : "") + "/></xf>";

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

    /// <summary>A6 → $A$6</summary>
    private static string AbsRef(string cell)
    {
        var letters = new string(cell.TakeWhile(char.IsLetter).ToArray());
        return $"${letters}${cell[letters.Length..]}";
    }

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
            Put(row, col, $"<c r=\"{Ref(row, col)}\" s=\"{style}\"><v>{value.ToString("R", CultureInfo.InvariantCulture)}</v></c>");

        public void Blank(int row, int col, int style) =>
            Put(row, col, $"<c r=\"{Ref(row, col)}\" s=\"{style}\"/>");

        public void Merge(int r1, int c1, int r2, int c2) => _merges.Add($"{Ref(r1, c1)}:{Ref(r2, c2)}");

        public void Height(int row, double height) => _heights[row] = height;

        /// <summary>남색 띠 구역 제목. cols칸을 합친다.</summary>
        public void Section(int row, string title, int cols, int startCol = 1)
        {
            Text(row, startCol, title, St.Section);
            for (var c = startCol + 1; c < startCol + cols; c++) Blank(row, c, St.Section);
            if (cols > 1) Merge(row, startCol, row, startCol + cols - 1);
            Height(row, 22);
        }

        public void Header(int row, params string[] labels) => HeaderAt(row, 1, labels);

        public void HeaderAt(int row, int startCol, params string[] labels)
        {
            for (var i = 0; i < labels.Length; i++) Text(row, startCol + i, labels[i], St.Header);
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
            x.Append("<sheetFormatPr defaultRowHeight=\"19\"/>");
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
