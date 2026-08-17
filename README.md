# Tidsprofiler – MIP-plugin för XProtect Smart Client

Låter behöriga användare läsa och redigera **befintliga** tidsprofiler direkt i Smart Client,
utan att öppna Management Client.

Byggt mot **MIP SDK 25.1**, vilket gör att det laddas i **XProtect 2025 R1 och senare**. Verifierat
i drift mot 2025 R2 Professional+ och 2026 R1.

---

## Vad pluginet gör

En egen flik, **Tidsprofiler**, i Smart Client:

| | |
|---|---|
| Vänsterkolumn | Alla tidsprofiler användaren får läsa, med sökfilter |
| Månadskalender | Riktiga datum, måndagsstart och veckonummer. Översikt över vad profilen täcker per dag, och den yta man väljer dagar på |
| Veckorutnät | En bestämd vecka med datum i huvudet, måndag–söndag × 24 timmar. Dra för att skapa en tid, dra i en tid för att flytta den, dra i kanten för att ändra längd |
| Sidopanel | Dagar, från/till-tid, giltighetsperiod och benämning för den valda tiden |
| Enstaka datum | Lista under kalendern för bokningar på specifika datum – helgdagar, avvikelser, heldagar |
| Spara / Avbryt | Spara skriver till servern, Avbryt läser om profilen |
| **?** och **i** | Hjälp om hur pluginet används, respektive vad paketet är – namn, version, utvecklare, språk och licens |

Tider snäppar till 15 minuter. Ett pass som `22:00–06:00` hanteras som ett nattpass och ritas
över midnatt i nästa dagkolumn.

**Giltighetsperiod.** Varje veckotid har ett startdatum och antingen ett slutdatum eller "tills
vidare". Så uttrycks t.ex. "vardagar 08–17, gäller 1 juni–31 augusti".

**Enstaka datum.** Bokningar på ett bestämt datum, med tider eller som heldag. De listas separat
under kalendern, redigeras i samma sidopanel, och ritas i veckorutnätet på sitt datum.

### Veckorutnätet visar en bestämd vecka

Kolumnerna är riktiga datum, inte bara veckodagsnamn: veckonumret står i hörnet och datumet under
varje dagnamn. Rutnätet följer kalendern – klicka en dag där och veckan den ligger i visas här.
Utan det har en bokning den 15 augusti ingen kolumn att hamna i, och syns bara i listan.

Att kolumnerna påstår datum gör två saker till nödvändiga, och de finns:

**Ett veckomönster utanför sin giltighetsperiod tonas ned** och ritas streckat i stället för som tid
som gäller. Det ligger kvar i profilen och går att välja och dra – man kan mycket väl vilja förlänga
det – men veckan som visas täcks inte av det.

**Ett pass över midnatt fortsätter till nästa datum**, inte runt till måndagen i samma vecka. Svällen
av ett söndagsnattpass lämnar alltså veckan till höger, och det som syns överst på måndagen kommer
från söndagen före.

Enstaka datum ritas i kalenderns orange. En heldagsbokning läggs *bakom* veckomönstren – den är en
egenskap hos dagen snarare än ett intervall i den, och ritad ovanpå skulle den dölja varje
veckomönster den dagen. Tidsatta bokningar ritas överst; de konkurrerar om samma rader som
veckomönstren, och en bokning gjord för en bestämd dag är det mer bestämda av de två påståendena.
Ett enstaka datum går att klicka på för att välja det, men inte att dra – det lagras som två
tidsstämplar och inte som starttid plus längd, så draget skulle flytta rutan på skärmen och lämna
bokningen där den var. Ändra den i sidopanelen.

### Månadskalendern

Veckorutnätet visar **mönstret**, månadskalendern visar **utfallet**. Båda behövs: en veckotid vars
giltighetsperiod tog slut förra månaden ser fullt frisk ut i veckorutnätet, och det är först på
riktiga datum man ser att den slutat gälla.

Varje dagruta har en liten remsa som är dygnet 00:00–24:00, med den täckta tiden ifylld – blått för
veckomönster, orange för enstaka datum. Att titta ned för en kolumn svarar alltså på "täcker den här
profilen lördagar" utan att man läser någonting. Hovra över en dag för exakta tider.

**Välja dagar**

| | |
|---|---|
| Klick | Väljer en dag |
| Dra | Väljer ett spann |
| Ctrl+klick | Lägger till eller tar bort en dag ur valet |
| Skift+klick | Utökar valet från den senast klickade dagen |
| Klick på veckonumret | Hela veckan |
| Klick på dagnamnet | Alla sådana dagar i månaden – alla lördagar, alla måndagar |
| Dubbelklick | Öppnar tiden som ligger på den dagen i sidopanelen |
| Esc | Rensar valet |

