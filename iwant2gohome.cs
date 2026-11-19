// iwant2gohome - 출근시간 자동 기록 + 퇴근 타이머 위젯 (Windows, .NET Framework 4)
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    static void KillOthers()
    {
        try
        {
            int me = System.Diagnostics.Process.GetCurrentProcess().Id;
            foreach (string name in new string[] { "WorkClock", "iwant2gohome" })
                foreach (System.Diagnostics.Process p in System.Diagnostics.Process.GetProcessesByName(name))
                {
                    try { if (p.Id != me) { p.Kill(); p.WaitForExit(3000); } } catch { }
                }
        }
        catch { }
    }

    [STAThread]
    static void Main(string[] args)
    {
        try
        {
            if (args.Length >= 2 && args[0] == "--snapshot") { Snapshot.Run(args); return; }
            if (args.Length >= 1 && args[0] == "--uninstall") { Startup.Remove(); return; }
            if (args.Length >= 1 && args[0] == "--install") { Startup.Add(); }

            KillOthers();   // 예전 이름(WorkClock)이나 이전 버전이 남아 있으면 종료 (최신 실행이 우선)
            bool createdNew;
            using (Mutex mutex = new Mutex(true, "Local\\iwant2gohomeWidget", out createdNew))
            {
                if (!createdNew) return;
                try { SetProcessDPIAware(); } catch { }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new WidgetForm());
            }
        }
        catch (Exception ex) { Log.Write(ex.ToString()); }
    }
}

static class Log
{
    public static void Write(string msg)
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            File.AppendAllText(Path.Combine(Store.Dir, "error.log"), DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine, Encoding.UTF8);
        }
        catch { }
    }
}

static class Startup
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "iwant2gohome";
    const string OldValueName = "WorkClock";
    public static void Add()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k != null) k.DeleteValue(OldValueName, false);
            if (k != null) k.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"");
        }
    }
    public static void Remove()
    {
        using (RegistryKey k = Registry.CurrentUser.OpenSubKey(RunKey, true))
        {
            if (k != null) k.DeleteValue(ValueName, false);
        }
    }
}

// 출근 기록(CSV) 읽기/쓰기. 형식: 날짜,요일,출근시간
static class Store
{
    static readonly CultureInfo CI = CultureInfo.InvariantCulture;
    static readonly string[] WD = { "일", "월", "화", "수", "목", "금", "토" };

    public static string Dir
    {
        get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "출근기록"); }
    }
    public static string Csv { get { return Path.Combine(Dir, "출근기록.csv"); } }

    // 새벽 4시 전까지는 전날 근무로 본다 (야근 대응)
    public static DateTime WorkDay(DateTime now) { return now.AddHours(-4).Date; }

    [DllImport("kernel32.dll")] static extern ulong GetTickCount64();

    public static DateTime? Read(DateTime day)
    {
        try
        {
            if (!File.Exists(Csv)) return null;
            string prefix = day.ToString("yyyy-MM-dd", CI) + ",";
            foreach (string line in File.ReadAllLines(Csv, Encoding.UTF8))
            {
                if (!line.StartsWith(prefix)) continue;
                string[] p = line.Split(',');
                DateTime t;
                if (p.Length >= 3 && DateTime.TryParseExact(p[2].Trim(), "HH:mm", CI, DateTimeStyles.None, out t))
                    return day.Date.AddHours(t.Hour).AddMinutes(t.Minute);
            }
        }
        catch (Exception ex) { Log.Write("Read: " + ex.Message); }
        return null;
    }

    public static void Write(DateTime day, DateTime t)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            List<string> lines = new List<string>();
            if (File.Exists(Csv)) lines.AddRange(File.ReadAllLines(Csv, Encoding.UTF8));
            if (lines.Count == 0 || !lines[0].StartsWith("날짜")) lines.Insert(0, "날짜,요일,출근시간");
            string prefix = day.ToString("yyyy-MM-dd", CI) + ",";
            string row = prefix + WD[(int)day.DayOfWeek] + "," + t.ToString("HH:mm", CI);
            bool done = false;
            for (int i = 0; i < lines.Count; i++)
                if (lines[i].StartsWith(prefix)) { lines[i] = row; done = true; break; }
            if (!done) lines.Add(row);
            File.WriteAllLines(Csv, lines.ToArray(), new UTF8Encoding(true));
        }
        catch (Exception ex) { Log.Write("Write: " + ex.Message); }
    }

    // 오늘 기록이 있으면 그 시각, 없으면 부팅 시각(오늘 부팅한 경우) 또는 지금을 기록
    public static DateTime ResolveClockIn(DateTime now)
    {
        DateTime day = WorkDay(now);
        DateTime? r = Read(day);
        if (r.HasValue) return r.Value;
        DateTime t = now;
        try
        {
            DateTime boot = now.AddMilliseconds(-(double)GetTickCount64());
            if (day == now.Date && boot.Date == now.Date && boot <= now) t = boot;
        }
        catch { }
        t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
        Write(day, t);
        return t;
    }
}

// 화면에 보여줄 값 계산
class Model
{
    public string InText, OffText, Label, Big, PctText, Phase, Act, Char;
    public bool Over, Warn;
    public double Progress;
    public int OffsetX;

    static string Hms(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = -t;
        return ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
    }
    static double Ratio(TimeSpan a, TimeSpan b)
    {
        if (b.TotalSeconds <= 0) return 1;
        double r = a.TotalSeconds / b.TotalSeconds;
        return r < 0 ? 0 : (r > 1 ? 1 : r);
    }

    // 단계: pre(점심까지) → meal(11:30~12:30 식사 중) → post(퇴근까지) → 야근 중
    public static Model Build(DateTime now, DateTime clockIn, string viewPref, string act, int ox)
    {
        Model m = new Model();
        DateTime off = clockIn.AddHours(9);
        DateTime lun = clockIn.Date.AddHours(11).AddMinutes(30);
        DateTime lunEnd = lun.AddHours(1);
        m.InText = "출근 " + clockIn.ToString("HH:mm");
        m.OffText = "퇴근 " + off.ToString("HH:mm");
        m.Act = act; m.OffsetX = ox; m.Char = Settings.Char;
        m.Progress = Ratio(now - clockIn, off - clockIn);
        m.PctText = (int)Math.Round(m.Progress * 100) + "%";
        TimeSpan left;
        if (now < lun)
        {
            m.Phase = "pre"; left = lun - now; m.Label = "점심까지"; m.Big = Hms(left);
        }
        else if (now < lunEnd)
        {
            m.Phase = "meal"; left = lunEnd - now; m.Label = "식사 중"; m.Big = Hms(left);
        }
        else if (now < off)
        {
            m.Phase = "post"; left = off - now; m.Label = "퇴근까지"; m.Big = Hms(left);
        }
        else
        {
            m.Phase = "over"; left = off - now; m.Over = true; m.Label = "야근 중"; m.Big = "+" + Hms(left);
        }
        m.Warn = !m.Over && left.TotalMinutes < 10;
        return m;
    }
}

// 그리기 (실제 창과 스냅샷이 같은 코드를 사용)
static class Ui
{
    public const int W = 200, H = 146, S = 2;
    static readonly Dictionary<string, string[]> QUIP = new Dictionary<string, string[]>();
    static readonly Dictionary<string, string> STATE = new Dictionary<string, string>();
    static Ui()
    {
        STATE["work"] = "코딩 중"; STATE["coffee"] = "커피 타임"; STATE["walk"] = "산책 중"; STATE["sleep"] = "졸음 모드"; STATE["eat"] = "맛점 타임"; STATE["cheer"] = "퇴근이다!";
        QUIP["work"] = new string[] { "버그 잡는 중…", "커밋 한 번만 더", "어? 왜 되지?", "빌드 성공!", "PR 올렸다", "콘솔에 에러가…", "이건 내일 리팩토링", "타입 에러 또…", "스택오버플로 검색" };
        QUIP["coffee"] = new string[] { "카페인 충전!", "수혈 중…", "한 모금만 더", "아아 없이 못 산다", "라떼 한 잔의 여유" };
        QUIP["walk"] = new string[] { "다리 좀 풀자", "머리 식히는 중", "스트레칭~", "바람 쐬러 가자", "화장실 가는 척" };
        QUIP["sleep"] = new string[] { "3분만…", "눈이 감겨요", "모니터가 베개로", "zzZ…", "커피가 안 듣네" };
        QUIP["eat"] = new string[] { "오늘 메뉴 뭐지?", "냠냠", "밥이 최고", "역시 점심시간", "한 숟갈만 더" };
        QUIP["cheer"] = new string[] { "칼퇴 성공!", "집에 가자~", "오늘도 수고!" };
        STATE["f:call"] = "통화 중"; STATE["f:smash"] = "벽 샌드백 중"; STATE["f:coffee"] = "커피 타임"; STATE["f:cat"] = "냥이 힐링"; STATE["f:sleep"] = "졸음 폭발"; STATE["f:leave"] = "퇴근 각"; STATE["f:eat"] = "맛점 타임"; STATE["f:cheer"] = "퇴근이다!";
        QUIP["f:call"] = new string[] { "네~ 확인할게요^^|(점심 뭐 먹지…)", "넵 바로 할게요!|(그 얘기 또 해…)", "좋은 의견이세요~|(왜 지금 말해…)", "회의 가능하세요?|(제발 안 된다고 해라)" };
        QUIP["f:smash"] = new string[] { "자료 보고 올게요^^|(퍽! 아 시원하다)", "이해했습니다^^|(퍽퍽! 열받네)", "금방 드릴게요^^|(주먹이 부들부들…)", "괜찮습니다^^|(참자 참자 참자…)" };
        QUIP["f:coffee"] = new string[] { "커피 한잔요?|(카페인 수혈 중…)", "오늘도 화이팅~|(퇴근까지 몇 시간?)", "향 좋네요~|(이거 마시고 버티자)" };
        QUIP["f:cat"] = new string[] { "어머 귀여워라|(고양이가 최고야)", "냥이 왔어요?|(이게 진짜 힐링)", "잠깐 쉴게요|(냥이 보고 싶다)" };
        QUIP["f:sleep"] = new string[] { "네 듣고 있어요^^|(아 잠온다…)", "집중해서 보는 중|(3분만 눈 감자…)", "자료 정리 중이에요|(누가 좀 깨워줘)" };
        QUIP["f:leave"] = new string[] { "먼저 들어갈게요^^|(퇴근이다!!)", "수고하셨습니다|(집 가서 뭐 먹지)", "내일 뵙겠습니다~|(내일은 제발 연차)" };
        QUIP["f:eat"] = new string[] { "맛있게 드세요^^|(오늘 메뉴 대박)", "식사 맛있게요~|(디저트는 뭐 먹지)", "천천히 드세요~|(밥이 제일 중요해)" };
        QUIP["f:cheer"] = new string[] { "수고하셨습니다!|(퇴근 완료!!)", "내일 뵙겠습니다~|(집에 가자!!)" };
        QUIP["night"] = new string[] { "집 가고 싶다", "달이 예쁘네", "야근수당 주세요", "내일 연차 쓸까", "배포만 하고 가자" };
    }
    static string Pick(string[] a, int fr, int salt) { return a[((fr / 32) + salt) % a.Length]; }

