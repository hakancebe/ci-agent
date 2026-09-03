using System.Globalization;

namespace CiAgent.Core;

/// <summary>
/// Türkçe mesajlardaki sayıları biçimlendirir: 62063 → "62.063".
///
/// Neden gerekli? Agent'ın kullanıcıya gösterdiği bütün metinler Türkçe, ama
/// C#'ın `{sayı:N0}` ifadesi ÇALIŞTIĞI MAKİNENİN kültürünü kullanıyor. Sonuç,
/// aynı kodun ortama göre farklı çıktı vermesiydi:
///
///   geliştirici makinesi (tr) : "Prompt 62.063 karakter limitine sığmadı"
///   Linux container (en/inv)  : "Prompt 62,063 karakter limitine sığmadı"
///
/// İkincisi Türkçe bir cümlenin içinde yanlış okunuyor ve ayrıca testleri
/// ortama bağımlı kılıyordu — nitekim CI'da (en-US runner) iki test düştü,
/// geliştirici makinesinde geçtikleri hâlde.
///
/// Kültür adıyla (`new CultureInfo("tr-TR")`) değil, biçimin kendisini kurarak
/// çözüyoruz: böylece ICU verisi olmayan ya da InvariantGlobalization ile
/// derlenmiş bir ortamda da aynı sonucu veriyor — bağımlılık yok, davranış sabit.
/// </summary>
internal static class TurkishNumber
{
    private static readonly NumberFormatInfo Format = new()
    {
        NumberGroupSeparator = ".",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = [3]
    };

    /// <summary>Binlik ayraçlı, ondalıksız: 62063 → "62.063".</summary>
    public static string Group(long value) => value.ToString("N0", Format);
}
