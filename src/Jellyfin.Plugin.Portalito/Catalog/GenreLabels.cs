namespace Jellyfin.Plugin.Portalito.Catalog;

/// <summary>
/// Spanish labels for <c>v3/filterGenre</c>'s <c>tags</c> vocabulary (English names, measured live 2026-09-23: 24
/// tags -- Action, Drama, Adventure, Crime, Sci-Fi, Cartoon, Comedy, Romance, Animation, Family, Fantasy, Thriller,
/// Horror, History, Mystery, Documentary, Reality-TV, Short, War, Western, Biography, Music, Sport, Game Shows).
/// An unknown tag (the portal's list can grow) falls back to its own English name rather than disappearing.
/// </summary>
public static class GenreLabels
{
    private static readonly IReadOnlyDictionary<string, string> Spanish = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Action"] = "Acción",
        ["Drama"] = "Drama",
        ["Adventure"] = "Aventura",
        ["Crime"] = "Crimen",
        ["Sci-Fi"] = "Ciencia ficción",
        ["Cartoon"] = "Dibujos animados",
        ["Comedy"] = "Comedia",
        ["Romance"] = "Romance",
        ["Animation"] = "Animación",
        ["Family"] = "Familiar",
        ["Fantasy"] = "Fantasía",
        ["Thriller"] = "Suspenso",
        ["Horror"] = "Terror",
        ["History"] = "Historia",
        ["Mystery"] = "Misterio",
        ["Documentary"] = "Documental",
        ["Reality-TV"] = "Telerrealidad",
        ["Short"] = "Cortometraje",
        ["War"] = "Bélico",
        ["Western"] = "Western",
        ["Biography"] = "Biografía",
        ["Music"] = "Música",
        ["Sport"] = "Deporte",
        ["Game Shows"] = "Concursos",
    };

    public static string ToSpanish(string englishTag) => Spanish.TryGetValue(englishTag, out var es) ? es : englishTag;
}