**Göra något med valet**

| Knapp | Vad den gör |
|---|---|
| Lägg till på valda datum | En bokning per vald dag, bara de dagarna |
| Lägg till som veckomönster | *En* återkommande tid på de veckodagar valet rör, giltig från första till sista valda datum. Så uttrycks "varje måndag den här terminen" som ett mönster i stället för sexton bokningar |
| Ta bort tid på valda datum | Tar bort enstaka bokningar på dagarna |

**Att ta bort en veckodag frågar först.** En tidsprofil är summan av sina bokningar och har inget
begrepp för undantag, så ett veckomönster går inte att stänga av för ett enstaka datum. Det enda
sättet att tömma en veckodag är att ta bort den ur mönstret, och då försvinner tiden varje vecka så
länge mönstret gäller. Det är inte vad "ta bort tid på de här datumen" låter som, så pluginet
frågar innan det gör det – och svarar nej om ingen är där att fråga.

**Vad kalendern inte ritar.** Mönster som pluginet ändå inte redigerar – dagliga, månatliga,
årliga, varannan vecka – placeras inte ut på datum, eftersom klienten aldrig läst fälten som säger
när de infaller. Att gissa vore värre än luckan: en kalender som tyst ritade ett månadsmönster på
fel dag skulle bli trodd. Finns sådana i profilen står det i klartext under kalendern, så en tom
dag betyder "inget som den här panelen kan rita" och inte "ingenting alls".

### Hjälp och information i gränssnittet

Två runda knappar uppe till höger, utanför allt som en profil, en behörighet eller en nåbar server
kan slå av.

**?** beskriver hur pluginet används: vad det kan och inte kan, skillnaden mellan ett veckomönster
och ett enstaka datum, vad färgerna betyder, att ingenting når servern förrän man sparar, och att
en låst Spara-knapp inte är hela skyddet. Texten ligger i `Model/HelpTopics.cs` som data, inte i
XAML, så den redigeras på ett ställe.

**i** visar namn, version, utvecklare, språk och licens. Licensraden står som "Ej angiven" tills
det finns en att ange – tom rad hade lästs som ett fel.

Uppgifterna läses ur assemblyn i stället för att skrivas ned en gång till (`Model/PluginInfo.cs`).
Samma namn, version och utvecklare visas på tre ställen samtidigt – den här panelen,
pluginlistan i Management Client och filegenskaperna i Utforskaren – och tre handskrivna kopior av
ett versionsnummer går isär. Det hade de redan gjort: pluginlistan påstod 1.0.0.0 om en fil som var
1.6.0. Harness kontrollerar numera att raderna inte är tomma och att de två stavningarna av
versionen hör ihop.

Panelerna ligger *ovanpå* redigeringsytan i stället för att ersätta den, så halvfärdiga ändringar
står kvar orörda när man stänger. Esc, krysset, knappen "Stäng" eller ett klick utanför stänger.

### Vad pluginet medvetet inte gör

Enligt kravet är detta en redigeringsfunktion, inte ett administrationsverktyg:

* skapar inte tidsprofiler
* tar inte bort tidsprofiler
* rör inga andra XProtect-inställningar

Dessutom lämnas följande orört – det visas men skrivs aldrig tillbaka:

* **Sunclock-profiler** styrs av soluppgång/solnedgång och har inga tidsintervall alls.
* **Andra återkommande mönster** än "varje vecka på dessa dagar" – dagliga, månatliga, årliga,
  varannan vecka, eller sådana som löper ett bestämt antal gånger.

Att rita ett sådant mönster i en veckovy och skriva tillbaka det som en vanlig veckoupprepning
skulle tyst ändra betydelsen, så pluginet visar dem i klartext (serverns egen beskrivning) och
låter dem vara.

---

## En utgåva, alla produktnivåer

Klienten byggs som **ett** paket, `TimeProfileEditor-<version>.msi`, och det är rätt på varje
XProtect-nivå. Serverkomponenten är ett eget paket, för att den ska till en enda maskin och inte
till varje operatörs PC – se **Installation och distribution**.

Här stod länge något annat, och felet är värt att skriva ned eftersom det kostade projektet en hel
arkitektur. Två olika saker blandades ihop:

