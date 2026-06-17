namespace Starling.Gui.Theme;

/// <summary>
/// Active theme, density, and type selection plus resolved token sets. Avalonia
/// controls subscribe to <see cref="Changed"/> and rebuild or re-bind their
/// visuals when it fires.
/// </summary>
public sealed class ThemeManager
{
    public const double UiScale = 1.3;

    public ThemeMode Theme { get; private set; } = ThemeMode.Dark;
    public DensityMode Density { get; private set; } = DensityMode.Comfy;
    public TypeMode Type { get; private set; } = TypeMode.Sans;

    public ThemeTokens Tokens { get; private set; } = ThemeTokens.Dark;
    public DensityTokens Metrics { get; private set; } = DensityTokens.Comfy.Scaled(UiScale);

    public string ChromeFont => Type == TypeMode.Mono ? Fonts.Mono : Fonts.Sans;
    public string MonoFont => Fonts.Mono;
    public string SansFont => Fonts.Sans;

    public event EventHandler? Changed;

    public void SetTheme(ThemeMode mode)
    {
        if (Theme == mode)
        {
            return;
        }

        Theme = mode;
        Tokens = ThemeTokens.For(mode);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetDensity(DensityMode mode)
    {
        if (Density == mode)
        {
            return;
        }

        Density = mode;
        Metrics = DensityTokens.For(mode).Scaled(UiScale);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetType(TypeMode mode)
    {
        if (Type == mode)
        {
            return;
        }

        Type = mode;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

public static class Fonts
{
    // Bundled via Starling.Gui.csproj as AvaloniaResource. The
    // FontFamily syntax is "avares://<assembly>/<path>#<family-name>".
    private const string Asm = "Starling.Gui";
    public const string Sans = $"avares://{Asm}/Assets/Fonts/Geist-Variable.ttf#Geist";
    public const string Mono = $"avares://{Asm}/Assets/Fonts/GeistMono-Variable.ttf#Geist Mono";
}
