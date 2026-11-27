using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;

namespace PotionCoop;

public enum Lang { Ru = 0, En = 1, Uk = 2, Fr = 3, De = 4, Es = 5, Pt = 6, It = 7, Zh = 8, Ja = 9, Ko = 10 }

// Язык надписей мода — по языку игры (11 штук: ru, en, uk, fr, de, es, pt, it, zh, ja, ko).
// Тексты в коде пишем парой «по-русски, по-английски» (Loc.L); для остальных языков берём перевод по английскому тексту
// из таблицы LocData: сначала точное совпадение, потом шаблоны с {0}, {1} (так фразы с подстановками переводятся без правок в коде).
public static class Loc
{
    public static Lang Cur = Lang.Ru;
    public static bool Ru => Cur == Lang.Ru;
    public static bool Cjk => Cur == Lang.Zh || Cur == Lang.Ja || Cur == Lang.Ko;
    static bool _known;
    static float _pollAt;

    // ---------- определение языка ----------
    public static void Detect()
    {
        string how = null; Lang? got = null;
        try { got = FromGame(out how); } catch (Exception e) { Plugin.L.LogWarning("LOC: язык игры (отражение): " + e.Message); }
        string txt = null;
        if (got == null)
        {
            try { got = FromButton(out txt); how = "по тексту кнопки «" + txt + "»"; } catch (Exception e) { Plugin.L.LogWarning("LOC: " + e.Message); }
        }
        if (got == null) return;
        if (!_known || got.Value != Cur)
        {
            Plugin.L.LogInfo($"LOC: язык мода — {got.Value} ({how})");
            Cur = got.Value; _known = true; _memo.Clear();
        }
    }

    // раз в пару секунд проверяем, не сменили ли язык в настройках игры
    public static void Poll()
    {
        if (Time.unscaledTime < _pollAt) return;
        _pollAt = Time.unscaledTime + 3f;
        Detect();
        ApplyGuiFont();
    }

    static bool _logged;
    // ищем в сервисе локализации игры поле/свойство с типом «…Language…» (eLanguage) и читаем его имя
    static Lang? FromGame(out string how)
    {
        how = null;
        var svc = Tourney.LocSvc; if (svc == null) return null;
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        object svcObj = svc;
        var ty = svcObj.GetType();
        Lang? found = null;
        foreach (var m in ty.GetMembers(flags))
        {
            Type mt = null;
            if (m is PropertyInfo pi) { if (pi.GetIndexParameters().Length != 0) continue; mt = pi.PropertyType; }
            else if (m is FieldInfo fi) mt = fi.FieldType;
            if (mt == null) continue;
            bool lang = mt.Name.IndexOf("Language", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!lang && mt.IsGenericType)
                foreach (var ga in mt.GetGenericArguments()) if (ga.Name.IndexOf("Language", StringComparison.OrdinalIgnoreCase) >= 0) lang = true;
            if (!lang) continue;
            object val = null;
            try { val = m is PropertyInfo p2 ? p2.GetValue(svcObj) : ((FieldInfo)m).GetValue(svcObj); } catch { continue; }
            if (val == null) continue;
            if (!(val is Enum))
            {
                // обёртка вроде ReactiveProperty<eLanguage> — берём .Value
                foreach (var nm in new[] { "Value", "CurrentValue" })
                {
                    var vp = val.GetType().GetProperty(nm, flags);
                    if (vp != null) { try { val = vp.GetValue(val); } catch { } break; }
                }
            }
            var s = val?.ToString();
            if (!_logged) Plugin.L.LogInfo($"LOC: у сервиса перевода игры есть «{m.Name}» ({mt.Name}) = {s}");
            if (found == null) { var l = MapName(s); if (l != null) { found = l; how = $"из настроек игры: {m.Name} = {s}"; } }
            if (!_logged && val.GetType().IsEnum) Plugin.L.LogInfo("LOC: языки игры: " + string.Join(", ", Enum.GetNames(val.GetType())));
        }
        _logged = true;
        return found;
    }

    static Lang? MapName(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var sb = new StringBuilder();
        foreach (var c in raw.ToLowerInvariant()) if (c >= 'a' && c <= 'z') sb.Append(c);
        var n = sb.ToString();
        switch (n)
        {
            case "ru": return Lang.Ru; case "en": case "enus": case "engb": return Lang.En; case "uk": case "ua": return Lang.Uk;
            case "fr": return Lang.Fr; case "de": return Lang.De; case "es": return Lang.Es; case "pt": case "ptbr": return Lang.Pt;
            case "it": return Lang.It; case "zh": case "zhcn": case "zhhans": return Lang.Zh; case "ja": case "jp": return Lang.Ja; case "ko": case "kr": return Lang.Ko;
        }
        if (n.Contains("ukr")) return Lang.Uk;
        if (n.Contains("rus")) return Lang.Ru;
        if (n.Contains("eng")) return Lang.En;
        if (n.Contains("fren") || n.Contains("fran")) return Lang.Fr;
        if (n.Contains("germ") || n.Contains("deut")) return Lang.De;
        if (n.Contains("span") || n.Contains("espa") || n.Contains("cast")) return Lang.Es;
        if (n.Contains("port") || n.Contains("brazil")) return Lang.Pt;
        if (n.Contains("ital")) return Lang.It;
        if (n.Contains("chin") || n.Contains("simpl")) return Lang.Zh;
        if (n.Contains("japan")) return Lang.Ja;
        if (n.Contains("korea")) return Lang.Ko;
        return null;
    }