* **Att ge en roll en delmängd av *administratörens* rättigheter** – konfigurationsskrivrätt på
  Management Server, vilket är vad en direktskrivning behöver. Det finns bara på Corporate, och
  licensen anger det som `DifferentiatedAdministratorSecurity`.

* **Ett MIP-plugins egen säkerhetsnamnrymd** – kryssrutorna en administratör sätter under
  **Roller → Tidsprofiler**. Det är en MIP-funktion, och den finns på Express+, Professional+,
  Expert och Corporate lika.

Bara det första saknas på Expert och Professional+. Slutsatsen "utan
`DifferentiatedAdministratorSecurity` kan pluginets behörigheter aldrig tilldelas någon" följde av
sammanblandningen, och den är fel.

**Uppmätt, inte resonerat.** På en XProtect Professional+ 2025 R2 svarar Management Server
`TimeProfileEditor.Edit = Granted` för en användare som inte är administratör – och `Forbidden` när
samma användare, på samma inloggning, försöker läsa rollistan via Configuration API. Behörigheten
finns alltså, och konfigurationsrätten saknas, precis som uppdelningen ovan säger.

Behörighetsmodellen är därför densamma överallt och behöver ingen utgåva: pluginet frågar vad
användaren tilldelats, och där servern nekar direktskrivningen utför Event Server-komponenten den i
stället. En binär, rätt på varje nivå, utan något att konfigurera och utan något sätt att installera
fel paket.

`build-installer.ps1 -Edition` finns kvar men betyder något annat nu. `Normal` är produkten.
`Measurement` är ett mätinstrument som inte kontrollerar någon behörighet alls inuti pluginet, så
att ett nej i en rapport bara kan ha kommit från Management Server; det byggs aldrig som standard,
skyltar med sig självt i arbetsytans namn, i bannern och i loggen, och ska aldrig driftsättas.

### Vad produktnivån faktiskt avgör

En enda sak: om en vanlig operatörs skrivning kan gå **direkt** till Management Server, eller måste
utföras av Event Server-komponenten.

| | Corporate | Expert / Professional+ |
|---|---|---|
| Pluginets egna rättigheter under Roller → Tidsprofiler | Ja | Ja |
| Roll kan ges konfigurationsskrivrätt | Ja | Nej |
| Operatörens sparning utförs av | klienten, som användaren | Event Server-komponenten |

Nivån läses aldrig av för att fatta det beslutet. Skrivningen försöks, och serverns svar avgör
vägen – en licensflagga är en gissning om det svaret, och där de två är oense har svaret rätt. Att
nivån ändå redovisas i diagnostiken är för att någon som undrar varför en skrivning tog omvägen ska
kunna se det.

## Behörighet

Behörigheten kontrolleras i **två oberoende lager**. Båda måste gå igenom.

### Lager 1 – vem gränssnittet erbjuds till

Pluginet publicerar två egna rättigheter, som administreras centralt i Management Client under
**Roller → \<roll\> → fliken Tidsprofiler**. Detta gäller på alla produktnivåer:

| Rättighet | Id | Effekt |
|---|---|---|
| Visa tidsprofiler | `TimeProfileEditor.View` | Fliken Tidsprofiler visas i Smart Client |
| Redigera tidsprofiler | `TimeProfileEditor.Edit` | Spara-knappen är aktiv |

Frågan ställs till Management Server och inte till något lokalt, så den går inte att slå på genom
att ändra en fil på klienten. Tre olika plattforms-API:er kan svara – de är inte lika tillgängliga
i varje värdprocess – och svaren vägs ihop enligt en regel: **ett auktoritativt ja räcker, och bara
en källa som svarar för den inloggade användaren får säga nej.** De andra två läser
administrationssidans vy av rättigheten, som inte är ifylld för en vanlig operatör. Deras "nej"
betyder "jag ser den inte", och att låta det dölja arbetsytan nekar exakt de användare funktionen
finns för.

**Ett obesvarat är inte ett nej.** Kan ingen källa svara – nätverksfel, en tjänst som inte hunnit
upp, eller att namnrymden ännu inte registrerats på servern – visas fliken i skrivskyddat läge med
orsaken utskriven, i stället för att försvinna. En flik som tyst inte finns lämnar den som ska
felsöka utan någonting att gå på, och det var precis så det första försöket misslyckades på Expert.

**Administratörsstatus och konfigurationsåtkomst avgör ingenting här.** Båda användes en gång som
ersättning för behörighetskontrollen, på tron att konfigurationsåtkomst och rätten att använda det
här pluginet var samma fråga på Expert och Professional+. Det är de inte, och att svara på den ena
med den andra nekade varenda operatör som pluginet finns för. De mäts fortfarande, men bara som
förklaring i diagnostiken till varför en skrivning routades.