    static Dictionary<int, SolidBrush> brushes = new Dictionary<int, SolidBrush>();
    static SolidBrush Br(Color c)
    {
        int k = c.ToArgb(); SolidBrush b;
        if (!brushes.TryGetValue(k, out b)) { b = new SolidBrush(c); brushes[k] = b; }
        return b;
    }
    static Color Hx(string s) { return ColorTranslator.FromHtml(s); }

    // 글꼴은 픽셀 단위로 만든다 (화면 배율은 ScaleTransform 한 곳에서만 적용 → 이중 확대 방지)
    static Font F(string name, float pt, FontStyle style) { return new Font(name, pt * 96f / 72f, style, GraphicsUnit.Pixel); }

    public static GraphicsPath RoundPath(int x, int y, int w, int h, int r)
    {
        GraphicsPath p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    public static void Draw(Graphics g, Model m, int fr)
    {
        bool night = m.Over, meal = m.Phase == "meal";
        string wk = Settings.WeatherOn ? Weather.Kind : "clear";
        bool gray = wk == "rain" || wk == "storm";
        Color skyTop, skyBot;
        SkyColors(wk, meal, out skyTop, out skyBot);
        Color bg = night ? Hx("#151A33") : skyBot;
        Color border = night ? Hx("#3C446E") : (meal ? Hx("#E6BE8E") : Hx("#9FB4CC"));
        Color txt = night ? Hx("#E8EAF6") : Hx("#1B2A41");
        Color sub = night ? Hx("#9AA3C7") : (meal ? Hx("#6B4A2A") : (gray ? Hx("#2F3E55") : Hx("#40597A")));
        Color accent = meal ? Hx("#E0752B") : Hx("#2F5BD8");
        Color track = night ? Hx("#2A3158") : (meal ? Hx("#EBCBA3") : Hx("#A9C8E8"));
        Color bigC = night ? Hx("#FFD87A") : (m.Warn ? Hx("#C2410C") : txt);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        using (GraphicsPath gp = RoundPath(0, 0, W - 1, H - 1, 10))
        {
            if (night) g.FillPath(Br(bg), gp);
            else using (LinearGradientBrush lg = new LinearGradientBrush(new Rectangle(0, 0, W, H), skyTop, skyBot, 90f)) g.FillPath(lg, gp);
            using (Pen pen = new Pen(border, 1)) g.DrawPath(pen, gp);
        }

        g.SmoothingMode = SmoothingMode.None;
        if (night) DrawNight(g, bg, fr);
        else DrawSky(g, wk, fr);

        using (Font f1 = F("Malgun Gothic", 8f, FontStyle.Regular))
        using (Font f2 = F("Malgun Gothic", 9f, FontStyle.Bold))
        using (Font fBig = F("Consolas", 19f, FontStyle.Bold))
        using (Font ft = F("Malgun Gothic", 7.5f, FontStyle.Regular))
        using (Font fi = F("Malgun Gothic", 7f, FontStyle.Italic))
        using (Font ftb = F("Malgun Gothic", 7.5f, FontStyle.Bold))
        using (Font fp = F("Malgun Gothic", 8.5f, FontStyle.Bold))
        using (StringFormat right = new StringFormat { Alignment = StringAlignment.Far })
        {
            g.DrawString(m.InText, f1, Br(sub), 8, 4);
            g.DrawString(m.OffText, f1, Br(sub), new RectangleF(8, 4, W - 16, 16), right);
            g.DrawString(m.Label, f2, Br(night ? Hx("#FFD87A") : txt), 8, 19);
            g.DrawString(m.Big, fBig, Br(bigC), 6, 34);

            // 진행 바(맨 아래, 좌우 끝까지) + 캐릭터
            int barX = 0, barW = W, barH = 5, barY = H - barH;
            g.FillRectangle(Br(track), barX, barY, barW, barH);
            g.FillRectangle(Br(night ? Hx("#FFD87A") : accent), barX, barY, (int)Math.Round(barW * m.Progress), barH);
            int spW = Sprite.PxW, spH = Sprite.PxH;
            int sx = barX + (int)Math.Round((barW - spW) * m.Progress) + (m.Act == "walk" && m.Char != "f1" ? m.OffsetX : 0);
            if (sx < barX) sx = barX;
            if (sx > barX + barW - spW) sx = barX + barW - spW;
            int spY = barY - 1 - spH;
            new Sprite(g, sx, spY, fr, m.Over).Draw(m.Char, m.Act);

            // 진행률(작게) - 진행 바 바로 위, 캐릭터 반대쪽 끝
            bool spRight = sx + spW / 2 > W / 2;
            string pct = m.PctText;
            if (spRight) g.DrawString(pct, fp, Br(sub), 5, barY - 15);
            else g.DrawString(pct, fp, Br(sub), new RectangleF(0, barY - 15, W - 5, 15), right);

            // 말풍선: 캐릭터 옆 빈 공간에 상태 + 한마디
            int leftRoom = sx - 6, rightRoom = W - (sx + spW) - 6;
            bool useRight = rightRoom >= leftRoom;
            int room = useRight ? rightRoom : leftRoom;
            bool fem = m.Char == "f1";
            string key = (fem ? "f:" : "") + m.Act;
            string[] qs = (!fem && m.Over && (m.Act == "work" || m.Act == "walk")) ? QUIP["night"] : QUIP[key];
            string quip = Pick(qs, fr, m.Act.Length);
            string inner = null;
            if (fem) { int bar = quip.IndexOf('|'); if (bar > 0) { inner = quip.Substring(bar + 1); quip = quip.Substring(0, bar); } }
            string state = (!fem && m.Over && m.Act == "work") ? "야근 코딩" : STATE[key];
            int bw = Math.Min(room - 6, 128);
            if (bw >= 44)
            {
                int pad = 5;
                bw = Math.Min(room - 6, 132);
                SizeF qsz = g.MeasureString(quip, ft, bw - pad * 2);
                SizeF isz = inner != null ? g.MeasureString(inner, fi, bw - pad * 2) : SizeF.Empty;
                int bh = (int)Math.Ceiling(qsz.Height) + (int)Math.Ceiling(isz.Height) + pad * 2;
                int bBottom = barY - 17, bTop = bBottom - bh;
                if (bTop < 64) { bTop = 64; bh = bBottom - bTop; }          // 큰 시계(숫자) 아래 영역만 사용
                int bx = useRight ? sx + spW + 5 : sx - 5 - bw;
                Rectangle br = new Rectangle(bx, bTop, bw, bh);
                int ty0 = bBottom - 9;                         // 꼬리 위치(캐릭터 얼굴 높이쯤)
                Color bf = night ? Hx("#262E5A") : Color.White;
                Color bb = night ? Hx("#5B6396") : (meal ? Hx("#E0A96B") : Hx("#8FB3DD"));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (GraphicsPath bp = RoundPath(br.X, br.Y, br.Width, br.Height, 6))
                {
                    g.FillPath(Br(bf), bp);
                    using (Pen pen = new Pen(bb, 1)) g.DrawPath(pen, bp);
                }
                Point[] tail = useRight
                    ? new Point[] { new Point(br.X + 1, ty0 - 3), new Point(br.X - 5, ty0 + 1), new Point(br.X + 1, ty0 + 4) }
                    : new Point[] { new Point(br.Right - 1, ty0 - 3), new Point(br.Right + 5, ty0 + 1), new Point(br.Right - 1, ty0 + 4) };
                g.FillPolygon(Br(bf), tail);
                using (Pen pen = new Pen(bb, 1))
                {
                    g.DrawLine(pen, tail[0], tail[1]); g.DrawLine(pen, tail[1], tail[2]);
                }
                g.SmoothingMode = SmoothingMode.None;
                Color tc = night ? Hx("#E8EAF6") : Hx("#1B2A41");
                                g.DrawString(quip, ft, Br(tc), new RectangleF(br.X + pad, br.Y + pad - 1, bw - pad * 2, qsz.Height + 1));
                if (inner != null)
                    g.DrawString(inner, fi, Br(night ? Hx("#F2A6C0") : Hx("#C2415C")), new RectangleF(br.X + pad, br.Y + pad - 1 + qsz.Height, bw - pad * 2, isz.Height + 1));
            }
        }
    }

    static void SkyColors(string wk, bool meal, out Color top, out Color bot)
    {
        if (meal) { top = Hx("#FBD9AE"); bot = Hx("#FFF0DC"); return; }
        switch (wk)
        {
            case "partly": top = Hx("#9CC6EC"); bot = Hx("#E1EEF8"); break;
            case "cloudy": top = Hx("#B3BFCC"); bot = Hx("#DEE4EA"); break;
            case "rain": case "storm": top = Hx("#8896AA"); bot = Hx("#C2CBD6"); break;
            case "snow": top = Hx("#BFCBDA"); bot = Hx("#EEF2F8"); break;
            default: top = Hx("#86C1F5"); bot = Hx("#DDEEFF"); break;
        }
    }

    static readonly string[] CLOUD = { "...XXXX.....", "..XXXXXXX...", ".XXXXXXXXXX.", "XXXXXXXXXXXX", "ssssssssssss" };
    static void Cloud(Graphics g, int x, int y, int p, Color lite, Color shade)
    {
        for (int r = 0; r < CLOUD.Length; r++)
            for (int c = 0; c < CLOUD[r].Length; c++)
            {
                char ch = CLOUD[r][c]; if (ch == '.') continue;
                g.FillRectangle(Br(ch == 's' ? shade : lite), x + c * p, y + r * p, p, p);
            }
    }

    static void DrawSky(Graphics g, string wk, int fr)
    {
        bool gray = wk == "cloudy" || wk == "rain" || wk == "storm";
        Color lite = gray ? Hx("#D5DBE3") : Hx("#FFFFFF"), shade = gray ? Hx("#AEB7C4") : Hx("#DCE8F5");
        if (wk == "rain" || wk == "storm") { lite = Hx("#B9C2CE"); shade = Hx("#98A3B2"); }

        // 해 / (실제 밤이면 달)
        if (wk == "clear" || wk == "partly")
        {
            if (Weather.IsDay) Sun(g, W - 30, 47, fr);
            else
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(Br(Hx("#FFE9A8")), W - 38, 38, 18, 18);
                g.FillEllipse(Br(Hx("#9CC6EC")), W - 32, 35, 17, 17);
                g.SmoothingMode = SmoothingMode.None;
            }
        }
        // 흘러가는 구름 (천천히)
        int n = wk == "clear" ? 2 : (wk == "partly" ? 3 : 4);
        int[] by = { 22, 60, 36, 74 };
        int[] bx = { 20, 110, 160, 60 };
        int[] sp = { 5, 3, 4, 6 };
        for (int i = 0; i < n; i++)
        {
            int p = i == 0 || i == 2 ? 3 : 2;
            int x = ((bx[i] + fr * 10 / sp[i] / 3) % (W + 80)) - 40;
            Cloud(g, x, by[i], p, lite, shade);
        }
        if (wk == "rain" || wk == "storm")
        {
            for (int i = 0; i < 34; i++)
            {
                int x = (i * 37 + fr * 2) % W;
                int y = (i * 23 + fr * 7 + (i % 3) * 11) % H;
                g.FillRectangle(Br(Hx("#5F82AD")), x, y, 1, 5);
            }
            if (wk == "storm" && fr % 40 < 3)
            {
                g.FillRectangle(Br(Color.FromArgb(70, 255, 255, 255)), 0, 0, W, H);
                int bx0 = 40 + (fr / 40 % 4) * 30;
                g.FillRectangle(Br(Hx("#FFD54A")), bx0 + 4, 24, 3, 10); g.FillRectangle(Br(Hx("#FFD54A")), bx0, 34, 5, 3); g.FillRectangle(Br(Hx("#FFD54A")), bx0 + 2, 37, 3, 12);
            }
        }
        else if (wk == "snow")
        {
            for (int i = 0; i < 28; i++)
            {
                int x = (i * 29 + (int)Math.Round(Math.Sin((fr + i * 3) / 5.0) * 3) + W) % W;
                int y = (i * 17 + fr * 2) % H;
                g.FillRectangle(Br(Color.White), x, y, 2, 2);
            }
        }
    }

