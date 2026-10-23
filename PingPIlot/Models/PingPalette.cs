using System.Windows.Media;
using Brush = System.Windows.Media.Brush;

namespace PingPilot.Models;

/// <summary>Пороги и цвета для оценки пинга: до 15 мс — зелёный, до 50 мс — жёлтый, выше — красный.</summary>
public static class PingPalette
{
    public const long GoodMaxMs = 15;
    public const long FairMaxMs = 50;

    public static readonly Brush Good = Make(0x22, 0xC5, 0x5E);
    public static readonly Brush Fair = Make(0xEA, 0xB3, 0x08);
    public static readonly Brush Poor = Make(0xEF, 0x44, 0x44);

    // Нет данных, устройство недоступно или проверка завершилась ошибкой.
    public static readonly Brush Neutral = Make(0x6B, 0x72, 0x80);

    public static Brush For(long? latencyMs) => latencyMs switch
    {
        null => Neutral,
        <= GoodMaxMs => Good,
        <= FairMaxMs => Fair,
        _ => Poor
    };

    public static Brush Make(byte r, byte g, byte b, byte a = 0xFF)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