### Lager 2 – serverns egen kontroll (den som faktiskt skyddar)

Varje läsning och skrivning går genom Configuration API **som den inloggade Smart
Client-användaren**. Management Server tillämpar användarens roll på varje anrop och avvisar det
som rollen inte får göra. Det gäller även om någon manipulerar klient-DLL:en – därför är detta,
och inte den gråa Spara-knappen, det som utgör skyddet.

Blir sparningen nekad av servern visar pluginet
*"Du saknar behörighet att ändra denna tidsprofil"* och slutar erbjuda Spara.

**Den routade vägen går inte förbi lagret, den flyttar det.** Utför Event Server-komponenten
skrivningen sker den med tjänstekontots rättigheter, och då kan Management Server inte längre vara
den som skiljer en behörig operatör från en obehörig. Komponenten gör det i stället, innan den rör
konfigurationen, i tre steg som ingenting tar sig förbi:

1. Är biljetten äkta? Frågan ställs till Management Server, inte till meddelandet.
2. Tillhör den den identitet som begäran påstår? Ett användarnamn i ett meddelande är ett påstående,
   inte ett bevis.
3. Har *den* identiteten `TimeProfileEditor.Edit`? Frågan ställs mot samma kryssrutor som en
   administratör sätter under Roller → Tidsprofiler.

Även läsningarna går genom den grinden. Det vore frestande att låta vem som helst lista profilerna
– arbetsytan syns ju ändå – men komponenten läser med administratörsrättigheter, så en ogrindad
läsning där lämnar ut konfiguration som Management Server medvetet undanhållit användaren.

### Viktigt att veta innan driftsättning

XProtect har **ingen inbyggd rollrättighet som enbart gäller tidsprofiler** – det finns ingen
inbyggd säkerhetsnamnrymd för dem, verifierat genom att lista serverns samtliga namnrymder. Den som
en *direktskrivning* mäts mot är Management Servers egen, och rätten att skriva där är bredare än
tidsprofiler. Pluginets rättigheter under Roller → Tidsprofiler ligger i dess *egen* namnrymd: de
styr det här gränssnittet, inte vad Management Server släpper igenom.

Det ger två sätt att låta en operatör ändra tidsprofiler:

| | Vad rollen behöver | Vad rollen då också kan | Var det fungerar |
|---|---|---|---|
| **Direktskrivning** | `Redigera tidsprofiler` **och** konfigurationsskrivrätt | Ändra annan konfiguration, via andra verktyg | Bara Corporate |
| **Via Event Server-komponenten** | `Redigera tidsprofiler` | Ingenting utöver tidsprofiler | Alla nivåer |

Den andra raden är den att välja när kravet är att användaren *inte* ska ha administrativa
rättigheter – vilket det är i det här projektet.

Ingenting behöver ställas in för att välja mellan dem. Klienten försöker alltid direkt först och
routar vidare först när servern nekar, så båda vägarna kan vara på plats samtidigt. På Corporate
behöver komponenten inte ens vara installerad.

---

## Vilka XProtect-versioner som stöds

**XProtect 2025 R1 och senare** – verifierat mot 2026 R1.

Ett MIP-plugin är framåtkompatibelt men inte bakåtkompatibelt: det laddas i den XProtect-version
det byggts mot och i alla senare, men aldrig i en tidigare. Ett plugin byggt mot 2026 R1 startar
alltså inte alls i en Smart Client 2025 R2 – ingen flik, inget felmeddelande i gränssnittet.

Därför byggs pluginet mot **MIP SDK 25.1**, inte mot det senaste. Golvet sitter där för att
`RolesTabName` – fliken i Management Client som publicerar pluginets behörigheter, och som hela
behörighetsmodellen vilar på – inte finns i 24.2.

Versionen står på ett ställe, [`Directory.Build.props`](Directory.Build.props). Höj den bara när
ett API som verkligen saknas i den äldre SDK:n behövs, och först efter ett beslut att sluta stödja
de äldre VMS-versionerna. Nyare plattformsfinesser som bara är trevliga att ha hämtas i stället
reflektivt – se `PluginSecurity.TryDetectNamespace`, som listar serverns säkerhetsnamnrymder där
plattformen kan det och hoppar över det där den inte kan.