    static void Sun(Graphics g, int cx, int cy, int fr)
    {
        // 픽셀 해: 원 + 깜빡이는 광선
        bool alt = fr % 4 < 2;
        Color ray = Hx("#FFC52E");
        int[,] d = { { 0, -15 }, { 11, -11 }, { 15, 0 }, { 11, 11 }, { 0, 15 }, { -11, 11 }, { -15, 0 }, { -11, -11 } };
        for (int i = 0; i < 8; i++)
            if ((i % 2 == 0) == alt)
                g.FillRectangle(Br(ray), cx + d[i, 0] - 1, cy + d[i, 1] - 1, 3, 3);
        g.FillEllipse(Br(Hx("#F59E0B")), cx - 10, cy - 10, 20, 20);
        g.FillEllipse(Br(Hx("#FFD23F")), cx - 9, cy - 9, 18, 18);
        g.FillRectangle(Br(Hx("#FFF1A8")), cx - 5, cy - 6, 4, 2);
    }

    static void DrawNight(Graphics g, Color bg, int fr)
    {
        // 달 (초승달) + 별
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.FillEllipse(Br(Hx("#FFE9A8")), W - 34, 22, 18, 18);
        g.FillEllipse(Br(bg), W - 28, 19, 17, 17);
        g.SmoothingMode = SmoothingMode.None;
        int[,] stars = { { 110, 12 }, { 150, 30 }, { 120, 56 }, { 176, 64 }, { 80, 16 }, { 100, 74 }, { 40, 66 } };
        for (int i = 0; i < stars.GetLength(0); i++)
        {
            bool bright = ((fr / 3) + i) % 3 != 0;
            g.FillRectangle(Br(bright ? Hx("#E8EAF6") : Hx("#5B6396")), stars[i, 0], stars[i, 1], 2, 2);
        }
    }
}

// 8비트 픽셀아트 개발자 (회색 후드티, 노트북 뒤로 얼굴만 보이는 치비 스타일)
class Sprite
{
    public const int GW = 18, GH = 15, OX = 2, OY = 1;
    public static int PxW { get { return GW * Ui.S; } }
    public static int PxH { get { return GH * Ui.S; } }

    readonly Graphics g; readonly int ox, oy, fr; readonly bool night;
    int jy;
    static readonly Dictionary<char, Color> PAL = new Dictionary<char, Color>();
    static readonly Dictionary<int, SolidBrush> cache = new Dictionary<int, SolidBrush>();
    static Color C(string s) { return ColorTranslator.FromHtml(s); }
    static Sprite()
    {
        PAL['K'] = C("#111c28"); PAL['H'] = C("#2f2a2d"); PAL['S'] = C("#f3cfa5"); PAL['B'] = C("#f2a1a1"); PAL['D'] = C("#c4a090");
        PAL['G'] = C("#9aa0a8"); PAL['g'] = C("#7c8289"); PAL['P'] = C("#3a4258");
        PAL['L'] = C("#8d93a3"); PAL['l'] = C("#6f7585"); PAL['W'] = C("#f4f4f6");
        PAL['M'] = C("#c9453f"); PAL['C'] = C("#d9534f"); PAL['c'] = C("#7a4a2a"); PAL['Z'] = C("#9aa0b0");
        PAL['Y'] = C("#ffd54a"); PAL['b'] = C("#5ec2f2"); PAL['p'] = C("#ff7eb6"); PAL['n'] = C("#7ed957"); PAL['R'] = C("#f1ece0"); PAL['k'] = C("#e5b86b");
        PAL['N'] = C("#c9d3f5");   // 밤 외곽선
        PAL['h'] = C("#4f464c"); PAL['r'] = C("#cdc4b0"); PAL['O'] = C("#f0a04b"); PAL['o'] = C("#c9772c"); PAL['U'] = C("#8a5a3a");
    }

    public Sprite(Graphics g, int ox, int oy, int fr, bool night) { this.g = g; this.ox = ox; this.oy = oy; this.fr = fr; this.night = night; }

    void Px(int x, int y, int w, int h, char ch)
    {
        if (ch == 'K' && night) ch = 'N';
        Color c; if (!PAL.TryGetValue(ch, out c)) return;
        int k = c.ToArgb(); SolidBrush b;
        if (!cache.TryGetValue(k, out b)) { b = new SolidBrush(c); cache[k] = b; }
        g.FillRectangle(b, ox + (x + OX) * Ui.S, oy + (y + OY + jy) * Ui.S, w * Ui.S, h * Ui.S);
    }
    void Map(string[] rows, int x0, int y0)
    {
        for (int r = 0; r < rows.Length; r++)
            for (int c = 0; c < rows[r].Length; c++)
                if (rows[r][c] != '.') Px(x0 + c, y0 + r, 1, 1, rows[r][c]);
    }

