using System.Windows;
using System.Windows.Media;

namespace PingPilot.Services;

/// <summary>
/// Общий масштаб интерфейса. Главное окно меняет его, а окна подробностей и диалоги
/// подписываются и масштабируются вместе с ним — раньше они всегда оставались в 100%.
/// </summary>
public static class UiScaleService
{
    public static double Scale { get; private set; } = 1.0;

    public static event Action<double>? ScaleChanged;

    public static void Set(double scale)
    {
        if (Math.Abs(scale - Scale) < 0.0001)
            return;

        Scale = scale;
        ScaleChanged?.Invoke(scale);
    }

    /// <summary>Применяет текущий масштаб к содержимому окна и следит за его изменениями.</summary>
    public static void Attach(Window window, FrameworkElement root)
    {
        var transform = new ScaleTransform(Scale, Scale);
        root.LayoutTransform = transform;

        void OnChanged(double value)
        {
            transform.ScaleX = value;
            transform.ScaleY = value;
        }

        ScaleChanged += OnChanged;
        window.Closed += (_, _) => ScaleChanged -= OnChanged;
    }
}
