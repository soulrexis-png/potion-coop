using System;
using System.Text;
using UnityEngine;

namespace PotionCoop;

// Язык надписей мода на кнопках меню. В английской версии игры у шрифта нет русских букв —
// надписи выходили пустыми. Язык узнаём по родной кнопке «Продолжить».
public static class Loc
{
    public static bool Ru = true;
    static bool _known;

    public static void Detect()
    {
        try
        {
            var st = Coop.ContinueStarter; if (st == null) return;
            foreach (var tx in st.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                var t = tx.text; if (string.IsNullOrEmpty(t)) continue;
                bool ru = HasCyr(t);
                if (!_known || ru != Ru) Plugin.L.LogInfo($"LOC: язык меню — {(ru ? "русский" : "не русский")} («{t}»)");
                Ru = ru; _known = true;
                return;
            }
        }
        catch (Exception e) { Plugin.L.LogWarning("LOC: " + e.Message); }
    }

    static bool HasCyr(string t) { foreach (var c in t) if (c >= 'А' && c <= 'я' || c == 'ё' || c == 'Ё') return true; return false; }

    static readonly (string ru, string en)[] Words = {
        ("Общие аптеки", "Shared pharmacies"), ("Захожу в аптеку…", "Joining…"), ("ждёт", "is waiting"),
        ("+ Новая аптека из моего сейва", "+ New pharmacy from my save"), ("+ Новая аптека", "+ New pharmacy"),
        ("Зайти:", "Join:"), ("Зайти в аптеку", "Join"), ("Открыть:", "Open:"), ("Мой цвет:", "My color:"),
        ("Ещё: цвет, удаление аптек…", "More: color, delete…"), ("Удалить аптеку…", "Delete pharmacy…"), ("Точно удалить", "Really delete"), ("Да, удалить", "Yes, delete"),
        ("Удалить:", "Delete:"), ("Отмена", "Cancel"),
        ("Назад", "Back"), ("личный сейв", "personal save"), ("аптека", "pharmacy"), ("Аптека", "Pharmacy"),
    };

    // плашка/сообщение: русский или английский — по языку игры
    public static string L(string ru, string en) => Ru ? ru : en;

    // перевести надпись для кнопки; оставшиеся русские буквы (названия аптек) — латиницей
    public static string T(string s)
    {
        if (Ru || string.IsNullOrEmpty(s)) return s;
        foreach (var (ru, en) in Words) s = s.Replace(ru, en);
        return Translit(s);
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
            else if (l >= 'а' && l <= 'я') r = Lat[l - 'а'];
            else { sb.Append(c); continue; }
            if (char.IsUpper(c) && r.Length > 0) r = char.ToUpperInvariant(r[0]) + r.Substring(1);
            sb.Append(r);
        }
        return sb.ToString();
    }
}