    static readonly string[] HEAD = {
        "..KKKKKKKK..",
        ".KHHHHHHHHK.",
        ".KHHHHHHHHK.",
        ".KHSSSSSSHK.",
        ".KSSSSSSSSK.",
        ".KSeSSSSeSK.",
        ".KBfSSSSfBK.",
        ".KSdSSSSdSK." };
    static readonly string[] CHIN = { "..KKKKKKKK.." };
    static readonly string[] LAP = {
        ".KKKKKKKKKK.",
        ".KLLLLLLLLK.",
        ".KLLLWWLLLK.",
        ".KLLLLLLLLK.",
        "KllllllllllK",
        "KKKKKKKKKKKK" };
    static readonly string[] BODY = {
        "..KGGGGGGK..",
        ".KGGGGGGGGK.",
        ".KGGGgggGGK." };
    static readonly string[] LEGS_A = { "..KPPK.KPPK.", "..KKKK.KKKK." };
    static readonly string[] LEGS_B = { "...KPPPPK...", "...KKKKKK..." };

    // dx: 머리 좌우 흔들림, mouth: 'M'이면 입 벌림
    void Head(int dx, int dy, char e, char f, char mouth)
    {
        for (int r = 0; r < HEAD.Length; r++)
            for (int c = 0; c < HEAD[r].Length; c++)
            {
                char ch = HEAD[r][c];
                if (ch == '.') continue;
                if (ch == 'e') ch = e; else if (ch == 'f') ch = f;
                else if (ch == 'd') ch = night ? 'D' : 'S';
                if (mouth != '.' && r == 7 && (c == 5 || c == 6)) ch = mouth;
                Px(c + dx, r + dy, 1, 1, ch);
            }
    }
    void Z(int x, int y, int sz)
    {
        Px(x, y, sz, 1, 'Z'); Px(x + sz - 1, y + 1, 1, sz > 2 ? 1 : 0, 'Z'); if (sz > 2) Px(x + 1, y + 2, 1, 1, 'Z');
        Px(x, y + (sz > 2 ? 3 : 2), sz, 1, 'Z');
    }

    void Work()
    {
        int t = fr % 8;
        int nod = (t == 0 || t == 4) ? 1 : 0;          // 리듬 타며 끄덕
        char e = 'K', f = 'K';
        if (fr % 14 == 0) { e = 'S'; f = 'K'; }
        Head(0, nod, e, f, '.');
        Map(LAP, 0, 8);
        bool a = fr % 2 == 0;
        // 양손이 번갈아 빠르게 타이핑
        Px(-1, a ? 10 : 12, 1, 2, 'S'); Px(12, a ? 12 : 10, 1, 2, 'S');
        Px(-2, a ? 10 : 12, 1, 1, 'G'); Px(13, a ? 12 : 10, 1, 1, 'G');
        // 키 입력 스파크
        if (fr % 3 != 2) Px(-2 + (fr * 5) % 3, 8 + (fr % 2), 1, 1, 'Y');
        if (fr % 3 != 1) Px(12 + (fr * 7) % 3, 8 + ((fr + 1) % 2), 1, 1, 'Y');
        // 화면의 로고가 깜빡
        if (fr % 6 < 3) Px(5, 10, 2, 1, 'Y');
        if (night) Px(11, 2 + fr % 3, 1, 1, 'b');       // 식은땀
    }

    void Coffee()
    {
        int t = fr % 10;
        bool sip = t >= 5;
        Head(0, 0, 'S', sip ? 'S' : 'K', sip ? 'K' : '.');
        Map(LAP, 0, 8);
        Px(-1, 11 + (fr % 2), 1, 2, 'S');
        int cy = sip ? 4 : 7;
        Px(11, cy + 2, 1, 2, 'S');
        int cx = sip ? 10 : 12;
        Px(cx, cy, 4, 5, 'K'); Px(cx + 1, cy + 1, 2, 1, 'c'); Px(cx + 1, cy + 2, 2, 2, 'C');
        if (!sip)
        {
            int u = (fr / 2) % 3;
            Px(13 + (u == 1 ? 1 : 0), 5 - u, 1, 1, 'Z'); Px(14 - (u == 1 ? 1 : 0), 3 - u + 1, 1, 1, 'Z');
        }
        else Px(7, 1, 1, 1, 'p');   // 기분 좋아서 하트
    }

    void Eat()
    {
        int t = fr % 8;
        bool up = t >= 4;
        int bob = (t == 2 || t == 6) ? 1 : 0;
        Head(0, bob, 'K', 'K', up ? 'M' : '.');
        Map(CHIN, 0, 8 + bob);
        Map(BODY, 0, 9);
        // 그릇: 밥 + 빨간 그릇
        Px(2, 10, 1, 1, 'K'); Px(9, 10, 1, 1, 'K'); Px(3, 10, 6, 1, 'R');
        Px(2, 11, 1, 1, 'K'); Px(9, 11, 1, 1, 'K'); Px(3, 11, 6, 1, 'C');
        Px(3, 12, 6, 1, 'K');
        // 식탁
        Px(-1, 12, 2, 1, 'K'); Px(9, 12, 4, 1, 'K'); Px(-1, 13, 14, 1, 'c');
        // 숟가락 든 손
        if (up) { Px(10, 8, 1, 2, 'G'); Px(10, 6, 2, 2, 'S'); Px(12, 5, 1, 2, 'Z'); Px(12, 4, 2, 1, 'Z'); }
        else { Px(10, 9, 1, 2, 'G'); Px(10, 10, 2, 1, 'S'); Px(11, 11, 2, 1, 'Z'); }
        Px(-1, 11, 1, 2, 'S');
        // 김
        int u = (fr / 2) % 3; Px(5 + u, 8 - 0, 1, 1, 'W');
        Px(0, 9 - (fr % 4) / 2, 1, 1, 'Z');
    }

    void Sleep()
    {
        int sw = (fr % 12) < 6 ? 0 : 1;
        Head(sw, 2, 'K', 'S', '.');
        Map(LAP, 0, 8);
        Px(-1, 12, 1, 2, 'S'); Px(12, 12, 1, 2, 'S');
        int z = fr % 12;
        Z(12, 3 - z / 4, 3);
        Z(15, 0 - z / 6 + 1, 3);
        if (z > 7) Px(14, 4, 1, 1, 'b');
        Px(3 + sw, 6, 1, 1, 'S');   // 눈꺼풀 라인 느낌 유지
    }

    void Walk()
    {
        int t = fr % 4; bool a = fr % 2 == 1;
        jy = a ? -1 : 0;
        Head(0, 0, 'K', 'K', '.');
        Map(CHIN, 0, 8);
        Map(BODY, 0, 9);
        // 팔 흔들기
        Px(-1, 9 + (a ? 0 : 1), 1, 3, 'G'); Px(-1, 12 + (a ? 0 : 1), 1, 1, 'S');
        Px(12, 9 + (a ? 1 : 0), 1, 3, 'G'); Px(12, 12 + (a ? 1 : 0), 1, 1, 'S');
        Map(a ? LEGS_A : LEGS_B, 0, 12);
        if (t == 0) { Px(-3, 14, 1, 1, 'Z'); Px(-2, 13, 1, 1, 'Z'); }   // 먼지
    }

    void Cheer()
    {
        int t = fr % 4;
        jy = t < 2 ? -2 : 0;
        Map(BODY, 0, 9);
        Map(LEGS_B, 0, 12);
        bool w = fr % 2 == 0;
        Px(-1, 4 + (w ? 0 : 1), 2, 5, 'G'); Px(11, 4 + (w ? 1 : 0), 2, 5, 'G');
        Px(-1, 2 + (w ? 0 : 1), 2, 2, 'S'); Px(11, 2 + (w ? 1 : 0), 2, 2, 'S');
        Head(0, 0, 'K', 'K', 'M');
        Map(CHIN, 0, 8);
        // 컨페티
        char[] cs = { 'Y', 'p', 'b', 'n', 'C', 'Y' };
        for (int i = 0; i < 6; i++)
        {
            int cx = (i * 5 + fr * 3) % 17 - 2;
            int cy = (fr * 2 + i * 4) % 12;
            Px(cx, cy, 1, 1, cs[i]);
        }
    }


    // ===== 여자1: 히피펌 커리어우먼 (14칸 폭, x=-1 부터) =====
    static readonly string[] FHEAD = {
        "..KKKKKKKKKK..",
        ".KHHhHHHHhHHK.",
        "KHHHhHSSHhHHHK",
        "KHHhHSSSSSHhHK",
        "KHHSSSSSSSSHHK",
        "KHHSeSSSSeSHHK",
        "KHHgSgSSgSgHHK",
        "KHHBSMMMMSBHHK",
        "KHHKSSSSSSKHHK",
        "KHhHKKKKKKHhHK" };
    static readonly string[] FBODY = {
        "KHHKRRRRRRKHHK",
        "KHHKRrRRrRKHHK",
        "KHhKRRrRRrKhHK",
        ".KHKKPPPPKKHK." };

    // eye: 0=^ 웃는 눈, 1=감은 눈(졸음), 2=뜬 눈 / mouth: 0=활짝, 1=작게 / back: 뒷모습
    void FHead(int dy, int eye, int mouth, bool back)
    {
        for (int r = 0; r < FHEAD.Length; r++)
            for (int c = 0; c < FHEAD[r].Length; c++)
            {
                char ch = FHEAD[r][c];
                if (ch == '.') continue;
                if (back && r >= 2 && c >= 3 && c <= 10 && !(r == 8 && (c == 3 || c == 10) && false))
                    ch = ((r * 5 + c * 3) % 7 == 0) ? 'h' : 'H';
                else
                {
                    if (ch == 'e') ch = eye == 0 ? 'K' : (eye == 2 ? 'K' : 'S');
                    else if (ch == 'g') ch = eye == 0 ? 'K' : (eye == 1 ? 'K' : 'S');
                    if (eye == 1 && r == 6 && (c == 4 || c == 9)) ch = 'K';
                    if (r == 7 && mouth == 1 && (c == 5 || c == 8)) ch = 'S';
                }
                Px(c - 1, r + dy, 1, 1, ch);
            }
    }
    void FBody(int dy) { Map(FBODY, -1, 10 + dy); }