Vilken SDK ett installerat plugin byggdes mot står i DLL:ens metadata och skrivs ut av
diagnostiken, så frågan går att besvara från en maskin i drift.

## Installation och distribution

MIP letar efter plugins i `C:\Program Files\Milestone\MIPPlugins\`. Varje undermapp med en
`plugin.def` läses in av alla MIP-värdar på maskinen.

### Installationspaket (rekommenderat)

```powershell
.\build\build-installer.ps1
```

Bygger `dist\TimeProfileEditor-<version>.msi` – per-maskin x64, samma paket för alla
produktnivåer. Kräver WiX 5 (`dotnet tool install --global wix`).

Serverkomponenten byggs för sig:

```powershell
.\build\build-server-installer.ps1
```

Två skript i stället för ett, med avsikt: paketen går till olika maskiner – det ena till en enda
server, det andra till varje arbetsstation – och uppdateras vid olika tillfällen. Ett skript som
producerar båda inbjuder till att rulla ut fel av dem, och en administrativ komponent som hamnar på
en operatörs PC är just det misstag som är värt att göra svårt.

Samma körning lägger diagnostikverktyget i `dist\Diagnostik\`. Den mappen behöver inte installeras
– den kopieras dit den behövs och körs på plats.

Installera genom att dubbelklicka på filen, eller tyst:

```bash
msiexec /i "dist\TimeProfileEditor-1.7.0.msi" /qn
```

Avinstallera med `msiexec /x` och samma fil, eller via Program och funktioner.

MSI:n sätter `UpgradeCode` fast, så en nyare version ersätter en installerad i stället för att
lägga sig bredvid. Versionsnumret hämtas från assemblyn, så `<Version>` i csproj-filen är enda
stället ett releasenummer behöver ändras. Vid avinstallation tas bara mappen
`TimeProfileEditor` bort – `Milestone\MIPPlugins` delas med Milestones egna installationer och
lämnas orörd.

**Stäng Smart Client och Management Client före installationen.** Är de igång ligger DLL:en låst,
och Windows Installer begär omstart i stället för att byta filen direkt.

### Kopiera filerna utan MSI

För en snabb utvecklingsinstallation:

```powershell
.\build\deploy.ps1
```

Kör den i en **PowerShell startad som administratör** – `C:\Program Files` är skrivskyddat annars.

Resultatet är avsiktligt minimalt:

```
C:\Program Files\Milestone\MIPPlugins\TimeProfileEditor\
    TimeProfileEditor.dll
    plugin.def
