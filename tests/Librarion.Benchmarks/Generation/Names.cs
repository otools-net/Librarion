using System.Globalization;

namespace Librarion.Benchmarks.Generation;

/// <summary>
/// Names made from an index (mixed radix over word lists): unique for every index, the same on every run, Latin and
/// Cyrillic mixed as in real collections.
/// </summary>
public static class Names
{
    private static readonly string[] FirstNames =
    [
        "Anna", "Boris", "Clara", "David", "Elena", "Felix", "Greta", "Hugo", "Irene", "Jonas", "Karin", "Leon", "Maria",
        "Nikolai", "Olga", "Pavel", "Rosa", "Stefan", "Tamara", "Ulrich", "Vera", "Walter", "Xenia", "Yuri", "Zoe",
        "Alexei", "Bianca", "Carlos", "Dmitri", "Emma", "Fyodor", "Gloria", "Henrik", "Isabel", "Jakob", "Lena", "Marco",
        "Nadia", "Oscar", "Petra", "Quentin", "Rita", "Sergei", "Tanya", "Viktor", "Wanda", "Ivan", "Sofia", "Mikhail",
        "Lucia", "Анна", "Борис", "Вера", "Глеб", "Дарья", "Евгений", "Жанна", "Зоя", "Игорь", "Ксения", "Лев", "Мария",
        "Никита", "Ольга", "Пётр", "Раиса", "Сергей", "Татьяна", "Фёдор", "Юлия", "James", "Mary", "Robert", "Linda",
        "John", "Susan", "Thomas", "Karen", "Daniel", "Nancy", "Paul", "Betty", "Mark", "Sandra", "George", "Ashley",
        "Kenji", "Yuki", "Wei", "Mei", "Arjun", "Priya", "Omar", "Leila", "Tomas", "Ingrid", "Pierre", "Amelie", "Luca",
    ];

    private static readonly string[] Syllables =
    [
        "bar", "ber", "bor", "dan", "den", "dor", "fal", "fen", "gar", "gol", "han", "hel", "kar", "kel", "kov", "lan",
        "len", "lor", "man", "mer", "mor", "nar", "nel", "nov", "par", "pel", "ros", "ral", "ren", "sal", "sen", "sky",
        "tan", "ter", "tor", "val", "ven", "vik", "wal", "win", "zar", "zel", "ash", "ell", "ing", "ost", "uld", "ark",
        "berg", "chen", "dahl", "erg", "ford", "gren", "holm", "ich", "son", "ton", "wood", "ley",
    ];

    private static readonly string[] Words =
    [
        "night", "river", "shadow", "garden", "stone", "winter", "silver", "city", "storm", "heart", "fire", "glass",
        "empire", "secret", "island", "mirror", "road", "forest", "kingdom", "ocean", "dream", "crown", "ghost", "star",
        "house", "war", "sea", "moon", "blood", "time", "dragon", "letter", "bridge", "song", "light", "dark", "last",
        "first", "lost", "hidden", "golden", "broken", "black", "white", "red", "long", "quiet", "wild", "old", "new",
        "ночь", "река", "тень", "сад", "камень", "зима", "город", "буря", "сердце", "огонь", "море", "звезда", "дом",
        "война", "время", "песня", "свет", "дорога", "тайна", "остров", "лес", "мост", "письмо", "король", "сон",
    ];

    private static readonly string[] Genres =
    [
        "Fiction", "Fantasy", "Science fiction", "Horror", "Mystery", "Thriller", "Romance", "History", "Biography",
        "Poetry", "Drama", "Adventure", "Children", "Young adult", "Humor", "Philosophy", "Religion", "Science",
        "Travel", "Cooking", "Art", "Music", "Business", "Psychology", "Politics", "Economics", "Computers", "Health",
        "Sports", "Classics",
    ];

    private static readonly string[] Subgenres =
    [
        "Epic", "Urban", "Historical", "Contemporary", "Dark", "Cozy", "Military", "Space opera", "Cyberpunk", "Gothic",
        "Paranormal", "Detective", "Noir", "Legal", "Medical", "Political", "Spy", "Western", "Satire", "Essays",
    ];

    public static readonly string[] Languages = ["en", "ru", "de", "fr", "es", "it", "pl", "uk", "cs", "pt", "nl", "sv", "fi", "ja", "zh", "be", "bg", "hu", "tr", "he"];

    public static string Author(int index)
    {
        var first = FirstNames[index % FirstNames.Length];
        var rest = index / FirstNames.Length;
        var last = Capitalize(Syllables[rest % Syllables.Length] + Syllables[rest / Syllables.Length % Syllables.Length]);
        var more = rest / (Syllables.Length * Syllables.Length);
        if (more > 0)
            last += Syllables[(more - 1) % Syllables.Length] + (more > Syllables.Length ? more.ToString(CultureInfo.InvariantCulture) : "");
        return $"{first} {last}";
    }

    public static string Series(int index) =>
        $"The {Capitalize(Words[index % Words.Length])} {Capitalize(Words[index / Words.Length % Words.Length])} {Ordinal(index / (Words.Length * Words.Length))}".TrimEnd();

    public static string Publisher(int index)
    {
        var pairs = Syllables.Length * Syllables.Length;
        var name = Capitalize(Syllables[index % Syllables.Length] + Syllables[index / Syllables.Length % Syllables.Length]);
        var kind = (index / pairs % 3) switch { 0 => "Press", 1 => "Books", _ => "Publishing" };
        var round = index / (pairs * 3);
        return round == 0 ? $"{name} {kind}" : $"{name} {kind} {round.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary>Plain tags, messy on purpose: every tenth repeats a nearby one with other case and punctuation.</summary>
    public static string PlainTag(int index)
    {
        var baseIndex = index % 10 == 9 ? index - 9 : index;
        var name = $"{Words[baseIndex % Words.Length]} {Words[baseIndex / Words.Length % Words.Length]}"
                   + (baseIndex >= Words.Length * Words.Length ? " " + (baseIndex / (Words.Length * Words.Length)).ToString(CultureInfo.InvariantCulture) : "");
        return index % 10 == 9 ? Capitalize(name).Replace(" ", " - ", StringComparison.Ordinal) + " (general)" : name;
    }

    public static int CategoryCount => Genres.Length * (1 + Subgenres.Length);

    /// <summary>Categories: the genres, then each genre's subgenres (index ≥ genre count), whose parent is the genre.</summary>
    public static (string Name, int? Parent) Category(int index) =>
        index < Genres.Length
            ? (Genres[index], null)
            : ($"{Genres[(index - Genres.Length) / Subgenres.Length]}: {Subgenres[(index - Genres.Length) % Subgenres.Length]}", (index - Genres.Length) / Subgenres.Length);

    public static string Title(Random random)
    {
        var count = 1 + random.Next(5);
        var words = new string[count];
        for (var i = 0; i < count; i++)
            words[i] = Words[random.Next(Words.Length)];
        return Capitalize(string.Join(' ', words));
    }

    private static string Ordinal(int n) => n == 0 ? "" : n.ToString(CultureInfo.InvariantCulture);

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