    void FCall()
    {
        bool talk = fr % 4 < 2;
        FHead(0, 0, talk ? 0 : 1, false);
        FBody(0);
        // 왼손은 키보드 위, 오른손은 전화기
        Px(11, 8, 2, 2, 'R'); Px(12, 6, 2, 2, 'S');
        Px(13, 2, 3, 6, 'K'); Px(14, 3, 1, 4, 'L');
        Px(2, 11, 2, 2, 'S');
        if (fr % 6 < 3) { Px(16, 3, 1, 1, 'Z'); Px(16, 5, 1, 1, 'Z'); }
        else { Px(15, 1, 1, 1, 'Z'); Px(16, 4, 1, 1, 'Z'); }
        if (talk) Px(-2, 4, 1, 1, 'Y');
    }

    void FSmash()
    {
        bool punch = fr % 4 < 2;
        FHead(0, 0, 0, true);
        FBody(0);
        // 오른쪽 벽 (벽돌)
        for (int y = 1; y <= 13; y++)
        {
            Px(14, y, 2, 1, (y % 3 == 0) ? 'K' : 'c');
            if (y % 3 != 0) Px(14 + (y % 2), y, 1, 1, 'U');
        }
        // 팔: 뻗어서 벽을 침 / 당겼다가 다시
        if (punch)
        {
            Px(9, 10, 4, 2, 'R'); Px(12, 9, 2, 3, 'S');
            Px(12, 7, 1, 1, 'Y'); Px(14, 8, 1, 1, 'Y'); Px(13, 12, 1, 1, 'Y'); Px(15, 6, 1, 1, 'Z');
            Px(14, 5, 1, 4, 'K'); Px(15, 8, 1, 2, 'K');       // 금이 감
        }
        else
        {
            Px(9, 9, 2, 3, 'R'); Px(10, 7, 2, 2, 'S');
        }
        // 화난 표시 + 머리 위 김
        Px(0, 0, 3, 1, 'C'); Px(1, -1, 1, 3, 'C');
        int u = (fr / 2) % 3; Px(8 + u, -1 - 0, 1, 1, 'W');
    }

    void FCoffee()
    {
        bool sip = fr % 10 >= 5;
        FHead(0, 0, sip ? 1 : 0, false);
        FBody(0);
        int my = sip ? 8 : 10;
        Px(4, my, 5, 4, 'K'); Px(5, my + 1, 3, 2, 'C'); Px(9, my + 1, 1, 2, 'K');
        Px(3, my + 2, 1, 2, 'S'); Px(9, my + 2, 1, 2, 'S');
        if (!sip) { int u = (fr / 2) % 3; Px(5 + u, 7 - u, 1, 1, 'Z'); }
        else Px(12, 3, 1, 1, 'p');
    }

    void FCat()
    {
        bool pet = fr % 4 < 2;
        FHead(0, 0, 0, false);
        FBody(0);
        // 쓰다듬는 손
        Px(8, 8, 2, 2, 'R'); Px(10, pet ? 6 : 7, 2, 2, 'S');
        // 고양이 (무릎 위)
        Px(9, 8, 6, 5, 'K'); Px(10, 9, 4, 3, 'O');
        Px(9, 7, 1, 1, 'K'); Px(14, 7, 1, 1, 'K'); Px(9, 8, 1, 1, 'K');
        Px(10, 9, 1, 1, 'o'); Px(13, 9, 1, 1, 'o');
        Px(10, 10, 1, 1, 'K'); Px(13, 10, 1, 1, 'K'); Px(11, 11, 2, 1, 'p');
        Px(9, 12, 7, 2, 'K'); Px(10, 12, 5, 1, 'O');
        Px(15, pet ? 10 : 9, 1, 3, 'O'); Px(15, pet ? 9 : 8, 1, 1, 'o');
        if (pet) { Px(12, 3, 1, 1, 'p'); Px(13, 2, 1, 1, 'p'); } else Px(11, 2, 1, 1, 'p');
    }

    void FSleep()
    {
        int sw = (fr % 12) < 6 ? 0 : 1;
        FBody(0);
        FHead(2, 1, 1, false);
        Px(-1, 12, 14, 1, 'K'); Px(-1, 13, 14, 1, 'c');
        int z = fr % 12;
        Z(11, 2 - z / 4, 3);
        Z(14, -1 - z / 6 + 1, 3);
        if (z > 7) Px(10, 8, 1, 1, 'b');
        Px(4 + sw, 7, 1, 1, 'b');
    }

    void FLeave(bool cheer)
    {
        int t = fr % 4;
        jy = cheer ? (t < 2 ? -2 : 0) : (t < 2 ? 0 : -1);
        FHead(0, 0, 0, false);
        FBody(0);
        // 가방끈 + 가방
        Px(8, 10, 1, 3, 'U'); Px(11, 11, 3, 3, 'K'); Px(12, 12, 1, 1, 'U');
        bool w = fr % 2 == 0;
        if (cheer)
        {
            Px(-2, 4 + (w ? 0 : 1), 2, 6, 'R'); Px(12, 4 + (w ? 1 : 0), 2, 6, 'R');
            Px(-2, 2 + (w ? 0 : 1), 2, 2, 'S'); Px(12, 2 + (w ? 1 : 0), 2, 2, 'S');
            char[] cs = { 'Y', 'p', 'b', 'n', 'C', 'Y' };
            for (int i = 0; i < 6; i++) Px((i * 5 + fr * 3) % 17 - 2, (fr * 2 + i * 4) % 12, 1, 1, cs[i]);
        }
        else
        {
            Px(11, 6, 2, 4, 'R'); Px(w ? 12 : 13, 4, 2, 2, 'S');      // 손 흔들기
            if (fr % 6 < 3) { Px(-1, 1, 1, 1, 'Y'); Px(14, 12, 1, 1, 'Y'); } else Px(1, 0, 1, 1, 'Y');
        }
    }

    void FEat()
    {
        int t = fr % 8; bool up = t >= 4;
        int bob = (t == 2 || t == 6) ? 1 : 0;
        FBody(0);
        FHead(bob, 0, up ? 1 : 0, false);
        Px(2, 11, 1, 1, 'K'); Px(9, 11, 1, 1, 'K'); Px(3, 11, 6, 1, 'R');
        Px(2, 12, 1, 1, 'K'); Px(9, 12, 1, 1, 'K'); Px(3, 12, 6, 1, 'C');
        Px(-1, 13, 14, 1, 'c');
        if (up) { Px(10, 9, 1, 2, 'R'); Px(11, 7, 2, 2, 'S'); Px(13, 6, 1, 2, 'Z'); }
        else { Px(10, 10, 1, 2, 'R'); Px(10, 11, 2, 1, 'S'); Px(12, 12, 1, 1, 'Z'); }
        int u = (fr / 2) % 3; Px(5 + u, 9, 1, 1, 'W');
    }

    public void Draw(string ch, string act)
    {
        if (ch == "f1")
        {
            switch (act)
            {
                case "call": FCall(); break;
                case "smash": FSmash(); break;
                case "coffee": FCoffee(); break;
                case "cat": FCat(); break;
                case "sleep": FSleep(); break;
                case "eat": FEat(); break;
                case "cheer": FLeave(true); break;
                default: FLeave(false); break;
            }
            return;
        }
        Draw(act);
    }

    public void Draw(string act)
    {
        switch (act)
        {
            case "walk": Walk(); break;
            case "cheer": Cheer(); break;
            case "coffee": Coffee(); break;
            case "sleep": Sleep(); break;
            case "eat": Eat(); break;
            default: Work(); break;
        }
    }
}

// 사용자 설정 (투명도) - 문서\출근기록\settings.ini
static class Settings
{
    public static int Opacity = 100;
    public static bool WeatherOn = true;
    public static string UpdateBase = "";   // 업데이트 주소: https://... 폴더 URL 또는 공유 폴더 경로 (version.txt + iwant2gohome.cs)
    public static string Char = "dev";   // dev: 개발자 / f1: 여자1
    public static int SizePct = 100;                       // 위젯 크기(%)
    public static int PosX = int.MinValue, PosY = int.MinValue;   // 저장된 위치 (없으면 오른쪽 아래)
    public static string ClockMode = "input";   // input: 그날 첫 키보드·마우스 입력 / boot: 컴퓨터 부팅 시각
    public static double Lat = 35.1796, Lon = 129.0756;   // 기본 위치: 부산 (settings.ini 에서 lat / lon 수정 가능)
    static string PathIni { get { return Path.Combine(Store.Dir, "settings.ini"); } }
    public static void Load()
    {
        try
        {
            if (!File.Exists(PathIni)) return;
            foreach (string line in File.ReadAllLines(PathIni, Encoding.UTF8))
            {
                string[] kv = line.Split('=');
                if (kv.Length < 2) continue;
                string k = kv[0].Trim(), val = kv[1].Trim();
                int v; double d;
                if (k == "opacity" && int.TryParse(val, out v) && v >= 20 && v <= 100) Opacity = v;
                else if (k == "weather") WeatherOn = val != "off";
                else if (k == "update") UpdateBase = line.Substring(line.IndexOf('=') + 1).Trim();
                else if (k == "char") Char = val == "f1" ? "f1" : "dev";
                else if (k == "size" && int.TryParse(val, out v) && v >= 50 && v <= 300) SizePct = v;
                else if (k == "x" && int.TryParse(val, out v)) PosX = v;
                else if (k == "y" && int.TryParse(val, out v)) PosY = v;
                else if (k == "clockin") ClockMode = val == "boot" ? "boot" : "input";
                else if (k == "lat" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) Lat = d;
                else if (k == "lon" && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) Lon = d;
            }
        }
        catch { }
    }
    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Store.Dir);
            string t = "opacity=" + Opacity + Environment.NewLine + "weather=" + (WeatherOn ? "on" : "off") + Environment.NewLine + "clockin=" + ClockMode + Environment.NewLine + "update=" + UpdateBase + Environment.NewLine + "char=" + Char + Environment.NewLine + "size=" + SizePct + Environment.NewLine
                + (PosX != int.MinValue ? "x=" + PosX + Environment.NewLine + "y=" + PosY + Environment.NewLine : "")
                + "lat=" + Lat.ToString(CultureInfo.InvariantCulture) + Environment.NewLine + "lon=" + Lon.ToString(CultureInfo.InvariantCulture) + Environment.NewLine;
            File.WriteAllText(PathIni, t, Encoding.UTF8);
        }
        catch { }
    }
}