```

Inga `VideoOS.*.dll` följer med. Smart Client laddar redan sina egna – en andra uppsättning i
plugin-mappen gör att MIP läser in plattformen två gånger och pluginet slutar fungera.
`deploy.ps1` avbryter om bygget råkat producera sådana.

### Ordning vid första installationen

Samma ordning på alla produktnivåer:

1. Installera klientpaketet på maskinen med **Management Client**.
2. Starta Management Client en gång. Det är den som registrerar pluginets säkerhetsnamnrymd på
   servern – fliken Tidsprofiler under Roller finns inte förrän det skett.
3. Roller → välj roll → fliken **Tidsprofiler** → kryssa i rättigheterna.
4. Installera klientpaketet på maskinerna med **Smart Client** och starta om dem.
5. **På Expert och Professional+**, eller varhelst operatörer inte har konfigurationsskrivrätt:
   installera serverkomponenten på maskinen som kör **Event Server**. Utan den kan de användarna
   se och redigera men inte spara – deras skrivning har ingen väg fram.

Hoppas steg 1–2 över kan behörigheten aldrig tilldelas någon. Pluginet upptäcker det och skriver ut
varför, i stället för att bara vara tyst avstängt.

### Central utrullning

XProtect distribuerar **inte** Smart Client-plugins automatiskt till anslutande klienter – det
finns ingen inbyggd mekanism för det. MSI:n är därför det som ska ut till varje klientmaskin:

* **GPO** – lägg MSI:n på en share och publicera den under Datorkonfiguration →
  Programvaruinstallation. Per-maskin-paketet installeras vid uppstart utan användarinteraktion.
* **Intune** – ladda upp som Line-of-business-app (.msi), tilldela till enhetsgruppen.
* **SCCM / Configuration Manager** – standardapplikation med `msiexec /i ... /qn`.
* **Med i klientimagen**, om Smart Client rullas ut som en förinstallerad avbild.

Vid uppdatering: bygg en ny MSI med högre versionsnummer och distribuera om – `UpgradeCode`
är oförändrad, så den ersätter den installerade. Behåll
`PluginIds.PluginDefinition` oförändrat mellan versioner – det är samma GUID som identifierar
säkerhetsnamnrymden, så ett byte skulle tyst nollställa alla tilldelade rättigheter.

---

## Loggning

Två spår, med olika syften:

* **XProtect Audit-logg** (Server Logs → audit) – skrivs av Management Server. Eftersom
  pluginet skriver som den inloggade användaren hamnar ändringen där under användarens eget
  namn. Det här är spåret som inte går att ändra från en klient och som ska användas för
  granskning. Kräver att revisionsloggning är påslagen på servern.
* **MIP-klientloggen** – pluginet skriver en läsbar rad per sparning med användare, tidpunkt,
  profil och exakt vilka intervall som lades till, ändrades eller togs bort. Finns under
  `C:\ProgramData\Milestone\XProtect Smart Client\Logs`.

---

## Felmeddelanden

| Läge | Meddelande |
|---|---|
| Sparat | "Ändringarna har sparats. (n ändring(ar))" |
| Inget ändrat | "Inget att spara – schemat är oförändrat." |
| Servern nekar | "Du saknar behörighet att ändra denna tidsprofil." |
| Rättigheter ej registrerade | Förklarar att Management Client behöver startas en gång |
| Någon annan hann före | "Tidsprofilen har ändrats av någon annan sedan du öppnade den." |
| Profilen är borttagen | "Tidsprofilen finns inte längre. Den kan ha tagits bort av någon annan." – sägs bara av den som läser med administratörsrättigheter, se `SaveStatus.NotVisible` |
| Övrigt fel | "Det gick inte att spara ändringarna: \<serverns text\>" |

Konfigurationsändringar sker utan transaktion. Pluginet skriver därför bara det som faktiskt
skiljer sig – rör användaren inte ett intervall genereras inget serveranrop för det. Skulle en
sparning ändå avbrytas halvvägs säger meddelandet det rakt ut och profilen läses om, i stället
för att låtsas att allt gick bra.

---

## Serverbeteenden som styrt designen

Sex saker upptäcktes genom att köra mot en riktig Management Server. De är inte dokumenterade
någonstans uppenbart, de ger inga felmeddelanden, och de förklarar varför koden ser ut som den gör.

**Fält som inte används valideras ändå – och olika servrar validerar olika.** En veckotid begränsas
antingen inte alls eller av ett slutdatum, så `RecurrenceRangeMaxOccurrences` spelar ingen roll för
någonting pluginet skriver. Den kontrolleras likväl på vägen in, och en Management Server 25.2 på
Professional+ avvisar `0` med *"The RangeMaxOccurrences property cannot be set to a value outside the
range of 1 - 999"* medan labbservern som skrivtesterna körs mot tar emot det utan invändning. Samma
sak gäller redan `RecurrenceRangeEndDate`, `RecurrencePatternDayOfMonth` och
`RecurrencePatternMonthOfYear`. Alla fyra får därför ett giltigt platshållarvärde oavsett vem som
svarar. Slutsatsen är inte "sätt 999" utan att ett obrukat fält måste vara giltigt ändå, och att
skrivtesterna på en enda server inte bevisar att en skrivning går igenom på nästa.

**En läsning som servern inte tillåter nekas inte – den kommer tillbaka tom.** Configuration API:t
svarar med de objekt anroparen får se, inte med ett fel. För en operatör på Expert eller
Professional+ är det inga objekt alls, och en tidsprofil som finns och fungerar ser då exakt ut som
en som är borttagen. Klienten kan därför aldrig av egen kraft avgöra vilket det är: den vet bara
att den inte såg den. Det är vad `SaveStatus.NotVisible` betyder, och därför går en sådan sparning
vidare till Event Server-komponenten – som läser med administratörsrättigheter och *kan* avgöra
saken – i stället för att rapporteras som ett fel. Kostade en felsökning: pluginet sa
"Tidsprofilen finns inte längre" om en profil det just hade läst in och visat.

**`AppointmentRootId` är inget id.** Servern delar ut ett nytt för samma oförändrade tidsintervall
vid varje läsning. Ett id som sparats undan mellan "profilen lästes in" och "användaren tryckte
Spara" matchar därför ingenting. Pluginet har egna klientnycklar för det, och serverns id används
bara inom den läsning som producerade det.

**En borttagning som inte hittar sitt mål svarar `Success`.** Kombinerat med ovanstående blir följden
att en sparning kan rapportera att allt gick bra medan ingenting skrevs. Pluginet läser därför om
profilen efter varje sparning och jämför mot det som begärdes, i stället för att lita på svarskoderna.

**Längder tolkas med dygnssemantik.** `"24:00:00"` läses av servern som *24 dygn* – den beskriver
själv en sådan post som "from 00:00 for 24 days". Ett dygnslångt pass skrivs därför som 00:00–23:59,
och `TimeProfileRepository.MaxDuration` är den enda plats som avgör det.

**Enstaka datum tas bort med sin starttid, inte med det handtag servern erbjuder.**
`RemoveAppointment()` returnerar en lista där varje bokning har handtaget `<ticks>-<ordningsnummer>`.
Ordningsnumret räknas bara bland de enstaka bokningarna, men servern löser upp det mot profilens
*alla* bokningar – så fort profilen även innehåller en veckotid avvisas handtaget servern nyss
lämnade ut, med "Invalid selection". Enbart ticks-delen accepteras alltid. Urvalet måste dessutom
sättas på samma task-objekt och köras med `Execute()`, och ingenting får läsas från profilen
däremellan – en läsning är i sig ett serveranrop som ogiltigförklarar det pågående urvalet.

Alla sex täcks av testverktyget, så en framtida SDK-uppgradering som ändrar beteendet syns direkt.
Med reservationen ovan: skrivtesterna mäter den server de körs mot, inte alla servrar.

## Projektstruktur

```
plugin.def                          MIP-manifest (laddas i SmartClient + Administration)
Directory.Build.props               MIP SDK-version = lägsta XProtect som stöds
build/build-installer.ps1           Bygger klientens MSI + dist/Diagnostik
build/build-server-installer.ps1    Bygger serverkomponentens MSI (egen maskin, eget skript)
build/deploy.ps1                    Kopierar filerna direkt (utveckling)
installer/Package.wxs               MSI-definition (UpgradeCode ändras aldrig)
src/TimeProfileEditor/
    TimeProfileEditorPluginDefinition.cs   MIP-ingång, rollrättigheter
    PluginIds.cs                           GUID:er och rättighets-id (ändras aldrig)
    Client/                                Workspace-flik och view item
    Security/PluginSecurity.cs             Serverbackad behörighetskontroll (lager 1)
    Security/SystemEdition.cs              Produktnivå och administratörsstatus - diagnostik, avgör inget
    Model/                                 Tidsintervall och dagmask
    Model/DayCoverage.cs                   Räknar ut vad profilen täcker på ett riktigt datum
    Services/TimeProfileRepository.cs      Configuration API, diff, konfliktkontroll
    Services/RoutedTimeProfileRepository.cs  Väljer direkt väg eller via Event Server
    Services/ServerComponentChannel.cs     Klientens ände av meddelandekanalen
    Protocol/ServerProtocol.cs             Vad de två halvorna säger till varandra (delad källa)
    Services/ChangeLog.cs                  Ändringsloggning
    ViewModels/                            Tillstånd, kommandon, dirty-hantering
    Views/WeekScheduleControl.cs           Veckorutnätet - en bestämd vecka med datum
    Views/MonthCalendarControl.cs          Månadskalendern (utfallet, och val av datum)
    Views/                                 XAML och konverterare
