using System.Collections.Generic;

namespace TimeProfileEditor.Model
{
    /// <summary>One heading and the paragraphs under it.</summary>
    internal sealed class HelpTopic
    {
        public HelpTopic(string title, string body)
        {
            Title = title;
            Body = body;
        }

        public string Title { get; }

        public string Body { get; }
    }

    /// <summary>
    /// The help text, written for the operator rather than for whoever installed this.
    ///
    /// Kept as data rather than as XAML so it is edited in one place and reads as prose while it
    /// is being written. It describes what the screen actually does today - every claim here was
    /// checked against the control that implements it, because help that is subtly out of date is
    /// worse than no help: it is believed.
    /// </summary>
    internal static class HelpText
    {
        public static IReadOnlyList<HelpTopic> All { get; } = new[]
        {
            new HelpTopic("Vad du kan göra här",
                "Du ändrar tiderna i tidsprofiler som redan finns. Att skapa nya profiler eller " +
                "ta bort dem görs i Management Client och går inte härifrån.\n\n" +
                "En tidsprofil styr när något i XProtect gäller – regler, patrullering, " +
                "inspelning. Ändrar du profilen ändras allt som använder den."),

            new HelpTopic("Välj en tidsprofil",
                "Listan till vänster visar de profiler du får se. Sökrutan filtrerar på namn och " +
                "\"Uppdatera listan\" hämtar om allt från servern.\n\n" +
                "Sunclock-profiler visas i listan men går inte att ändra här. De följer " +
                "soluppgång och solnedgång och ställs in i Management Client."),

            new HelpTopic("Kalendern och veckorutnätet",
                "De två panelerna svarar på olika frågor om samma profil.\n\n" +
                "Kalendern visar riktiga datum: vilka dagar profilen faktiskt täcker. " +
                "Veckorutnätet visar en bestämd vecka, måndag till söndag, dygnet runt: vilka " +
                "klockslag.\n\n" +
                "Klicka på en dag i kalendern så visar rutnätet den veckan. Dra över flera dagar " +
                "för att välja ett spann, klicka på ett veckonummer för hela veckan eller på ett " +
                "dagnamn för alla sådana dagar i månaden."),

            new HelpTopic("Lägg till en tid",
                "Det finns två sätt, och skillnaden mellan dem spelar roll.\n\n" +
                "Dra i en tom del av veckorutnätet, eller tryck \"Lägg till tid\", för att skapa " +
                "ett veckomönster: en tid som återkommer varje vecka på de valda veckodagarna, " +
                "så länge giltighetsperioden varar.\n\n" +
                "Välj datum i kalendern och tryck \"Lägg till på valda datum\" för enstaka " +
                "bokningar. De gäller bara de dagarna och rör ingen annan vecka. Kryssa " +
                "\"Heldag\" för att täcka hela dygnet."),

            new HelpTopic("Ändra eller ta bort en tid",
                "Klicka på ett block i rutnätet, eller på en rad under \"Enstaka datum\". " +
                "Panelen till höger visar dagar, klockslag och giltighetsperiod för det som är " +
                "valt.\n\n" +
                "Ett veckomönster flyttas genom att dras i rutnätet, och ändrar längd om du drar " +
                "i dess över- eller underkant. Enstaka datum ändras i panelen till höger i " +
                "stället – de sitter fast i rutnätet, eftersom en dragning där skulle betyda " +
                "något annat än en dag.\n\n" +
                "Ta bort med knappen längst ned i panelen, eller med Delete när rutnätet har " +
                "fokus.\n\n" +
                "Skriver du en sluttid som är tidigare än starttiden tolkas det som ett nattpass " +
                "som fortsätter in på nästa dygn, inte som ett skrivfel."),

            new HelpTopic("Giltighetsperiod",
                "Ett veckomönster gäller från ett datum, och antingen till och med ett annat " +
                "eller tills vidare.\n\n" +
                "En tid vars period har löpt ut ligger kvar i profilen men gäller inte längre. " +
                "Den ritas streckad och nedtonad i de veckor där den inte gäller, så att en tom " +
                "vecka inte ska se ut som ett borttaget mönster."),

            new HelpTopic("Färgerna",
                "Blått är veckomönster. Orange är enstaka datum. Streckat och nedtonat betyder " +
                "att tiden finns kvar men inte gäller den vecka som visas.\n\n" +
                "Står det \"Övriga tider i profilen\" längst ned rör det mönster som inte går " +
                "att rita rättvist här. De visas, lämnas orörda och följer med när du sparar."),

            new HelpTopic("Spara och avbryt",
                "Ingenting når servern förrän du trycker Spara. Avbryt återställer allt till det " +
                "som står på servern.\n\n" +
                "Byter du profil med osparade ändringar får du en fråga först."),

            new HelpTopic("Om något är låst",
                "Saknar du behörighet att ändra tidsprofiler är Spara låst, och en gul ruta " +
                "överst förklarar varför.\n\n" +
                "Att knappen är låst är inte hela skyddet. Servern prövar behörigheten en gång " +
                "till när ändringen sparas, så en ändring kan inte smygas förbi gränssnittet.\n\n" +
                "Knappen \"Kopiera diagnostik\" i den gula rutan lägger en teknisk sammanfattning " +
                "på urklipp. Klistra in den i ett supportärende."),

            new HelpTopic("Vad som loggas",
                "Varje sparad ändring loggas med vem som gjorde den, när den gjordes, vilken " +
                "tidsprofil det gällde och vad som ändrades.")
        };
    }
}