// 자체 업데이트: 서버(URL 또는 공유 폴더)의 version.txt 숫자가 더 크면 iwant2gohome.cs 를 받아 컴파일 후 교체·재시작
static class Updater
{
    public const int Version = 4;
    static bool busy;
    public static bool Busy { get { return busy; } }

    static byte[] Fetch(string name)
    {
        string b = Settings.UpdateBase.Trim();
        if (b.Length == 0) throw new Exception("업데이트 주소가 설정되지 않았습니다.");
        if (b.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
            using (WebClient wc = new WebClient())
            {
                wc.Headers[HttpRequestHeader.CacheControl] = "no-cache";
                string url = b.TrimEnd('/') + "/" + name + "?t=" + DateTime.UtcNow.Ticks;
                return wc.DownloadData(url);
            }
        }
        return File.ReadAllBytes(Path.Combine(b, name));
    }

    // 최신 버전 번호 조회 (실패 시 예외)
    public static int Latest()
    {
        string t = Encoding.UTF8.GetString(Fetch("version.txt")).Trim().TrimStart('﻿');
        int v;
        if (!int.TryParse(t, out v)) throw new Exception("version.txt 형식이 올바르지 않습니다: " + t);
        return v;
    }

    // 소스 받기 → 컴파일 → 교체 후 재시작 스크립트 실행. 성공하면 null, 실패하면 오류 메시지
    public static string Apply()
    {
        if (busy) return "이미 업데이트 중입니다.";
        busy = true;
        try
        {
            string tmp = Path.Combine(Path.GetTempPath(), "iwant2gohome_upd");
            Directory.CreateDirectory(tmp);
            string cs = Path.Combine(tmp, "iwant2gohome.cs");
            string newExe = Path.Combine(tmp, "iwant2gohome.new.exe");
            File.WriteAllBytes(cs, Fetch("iwant2gohome.cs"));
            if (File.Exists(newExe)) File.Delete(newExe);

            string csc = Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "csc.exe");
            if (!File.Exists(csc)) return "컴파일러(csc.exe)를 찾을 수 없습니다.";
            ProcessStartInfo psi = new ProcessStartInfo(csc, "/nologo /target:winexe /out:\"" + newExe + "\" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll \"" + cs + "\"");
            psi.UseShellExecute = false; psi.CreateNoWindow = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true;
            using (Process pr = Process.Start(psi))
            {
                string o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd();
                pr.WaitForExit();
                if (pr.ExitCode != 0 || !File.Exists(newExe)) { Log.Write("업데이트 컴파일 실패: " + o); return "새 버전을 만드는 데 실패했습니다. (error.log 확인)"; }
            }

            string exe = Application.ExecutablePath;
            string bat = Path.Combine(tmp, "swap.bat");
            string script = "@echo off\r\nfor /l %%i in (1,1,20) do (\r\n  ping 127.0.0.1 -n 2 >nul\r\n  move /y \"" + newExe + "\" \"" + exe + "\" >nul 2>&1 && goto done\r\n)\r\n:done\r\nstart \"\" \"" + exe + "\"\r\n";
            File.WriteAllText(bat, script, Encoding.Default);
            ProcessStartInfo sp = new ProcessStartInfo("cmd.exe", "/c \"" + bat + "\"");
            sp.UseShellExecute = false; sp.CreateNoWindow = true;
            Process.Start(sp);
            return null;
        }
        catch (Exception ex) { Log.Write("업데이트 실패: " + ex.Message); return "업데이트 실패: " + ex.Message; }
        finally { busy = false; }
    }
}

// 실시간 날씨 (Open-Meteo, 키 필요 없음). 접속 주소: https://api.open-meteo.com
static class Weather
{
    public static string Kind = "clear";   // clear / partly / cloudy / rain / snow / storm
    public static bool IsDay = true;
    static DateTime last = DateTime.MinValue;
    static bool busy, ok;

    public static void Tick()
    {
        if (!Settings.WeatherOn || busy) return;
        if ((DateTime.Now - last).TotalMinutes < (ok ? 30 : 5)) return;
        busy = true; last = DateTime.Now;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                try { ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; } catch { }
                string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Settings.Lat.ToString(CultureInfo.InvariantCulture)
                    + "&longitude=" + Settings.Lon.ToString(CultureInfo.InvariantCulture) + "&current=weather_code,cloud_cover,is_day";
                using (WebClient wc = new WebClient())
                {
                    wc.Encoding = Encoding.UTF8;
                    Parse(wc.DownloadString(url));
                }
                ok = true;
            }
            catch (Exception ex)
            {
                ok = false;
                Log.Write("날씨 조회 실패 (api.open-meteo.com 접속이 허용되어야 합니다): " + ex.Message);
            }
            finally { busy = false; }
        });
    }

    static void Parse(string j)
    {
        int idx = j.LastIndexOf("\"current\":");
        if (idx < 0) return;
        string c = j.Substring(idx);
        Match mc = Regex.Match(c, "\"weather_code\":(\\d+)");
        Match mv = Regex.Match(c, "\"cloud_cover\":(\\d+)");
        Match md = Regex.Match(c, "\"is_day\":(\\d)");
        if (!mc.Success) return;
        int code = int.Parse(mc.Groups[1].Value);
        int cover = mv.Success ? int.Parse(mv.Groups[1].Value) : 0;
        IsDay = !md.Success || md.Groups[1].Value == "1";
        Kind = FromCode(code, cover);
    }

    public static string FromCode(int code, int cover)
    {
        if (code >= 95) return "storm";
        if ((code >= 71 && code <= 77) || code == 85 || code == 86) return "snow";
        if ((code >= 51 && code <= 67) || (code >= 80 && code <= 82)) return "rain";
        if (code == 45 || code == 48 || code == 3) return "cloudy";
        if (code == 2) return cover >= 70 ? "cloudy" : "partly";
        return cover >= 60 ? "partly" : "clear";
    }
}