src/TimeProfileEditor.Server/       Event Server-komponenten
    Background/TimeProfileServerPlugin.cs  Tar emot begäran, grindar den, utför skrivningen
    Security/TokenValidator.cs             Är biljetten äkta, och vems är den
    Security/PermissionOracle.cs           Vad får den identiteten göra
tests/TimeProfileEditor.Harness/    Kör repositoryt mot en riktig server
```

## Om fliken inte syns, eller behörigheten inte kan kontrolleras

Står det något i banderollen finns knappen **Kopiera diagnostik** under den. Den lägger hela bilden
på urklipp – versioner, inloggad användare, rollista, vilket läge som valts och varför, vad
Configuration API lämnar ut, och vad behörighetskontrollen svarar. Samma text hamnar i MIP-loggen.
Det är det snabbaste sättet att få ut ett svar från en maskin man inte når själv, och kräver inget
installerat verktyg.

Samma rapport från kommandoraden: `TimeProfileEditor.Harness.exe --report`.

Behövs mer än så, börja med **`dist\Diagnostik\Kör diagnostik.cmd`**. Mappen är xcopy-bar – kopiera den till maskinen
som har problemet och kör den där, inloggad som användaren det gäller. Den kräver ingen
utvecklingsmiljö, ändrar ingenting, och sparar rapporten som `diagnostik.txt` bredvid sig.

Ligger Management Server på en annan maskin:

```bash
"Kör diagnostik.cmd" --server http://minserver
```

Rapporten visar vilken användare och SID som gäller, vilken MIP-version pluginet byggts mot och
vilken plattform som körs, produktnivå och valt behörighetsläge, administratörsstatus och var det
avgjordes, om servern släpper igenom en konfigurationsläsning, om pluginets säkerhetsnamnrymd finns
på servern och vad var och en av behörighetskontrollerna svarar.

Från en utvecklingsmaskin går samma sak att köra direkt:

```bash
dotnet run --project tests\TimeProfileEditor.Harness -- --diag
```

**Syns ingen flik alls** är det oftast en av två saker:

- *Pluginet laddades aldrig.* Byggt mot en nyare MIP SDK än XProtect-versionen på maskinen – se
  **Vilka XProtect-versioner som stöds**. Smart Clients MIP-logg,
  `C:\ProgramData\Milestone\MIPSDK\MIP<ååååmmdd>.log`, visar då ett laddningsfel för DLL:en.
- *Servern nekade `Visa tidsprofiler`.* Samma logg innehåller då raden "Arbetsytan Tidsprofiler
  visas inte" med vilken kontroll som avgjorde det.

Innehåller loggen ingetdera har pluginet inte hittats – kontrollera att både `TimeProfileEditor.dll`
och `plugin.def` ligger i `C:\Program Files\Milestone\MIPPlugins\TimeProfileEditor\`.

**Listan är tom trots att det finns tidsprofiler** betyder att kontot saknar konfigurationsrätt
*och* att serverkomponenten inte svarade. Det första ensamt är normalt: XProtect svarar en sådan
anropare genom att lämna ut de poster hen får se – alltså inga – i stället för att neka, så
"lyckades, returnerade ingenting" och "får inte se någonting" är samma svar på tråden när det
gäller tidsprofiler. Just därför drar klienten aldrig någon slutsats av en tom lista, utan frågar
komponenten. Är listan ändå tom är det komponenten som fattas eller tiger – leta i Event
Server-loggen efter raden `Serverkomponenten är igång`.

Roller beter sig däremot inte så. Samma konto som får en tom tidsprofillista får ett rent
`NotAuthorizedMIPException` (`VMO61008`) när det försöker läsa rollerna. Det är därför **rollistan**
och inte tidsprofilerna som diagnostiken använder för att mäta konfigurationsåtkomst – den frågan
har ett svar det går att lita på. Den mätningen förklarar varför en skrivning routades; den avgör
ingenting, se **Lager 1**.

Diagnostiken läser båda, plus inspelningsservrar, kameragrupper och användardefinierade händelser.
Den visar också vilket `ServerId` som används och vilken Management Server anropet faktiskt landade
på, vilket fångar fallet att klienten pratar med fel server.

Saknas namnrymden har Management Client inte laddat pluginet ännu – se installationsordningen ovan.
Finns den men rättigheterna är `False` är det rollen som saknar dem.

På Corporate avgörs behörigheten av tre olika plattforms-API:er som prövas i tur och ordning, eftersom de inte
är lika tillgängliga i alla värdprocesser: ett REST-anrop fungerar i en fristående SDK-process men
inte nödvändigtvis inuti Smart Client, som redan äger en initierad säkerhetsstack. Ett jakande svar
från någon av dem räcker – en kontroll som inte *kan* se rättigheten får inte lägga in veto mot en
som kan. Knappen **Uppdatera listan** kör om hela kontrollen, så en check som misslyckades för att
klienten fortfarande loggade in kan göras om utan att starta om Smart Client.

## Testverktyg

```powershell
dotnet run --project tests\TimeProfileEditor.Harness -- --server http://localhost --write
```

Slaskprofilen lämnas kvar efter körningen så att ett misslyckat testfall går att inspektera.
`--cleanup` tar bort den.

Läsdelen är ofarlig. `--write` skapar en slaskprofil `TEST - Harness` och skriver i den –
**kör den bara mot en labbmiljö.** Den kontrollerar bland annat att dagmasken tolkas rätt
(söndag = 1 … lördag = 64, verifierat mot serverns egna beskrivningar), att ändringar sparas och
läses tillbaka oförändrade, att ett nattpass över midnatt överlever tur och retur, att en
oförändrad sparning inte rör servern, att en föråldrad tidsstämpel ger konflikt och att den
nekade behörigheten faktiskt stoppar skrivningen.