    // запасной способ — по надписи на родной кнопке «Продолжить»
    static Lang? FromButton(out string txt)
    {
        txt = null;
        var st = Coop.ContinueStarter; if (st == null) return null;
        foreach (var tx in st.GetComponentsInChildren<TMPro.TMP_Text>(true))
        {
            var t = tx.text; if (string.IsNullOrEmpty(t)) continue;
            txt = t;
            bool han = false;
            foreach (var c in t)
            {
                if (c >= 0xAC00 && c <= 0xD7AF || c >= 0x1100 && c <= 0x11FF) return Lang.Ko;
                if (c >= 0x3040 && c <= 0x30FF) return Lang.Ja;
                if (c >= 0x4E00 && c <= 0x9FFF) han = true;
            }
            if (han) return Lang.Zh;
            if (HasCyr(t)) return t.IndexOf("овж", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOfAny(new[] { 'і', 'ї', 'є', 'ґ' }) >= 0 ? Lang.Uk : Lang.Ru;
            var l = t.ToLowerInvariant();
            if (l.Contains("continuer")) return Lang.Fr;
            if (l.Contains("weiter") || l.Contains("fortsetzen")) return Lang.De;
            if (l.Contains("continua") && !l.Contains("continuar")) return Lang.It;
            if (l.Contains("continuar")) return Lang.Es;
            return Lang.En;
        }
        return null;
    }

    static bool HasCyr(string t) { foreach (var c in t) if (c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё' || c == 'і' || c == 'ї' || c == 'є' || c == 'ґ' || c == 'І' || c == 'Ї' || c == 'Є' || c == 'Ґ') return true; return false; }

    // ---------- шрифт для иероглифов в нашем окошке (IMGUI) ----------
    static Lang? _fontFor; static Font _cjkFont;
    public static void ApplyGuiFont()
    {
        try
        {
            if (!Cjk)
            {
                if (_fontFor != null) { _fontFor = null; GUI.skin.font = null; }
                return;
            }
            if (_fontFor == Cur) return;
            string[] names = Cur == Lang.Zh ? new[] { "Microsoft YaHei", "SimHei", "Arial Unicode MS" }
                : Cur == Lang.Ja ? new[] { "Yu Gothic", "Meiryo", "MS Gothic", "Arial Unicode MS" }
                : new[] { "Malgun Gothic", "Gulim", "Arial Unicode MS" };
            Font f = null;
            foreach (var nm in names)
            {
                try { f = Font.CreateDynamicFontFromOSFont(nm, 16); } catch { f = null; }
                if (f != null) { Plugin.L.LogInfo($"LOC: шрифт для {Cur} — {nm}"); break; }
            }
            _cjkFont = f;
            if (f != null) GUI.skin.font = f;
            _fontFor = Cur;
        }
        catch (Exception e) { _fontFor = Cur; Plugin.L.LogWarning("LOC: шрифт: " + e.Message); }
    }

    // ---------- перевод ----------
    // плашка/сообщение: русский или английский — как написано в коде, остальные языки — из таблицы по английскому тексту
    public static string L(string ru, string en) => Cur == Lang.Ru ? ru : Cur == Lang.En ? en : Tr(en);

    static readonly Dictionary<string, string> _memo = new();
    static Dictionary<string, string[]> _exact;
    static List<(Regex re, string[] row, int n)> _pats;

    static void Build()
    {
        _exact = new Dictionary<string, string[]>(); _pats = new List<(Regex, string[], int)>();
        var tmp = new List<(Regex re, string[] row, int lit)>();
        foreach (var row in LocData.Rows)
        {
            var en = row[0]; _exact[en] = row;
            if (en.IndexOf('{') < 0) continue;
            var sb = new StringBuilder("^"); int lit = 0, pos = 0, cnt = 0;
            foreach (Match m in Regex.Matches(en, @"\{(\d)\}"))
            {
                var chunk = en.Substring(pos, m.Index - pos); lit += chunk.Length;
                sb.Append(Regex.Escape(chunk)).Append("(?<g").Append(m.Groups[1].Value).Append(">.+?)");
                pos = m.Index + m.Length; cnt++;
            }
            var tail = en.Substring(pos); lit += tail.Length;
            sb.Append(Regex.Escape(tail)).Append('$');
            tmp.Add((new Regex(sb.ToString(), RegexOptions.Singleline), row, lit));
        }
        tmp.Sort(CmpLit);
        foreach (var t in tmp) _pats.Add((t.re, t.row, 0));
    }

    static int CmpLit((Regex re, string[] row, int lit) a, (Regex re, string[] row, int lit) b) => b.lit.CompareTo(a.lit);

    public static string TrLines(string en)
    {
        var ls = en.Split('\n');
        for (int i = 0; i < ls.Length; i++) ls[i] = Tr(ls[i]);
        return string.Join("\n", ls);
    }

    static string Pick(string[] row)
    {
        int i = (int)Cur - 1;
        if (i < 1 || i >= row.Length || string.IsNullOrEmpty(row[i])) return row[0];
        return row[i];
    }

    public static string Tr(string en)
    {
        if (string.IsNullOrEmpty(en) || Cur == Lang.En || Cur == Lang.Ru) return en;
        if (_memo.TryGetValue(en, out var hit)) return hit;
        string res;
        try { res = TrCore(en); } catch (Exception e) { Plugin.L.LogWarning("LOC: перевод: " + e.Message); res = en; }
        if (_memo.Count > 1500) _memo.Clear();
        _memo[en] = res;
        return res;
    }

    static string TrCore(string en)
    {
        if (_exact == null) Build();
        if (_exact.TryGetValue(en, out var row)) return Pick(row);
        foreach (var (re, prow, _) in _pats)
        {
            var m = re.Match(en);
            if (!m.Success) continue;
            var t = Pick(prow);
            var outb = new StringBuilder(); int pos = 0;
            foreach (Match mm in Regex.Matches(t, @"\{(\d)\}"))
            {
                outb.Append(t, pos, mm.Index - pos);
                var g = m.Groups["g" + mm.Groups[1].Value];
                outb.Append(g.Success ? TrArg(g.Value) : mm.Value);
                pos = mm.Index + mm.Length;
            }
            outb.Append(t, pos, t.Length - pos);
            return outb.ToString();
        }
        if (en.IndexOf('\n') >= 0)
        {
            var ls = en.Split('\n');
            for (int i = 0; i < ls.Length; i++) if (ls[i].Length > 0) ls[i] = TrCore(ls[i]);
            return string.Join("\n", ls);
        }
        return en;
    }

    // подставляемое значение: название цвета/команды переводим, имена и названия аптек на кириллице — латиницей
    static string TrArg(string a)
    {
        if (_exact.TryGetValue(a, out var row)) return Pick(row);
        if (Cur != Lang.Uk && HasCyr(a)) return Translit(a);
        return a;
    }

    // русские слова на кнопках меню → перевод; оставшаяся кириллица (названия аптек) — латиницей, если шрифт без неё
    static readonly (string ru, string en)[] Words = {
        ("Общие аптеки", "Shared pharmacies"), ("Захожу в аптеку…", "Joining…"), ("ждёт", "is waiting"),
        ("+ Новая аптека из моего сейва", "+ New pharmacy from my save"), ("+ Новая аптека", "+ New pharmacy"),
        ("Зайти:", "Join:"), ("Зайти в аптеку", "Join"), ("Открыть:", "Open:"), ("Мой цвет:", "My color:"),
        ("Ещё: цвет, удаление аптек…", "More: color, delete…"), ("Удалить аптеку…", "Delete pharmacy…"), ("Точно удалить", "Really delete"), ("Да, удалить", "Yes, delete"),
        ("Удалить:", "Delete:"), ("Отмена", "Cancel"),
        ("Назад", "Back"), ("личный сейв", "personal save"), ("аптека", "pharmacy"), ("Аптека", "Pharmacy"),
    };

    public static string T(string s)
    {
        if (Cur == Lang.Ru || string.IsNullOrEmpty(s)) return s;
        foreach (var (ru, en) in Words) s = s.Replace(ru, Tr(en));
        return Cur == Lang.Uk ? s : Translit(s);
    }

    static readonly string[] Lat = { "a","b","v","g","d","e","zh","z","i","y","k","l","m","n","o","p","r","s","t","u","f","kh","ts","ch","sh","shch","","y","","e","yu","ya" };
    public static string Translit(string s)
    {
        if (!HasCyr(s)) return s;
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            char l = char.ToLowerInvariant(c);
            string r;
            if (l == 'ё') r = "yo";
            else if (l == 'і') r = "i"; else if (l == 'ї') r = "yi"; else if (l == 'є') r = "ye"; else if (l == 'ґ') r = "g";
            else if (l >= 'а' && l <= 'я') r = Lat[l - 'а'];
            else { sb.Append(c); continue; }
            if (char.IsUpper(c) && r.Length > 0) r = char.ToUpperInvariant(r[0]) + r.Substring(1);
            sb.Append(r);
        }
        return sb.ToString();
    }
}