class WidgetForm : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    float dpiScale = 1f, scale = 1f;
    bool dragging, resizing; Point dragStart, formStart; int startSize; Size startClient;
    readonly List<ToolStripMenuItem> sizeItems = new List<ToolStripMenuItem>();
    const int GRIP = 14;
    readonly NotifyIcon tray = new NotifyIcon();
    readonly List<ToolStripMenuItem> opacityItems = new List<ToolStripMenuItem>();
    ToolStripMenuItem weatherItem, modeInput, modeBoot;
    ContextMenuStrip charMenu;
    int promptedVersion;
    readonly List<ToolStripMenuItem> charItems = new List<ToolStripMenuItem>();
    Point downPos; bool moved;
    bool pending;
    [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
    [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);

    static DateTime? LastInput(DateTime now)
    {
        try
        {
            LASTINPUTINFO l = new LASTINPUTINFO(); l.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            if (!GetLastInputInfo(ref l)) return null;
            uint idle = unchecked((uint)Environment.TickCount - l.dwTime);
            return now.AddMilliseconds(-(double)idle);
        }
        catch { return null; }
    }

    // 출근시간 결정: 기록이 있으면 그대로, 없으면 설정에 따라 '첫 입력' 또는 '부팅 시각'
    void ResolveClock(DateTime now)
    {
        DateTime? r = Store.Read(workDay);
        if (r.HasValue) { clockIn = r.Value; pending = false; return; }
        if (Settings.ClockMode == "boot") { clockIn = Store.ResolveClockIn(now); pending = false; return; }
        DateTime? li = LastInput(now);
        if (li.HasValue && li.Value >= workDay.AddHours(4) && li.Value <= now)
        {
            DateTime t = li.Value;
            t = new DateTime(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0);
            Store.Write(workDay, t); clockIn = t; pending = false;
        }
        else
        {
            clockIn = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0); pending = true;   // 첫 입력을 기다리는 중(저장 안 함)
        }
    }
    DateTime clockIn, workDay, cheerUntil = DateTime.MinValue;
    string viewPref = "auto", act = "work";
    int tk, fr, ox, wdir = 1;
    bool wasOver, inited;
    Model model;

    // 모든 일반 창의 맨 아래(바탕화면 바로 위)에 고정: 다른 창을 띄우면 위젯이 가려진다
    void PinBottom()
    {
        if (IsHandleCreated && Visible) SetWindowPos(Handle, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE);
    }
    protected override void OnShown(EventArgs e) { base.OnShown(e); PinBottom(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); PinBottom(); }

    public WidgetForm()
    {
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) dpiScale = g.DpiX / 96f;
        if (dpiScale < 1f) dpiScale = 1f;
        Settings.Load();
        Opacity = Settings.Opacity / 100.0;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = false;
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        ApplyScale(Settings.SizePct, false);
        PlaceInitial();
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += delegate { PlaceInitial(); };

        DateTime now = DateTime.Now;
        workDay = Store.WorkDay(now);
        ResolveClock(now);

        BuildMenus();

        timer.Interval = 125;
        timer.Tick += OnTick;
        timer.Start();
        OnTick(null, EventArgs.Empty);
    }

    void BuildMenus()
    {
        ContextMenuStrip menu = new ContextMenuStrip();
        menu.Items.Add("출근시간 수정…", null, delegate { EditClockIn(); });

        ToolStripMenuItem op = new ToolStripMenuItem("투명도");
        foreach (int v in new int[] { 100, 90, 80, 70, 60, 50, 40, 30 })
        {
            int val = v;
            ToolStripMenuItem it = new ToolStripMenuItem(val + "%");
            it.Tag = val;
            it.Click += delegate { SetOpacity(val); };
            opacityItems.Add(it);
            op.DropDownItems.Add(it);
        }
        menu.Items.Add(op);
        ToolStripMenuItem md = new ToolStripMenuItem("출근시간 기준");
        modeInput = new ToolStripMenuItem("그날 첫 키보드·마우스 입력");
        modeBoot = new ToolStripMenuItem("컴퓨터 부팅 시각");
        modeInput.Click += delegate { Settings.ClockMode = "input"; Settings.Save(); };
        modeBoot.Click += delegate { Settings.ClockMode = "boot"; Settings.Save(); };
        md.DropDownItems.Add(modeInput); md.DropDownItems.Add(modeBoot);
        menu.Items.Add(md);
        menu.Items.Add("업데이트 확인…  (v" + Updater.Version + ")", null, delegate { CheckUpdate(true); });
        menu.Items.Add("업데이트 주소 설정…", null, delegate { EditUpdateBase(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("캐릭터 선택 (왼쪽 클릭)", null, delegate { ShowCharMenu(); });
        ToolStripMenuItem sz = new ToolStripMenuItem("크기 (드래그·휠로도 조절)");
        foreach (int v in new int[] { 70, 85, 100, 120, 150, 180, 220 })
        {
            int val = v;
            ToolStripMenuItem it = new ToolStripMenuItem(val + "%");
            it.Tag = val;
            it.Click += delegate { ApplyScale(val, true); Settings.Save(); };
            sizeItems.Add(it); sz.DropDownItems.Add(it);
        }
        menu.Items.Add(sz);
        menu.Items.Add("위치·크기 초기화", null, delegate { Settings.PosX = int.MinValue; Settings.PosY = int.MinValue; ApplyScale(100, false); PlaceBottomRight(); Settings.Save(); });
        weatherItem = new ToolStripMenuItem("날씨 효과");
        weatherItem.Click += delegate { Settings.WeatherOn = !Settings.WeatherOn; Settings.Save(); if (!Settings.WeatherOn) Weather.Kind = "clear"; };
        menu.Items.Add(weatherItem);
        menu.Items.Add("최소화 (트레이로)", null, delegate { MinimizeToTray(); });
        menu.Items.Add("기록 폴더 열기", null, delegate { try { Process.Start(Store.Dir); } catch { } });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, delegate { Close(); });
        menu.Opening += delegate { RefreshChecks(); };
        ContextMenuStrip = menu;

        ContextMenuStrip tm = new ContextMenuStrip();
        tm.Items.Add("위젯 보이기", null, delegate { RestoreFromTray(); });
        tm.Items.Add("종료", null, delegate { Close(); });
        tray.ContextMenuStrip = tm;
        tray.Text = "iwant2gohome";
        tray.Icon = MakeIcon();
        tray.MouseClick += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) RestoreFromTray(); };
        tray.Visible = false;
    }

    void RefreshChecks()
    {
        foreach (ToolStripMenuItem it in opacityItems) it.Checked = ((int)it.Tag) == Settings.Opacity;
        weatherItem.Checked = Settings.WeatherOn;
        foreach (ToolStripMenuItem it in sizeItems) it.Checked = ((int)it.Tag) == Settings.SizePct;
        modeInput.Checked = Settings.ClockMode == "input"; modeBoot.Checked = Settings.ClockMode == "boot";
    }
    void SetOpacity(int v)
    {
        Settings.Opacity = v; Opacity = v / 100.0; Settings.Save();
    }
    void MinimizeToTray() { tray.Visible = true; Hide(); }
    void RestoreFromTray() { Show(); PinBottom(); tray.Visible = false; }

    static Icon MakeIcon()
    {
        using (Bitmap b = new Bitmap(16, 16))
        {
            using (Graphics gg = Graphics.FromImage(b))
            {
                gg.Clear(Color.Transparent);
                using (SolidBrush hair = new SolidBrush(Color.FromArgb(47, 42, 45)))
                using (SolidBrush skin = new SolidBrush(Color.FromArgb(243, 207, 165)))
                using (SolidBrush lap = new SolidBrush(Color.FromArgb(141, 147, 163)))
                using (SolidBrush eye = new SolidBrush(Color.FromArgb(17, 28, 40)))
                {
                    gg.FillRectangle(hair, 2, 1, 12, 5);
                    gg.FillRectangle(skin, 3, 5, 10, 6);
                    gg.FillRectangle(eye, 5, 7, 2, 2);
                    gg.FillRectangle(eye, 9, 7, 2, 2);
                    gg.FillRectangle(lap, 1, 11, 14, 4);
                }
            }
            Icon ic = Icon.FromHandle(b.GetHicon());
            return (Icon)ic.Clone();
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        tray.Visible = false; tray.Dispose();
        base.OnFormClosed(e);
    }

    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams
    {
        get { CreateParams cp = base.CreateParams; cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE; return cp; }
    }

    void PlaceBottomRight()
    {
        Rectangle wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - (int)(12 * scale), wa.Bottom - Height - (int)(12 * scale));
    }

    // 저장된 위치가 화면 안이면 그 위치, 아니면 오른쪽 아래
    void PlaceInitial()
    {
        if (Settings.PosX != int.MinValue)
        {
            Rectangle r = new Rectangle(Settings.PosX, Settings.PosY, Width, Height);
            foreach (Screen sc in Screen.AllScreens)
            {
                Rectangle inter = Rectangle.Intersect(sc.WorkingArea, r);
                if (inter.Width >= 40 && inter.Height >= 30) { Location = new Point(Settings.PosX, Settings.PosY); return; }
            }
        }
        PlaceBottomRight();
    }

    // 크기 변경 (pct: 100 = 기본). 위치를 저장해 둔 경우 왼쪽 위 고정, 아니면 오른쪽 아래 기준 유지
    void ApplyScale(int pct, bool keepAnchor)
    {
        if (pct < 50) pct = 50; if (pct > 300) pct = 300;
        Settings.SizePct = pct;
        scale = dpiScale * pct / 100f;
        Point br = new Point(Right, Bottom);
        ClientSize = new Size((int)Math.Round(Ui.W * scale), (int)Math.Round(Ui.H * scale));
        using (GraphicsPath gp = Ui.RoundPath(0, 0, ClientSize.Width, ClientSize.Height, (int)Math.Round(10 * scale)))
        {
            Region old = Region;
            Region = new Region(gp);
            if (old != null) old.Dispose();
        }
        if (keepAnchor && Settings.PosX == int.MinValue) Location = new Point(br.X - Width, br.Y - Height);
        Invalidate();
    }
    static DialogResult Ask(string text, string title, MessageBoxButtons b)
    {
        using (Form owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-2000, -2000), Size = new Size(1, 1), FormBorderStyle = FormBorderStyle.None })
        {
            owner.Show();
            return MessageBox.Show(owner, text, title, b, MessageBoxIcon.Information);
        }
    }

    // manual=true: 결과를 항상 알려줌 / false: 새 버전이 있을 때만 물어봄
    void CheckUpdate(bool manual)
    {
        if (Settings.UpdateBase.Trim().Length == 0)
        {
            if (manual) { Ask("업데이트 주소가 아직 없습니다.\n우클릭 > '업데이트 주소 설정…' 에서 입력해 주세요.", "iwant2gohome", MessageBoxButtons.OK); }
            return;
        }
        ThreadPool.QueueUserWorkItem(delegate
        {
            int latest = 0; string err = null;
            try { latest = Updater.Latest(); }
            catch (Exception ex) { err = ex.Message; Log.Write("업데이트 확인 실패 (" + Settings.UpdateBase + "): " + ex.Message); }
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (err != null) { if (manual) Ask("업데이트 확인에 실패했습니다.\n" + err + "\n(사내망에서 해당 주소 접속이 허용되어야 합니다)", "iwant2gohome", MessageBoxButtons.OK); return; }
                    if (latest <= Updater.Version) { if (manual) Ask("최신 버전입니다. (v" + Updater.Version + ")", "iwant2gohome", MessageBoxButtons.OK); return; }
                    if (!manual && promptedVersion == latest) return;
                    promptedVersion = latest;
                    if (Ask("새 버전(v" + latest + ")이 있습니다. 지금 업데이트할까요?\n(현재 v" + Updater.Version + ", 업데이트 후 위젯이 자동으로 다시 시작됩니다)", "iwant2gohome 업데이트", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                    DoUpdate();
                });
            }
            catch { }
        });
    }

    void DoUpdate()
    {
        tray.Text = "iwant2gohome 업데이트 중…";
        ThreadPool.QueueUserWorkItem(delegate
        {
            string r = Updater.Apply();
            try
            {
                BeginInvoke((Action)delegate
                {
                    if (r == null) { tray.Visible = false; Application.Exit(); }
                    else { tray.Text = "iwant2gohome"; Ask(r, "iwant2gohome 업데이트", MessageBoxButtons.OK); }
                });
            }
            catch { }
        });
    }

    void EditUpdateBase()
    {
        using (Form f = new Form())
        {
            f.Text = "업데이트 주소 설정";
            f.FormBorderStyle = FormBorderStyle.FixedDialog; f.StartPosition = FormStartPosition.CenterScreen;
            f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.TopMost = true;
            f.ClientSize = new Size(420, 120);
            Label l = new Label { Text = "version.txt 와 iwant2gohome.cs 가 있는 주소(URL) 또는 공유 폴더 경로", Left = 12, Top = 12, AutoSize = true };
            TextBox tb = new TextBox { Left = 12, Top = 36, Width = 396, Text = Settings.UpdateBase };
            Button ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Left = 252, Top = 76, Width = 75 };
            Button cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Left = 333, Top = 76, Width = 75 };
            f.Controls.AddRange(new Control[] { l, tb, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog() == DialogResult.OK) { Settings.UpdateBase = tb.Text.Trim(); Settings.Save(); promptedVersion = 0; CheckUpdate(true); }
        }
    }

    void SetChar(string id)
    {
        Settings.Char = id; Settings.Save();
        act = "work"; ox = 0; cheerUntil = DateTime.MinValue;
        OnTick(null, EventArgs.Empty);
    }
    void ShowCharMenu()
    {
        if (charMenu == null)
        {
            charMenu = new ContextMenuStrip();
            charMenu.Items.Add(new ToolStripLabel("캐릭터 선택"));
            string[] ids = { "dev", "f1" }, names = { "개발자 (회색 후드티)", "여자1 (히피펌 커리어우먼)" };
            for (int i = 0; i < ids.Length; i++)
            {
                string id = ids[i];
                ToolStripMenuItem it = new ToolStripMenuItem(names[i]);
                it.Tag = id;
                it.Click += delegate { SetChar(id); };
                charItems.Add(it); charMenu.Items.Add(it);
            }
        }
        foreach (ToolStripMenuItem it in charItems) it.Checked = (string)it.Tag == Settings.Char;
        charMenu.Show(Control.MousePosition);
    }
    void SaveBounds() { Settings.PosX = Left; Settings.PosY = Top; Settings.Save(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        dragStart = Control.MousePosition; formStart = Location; downPos = dragStart; moved = false;
        if (e.X >= ClientSize.Width - GRIP && e.Y >= ClientSize.Height - GRIP) { resizing = true; startSize = Settings.SizePct; startClient = ClientSize; }
        else dragging = true;
        Capture = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Point m = Control.MousePosition;
        if ((Math.Abs(m.X - downPos.X) > 3 || Math.Abs(m.Y - downPos.Y) > 3) && (dragging || resizing)) moved = true;
        if (dragging)
            Location = new Point(formStart.X + m.X - dragStart.X, formStart.Y + m.Y - dragStart.Y);
        else if (resizing)
        {
            double fx = (startClient.Width + m.X - dragStart.X) / (double)startClient.Width;
            double fy = (startClient.Height + m.Y - dragStart.Y) / (double)startClient.Height;
            int pct = (int)Math.Round(startSize * Math.Max(fx, fy) / 5.0) * 5;
            if (pct != Settings.SizePct) { Settings.PosX = Left; Settings.PosY = Top; ApplyScale(pct, false); }
        }
        else
            Cursor = (e.X >= ClientSize.Width - GRIP && e.Y >= ClientSize.Height - GRIP) ? Cursors.SizeNWSE : Cursors.Default;
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        bool wasDrag = dragging, wasRes = resizing;
        if (dragging || resizing) { dragging = false; resizing = false; Capture = false; if (moved) SaveBounds(); }
        if (wasDrag && !moved && e.Button == MouseButtons.Left) ShowCharMenu();
    }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ApplyScale(Settings.SizePct + (e.Delta > 0 ? 10 : -10), true);
        Settings.Save();
    }

    // 행동은 15분마다 바뀐다 (같은 15분 구간에서는 항상 같은 행동 → 재실행해도 일관)
    static string SlotAct(DateTime now, bool over, string ch)
    {
        if (ch == "f1")
        {
            long sl = now.Ticks / TimeSpan.TicksPerMinute / 15;
            Random fr2 = new Random((int)(sl % int.MaxValue) * 6151 + 29);
            string[] pool = { "call", "smash", "coffee", "cat", "sleep", "leave" };
            return pool[fr2.Next(pool.Length)];
        }
        long slot = now.Ticks / TimeSpan.TicksPerMinute / 15;
        Random r = new Random((int)(slot % int.MaxValue) * 7919 + 13);
        int v = r.Next(100);
        if (over) return v < 35 ? "sleep" : (v < 65 ? "coffee" : (v < 80 ? "walk" : "work"));
        return v < 40 ? "work" : (v < 62 ? "coffee" : (v < 82 ? "walk" : "sleep"));
    }

    string CurAct(DateTime now) { return now < cheerUntil ? "cheer" : act; }

    void OnTick(object s, EventArgs e)
    {
        try
        {
            tk++; if (tk % 2 == 0) fr++;
            if (tk % 8 == 0) PinBottom();
            if (tk % 40 == 0) Weather.Tick();
            if (Settings.UpdateBase.Length > 0 && (tk == 160 || (tk > 160 && tk % 172800 == 160))) CheckUpdate(false);
            if (pending && tk % 4 == 0) ResolveClock(DateTime.Now);
            DateTime now = DateTime.Now;
            if (Store.WorkDay(now) != workDay)
            {
                workDay = Store.WorkDay(now);
                ResolveClock(now);
                viewPref = "auto"; wasOver = false;
            }
            Model m0 = Model.Build(now, clockIn, viewPref, "work", 0);
            string slot = m0.Phase == "meal" ? "eat" : SlotAct(now, m0.Over, Settings.Char);
            if (slot != act) { act = slot; ox = 0; }
            if (m0.Over && !wasOver && inited) cheerUntil = now.AddSeconds(10);
            wasOver = m0.Over; inited = true;

            string a = CurAct(now);
            if (a == "walk" && Settings.Char != "f1")
            {
                ox += wdir;
                if (ox > 14) { ox = 14; wdir = -1; }
                if (ox < -14) { ox = -14; wdir = 1; }
            }
            model = Model.Build(now, clockIn, viewPref, a, ox);
            if (Visible) Invalidate();
        }
        catch (Exception ex) { Log.Write("Tick: " + ex.Message); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (model == null) return;
        e.Graphics.ScaleTransform(scale, scale);
        Ui.Draw(e.Graphics, model, fr);
    }

    void EditClockIn()
    {
        using (Form f = new Form())
        {
            f.Text = "출근시간 수정";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.MaximizeBox = false; f.MinimizeBox = false; f.ShowInTaskbar = false; f.TopMost = true;
            f.ClientSize = new Size(240, 100);
            Label l = new Label { Text = "오늘 출근시간 (시:분)", Left = 14, Top = 14, AutoSize = true };
            DateTimePicker dp = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Left = 14, Top = 38, Width = 90, Value = clockIn };
            Button ok = new Button { Text = "저장", DialogResult = DialogResult.OK, Left = 70, Top = 68, Width = 70 };
            Button cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Left = 150, Top = 68, Width = 70 };
            f.Controls.AddRange(new Control[] { l, dp, ok, cancel });
            f.AcceptButton = ok; f.CancelButton = cancel;
            if (f.ShowDialog() == DialogResult.OK)
            {
                DateTime v = dp.Value;
                clockIn = workDay.AddHours(v.Hour).AddMinutes(v.Minute);
                if (v.Hour < 4) clockIn = clockIn.AddDays(1);
                Store.Write(workDay, clockIn); pending = false;
                OnTick(null, EventArgs.Empty);
            }
        }
    }
}

