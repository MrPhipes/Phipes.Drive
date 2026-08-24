using System.Globalization;

namespace Phipes.Drive.App.ViewModels;

/// <summary>A month of the timeline, e.g. "agosto de 2026", newest first.</summary>
public sealed class MonthGroup(DateTime month, IEnumerable<GalleryItemVm> items)
    : List<GalleryItemVm>(items)
{
    private static readonly CultureInfo Spanish = ResolveSpanish();

    public DateTime Month { get; } = month;

    public string Title { get; } = Capitalize(month.ToString("MMMM 'de' yyyy", Spanish));

    public string CountText => Count == 1 ? "1 elemento" : $"{Count} elementos";

    private static CultureInfo ResolveSpanish()
    {
        try
        {
            return CultureInfo.GetCultureInfo("es-CL");
        }
        catch (CultureNotFoundException)
        {
            return CultureInfo.CurrentCulture;
        }
    }

    private static string Capitalize(string text) =>
        text.Length == 0 ? text : char.ToUpper(text[0], Spanish) + text[1..];
}