// 개발/검증용: 화면을 PNG로 저장 (--snapshot 파일.png "yyyy-MM-dd HH:mm:ss" 행동 [뷰])
static class Snapshot
{
    public static void Run(string[] a)
    {
        DateTime now = DateTime.ParseExact(a.Length > 2 ? a[2] : "2026-11-18 14:14:00", "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        string act = a.Length > 3 ? a[3] : "work";
        string view = a.Length > 4 ? a[4] : "auto";
        DateTime ci = now.Date.AddHours(8).AddMinutes(12);
        if (a.Length > 8) { Weather.Kind = a[8]; Weather.IsDay = a.Length <= 9 || a[9] != "0"; }
        if (a.Length > 10) Settings.Char = a[10];
        Model m = Model.Build(now, ci, view, act, 0);
        int sc = a.Length > 5 ? int.Parse(a[5]) : 4;
        using (Bitmap bmp = new Bitmap(Ui.W * sc, Ui.H * sc))
        using (Graphics g = Graphics.FromImage(bmp))
        {
            if (a.Length > 6) bmp.SetResolution(float.Parse(a[6], CultureInfo.InvariantCulture), float.Parse(a[6], CultureInfo.InvariantCulture));
            g.Clear(Color.FromArgb(235, 237, 241));
            g.ScaleTransform(sc, sc);
            Ui.Draw(g, m, a.Length > 7 ? int.Parse(a[7]) : 3);
            bmp.Save(a[1], System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
