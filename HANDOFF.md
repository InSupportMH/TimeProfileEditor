# HANDOFF – Tidsprofiler (MIP-plugin för XProtect)

Arbetsdokument för nästa session. Produkten i sig är dokumenterad i [README.md](README.md) –
det här är läget i arbetet, vad som visat sig fungera, vad som inte gjorde det, och vad som är kvar.

Senast uppdaterad: 2026-08-13. Klient **1.7.0**, serverkomponent **1.0.2**.

---

## Mål

Ett MIP-plugin som låter behöriga vanliga användare redigera **befintliga** tidsprofiler direkt i
Smart Client, utan Management Client.

Bindande krav från beställningen:

* Se, välja, redigera tider och schemaläggning; spara och avbryta.
* Användaren ska **inte** kunna skapa eller ta bort tidsprofiler, administrera användare eller
  behörigheter, eller ändra kamera-/XProtect-konfiguration.
* Behörigheter administreras centralt.
* **"Det är viktigt att behörighetskontrollen inte enbart bygger på att knappen döljs i
  gränssnittet. Även själva operationen att spara ändringar ska kontrolleras mot användarens
  behörighet."** – det är detta krav hela tvålagersmodellen finns för.
* Central installation med enkel uppdatering och versionshantering.
* Enkelt modernt gränssnitt med kalender/tidslinje.
* Svensk återkoppling: "Ändringarna har sparats." / "Du saknar behörighet att ändra denna
  tidsprofil." / "Det gick inte att spara ändringarna."
* Ändringar loggas med användare, datum/tid, profil och vad som ändrades.

### Två stående regler

Båda har uppstått ur konkreta misstag och gäller tills någon aktivt beslutar annat.

1. **Bearer-token skrivs aldrig ut i diagnostiken.** Bara längd, form, giltighetstid och
   *namnen* på claims – aldrig ett claim-värde.
2. **Harnessens destruktiva `--write` får aldrig ligga ett dubbelklick bort på en kundserver.**
   Bara läsande startpunkter får `.cmd`-filer i `dist\Diagnostik\`.

---

## Var arbetet står

32 uppgifter i tasklistan, **alla klara utom #25**.

| Del | Version | Status |
|---|---|---|
| Klient-plugin (`TimeProfileEditor.dll`) | 1.7.0 | Byggd och paketerad, `dist\TimeProfileEditor-1.7.0.msi` (388 KB) |
| Serverkomponent (`TimeProfileEditor.Server.dll`) | 1.0.2 | Paketerad `dist\TimeProfileEditor-EventServer-1.0.2.msi`. **Källan har en orepaketerad ändring**, se Nästa steg |
| Testharness | – | Senast kört: **74 godkända, 0 underkända** i läsläge |
| Bygge | – | 0 varningar |

`dist\` är städat: bara de två aktuella MSI:erna ligger i roten, allt överspelat ligger i
`dist\Old\`. `dist\Diagnostik\` är den xcopy-bara felsökningsmappen.

Senast avslutade uppgift var **#32 – hjälp- och informationspanelen** ("?" och "i" uppe till höger i
gränssnittet). Den är klar, byggd, paketerad och rapporterad till användaren.

---

## Vad som fungerade

**Tvålagersbehörighet där lager 2 är det som faktiskt skyddar.** Lager 1 avgör vem gränssnittet
erbjuds till; lager 2 är serverns egen kontroll när sparningen utförs. Det är det som uppfyller
kundens uttryckliga krav – att dölja knappen är inte skyddet, bara artigheten.

**En binär för alla produktnivåer.** Vägen för en skrivning avgörs av **serverns svar**, inte av
licensflaggan: skrivningen försöks direkt, och nekas den utför Event Server-komponenten den i
stället. Produktnivån redovisas i diagnostiken men avgör ingenting. En licensflagga är en gissning
om serverns svar, och där de är oense har svaret rätt.

**MIP SDK-golvet på ett enda ställe.** [`Directory.Build.props`](Directory.Build.props) = 25.1.3.
Plugins är framåt- men inte bakåtkompatibla, så att bygga mot den *äldsta* version vi tänker stödja
är vad som gör ett paket giltigt överallt. 25.1 är golvet för att `RolesTabName` inte finns i 24.2.
Nyare plattformsfinesser hämtas reflektivt i stället – se `PluginSecurity.TryDetectNamespace`.

**En källa till sanning för självbeskrivningen.** Namn, version och utvecklare visas på tre ställen
samtidigt (informationspanelen, pluginlistan i Management Client, Explorers filegenskaper). De läses
ur assemblyn i [`Model/PluginInfo.cs`](src/TimeProfileEditor/Model/PluginInfo.cs). Detta var inte
förebyggande: kopiorna hade redan glidit isär – `VersionString` sa `1.0.0.0` om en 1.6.0-fil.
`<Version>` i csproj:en är enda stället ett releasenummer ändras; MSI:n läser det ur den byggda
assemblyn.

**Harnessen som regressionsnät.** Alla sex serverbeteenden i README:s
*"Serverbeteenden som styrt designen"* täcks av tester, så en SDK-uppgradering som ändrar beteendet
syns direkt. Läsdelen är ofarlig och körs var som helst.

**Rendera den kompilerade vyn för att verifiera UI.** Ett litet WPF-exe i scratchpad konstruerar
`TimeProfileEditorView` och driver den genom de riktiga commands. Renderingen blir då bevis om
*kopplingen*, inte bara om ritandet.

**Kontrollera hjälptext mot koden som implementerar den.** Varje påstående i hjälpen lästes av mot
`WeekScheduleControl` innan det skrevs. Hjälp som är lite inaktuell är sämre än ingen hjälp, för den
blir trodd.

---

## Vad som inte fungerade

Gör inte om detta.

**Separata bygganden per produktnivå.** Kostade projektet en hel arkitektur. Två olika saker
blandades ihop: *konfigurationsskrivrätt* (finns bara på Corporate) och *ett MIP-plugins egen
säkerhetsnamnrymd* (finns på Express+, Professional+, Expert och Corporate lika). Slutsatsen "utan
`DifferentiatedAdministratorSecurity` kan behörigheten aldrig tilldelas" följde av
sammanblandningen och är fel – uppmätt: Professional+ 2025 R2 svarar `Granted` för en
icke-administratör. `-Edition` finns kvar men betyder något helt annat nu (`Normal` = produkten,
`Measurement` = mätinstrument som aldrig driftsätts).

**Att dra en slutsats av en tom lista.** Configuration API nekar inte en läsning anroparen saknar
rätt till – den kommer tillbaka **tom**. En tidsprofil som finns ser då exakt ut som en borttagen.
Pluginet sa "Tidsprofilen finns inte längre" om en profil det just visat. Klienten drar därför
aldrig någon slutsats av tomhet, utan frågar serverkomponenten.

**Att lita på svarskoderna.** En borttagning som inte hittar sitt mål svarar `Success`. En sparning
kunde alltså rapportera framgång utan att något skrevs. Profilen läses om efter varje sparning och
jämförs mot det som begärdes.

**`AppointmentRootId` som identitet.** Servern delar ut ett nytt id för samma oförändrade
tidsintervall vid varje läsning. Pluginet har egna klientnycklar.

**Att anta att oanvända fält inte behöver vara giltiga.** `RecurrenceRangeMaxOccurrences` m.fl.
valideras på vägen in även när de inte betyder något – och olika servrar validerar olika. Slutsatsen
är inte "sätt 999" utan att skrivtester mot *en* server inte bevisar något om nästa.

**`"24:00:00"` som ett dygn.** Servern läser det som 24 *dygn*. Ett dygnslångt pass skrivs 00:00–23:59.

**Handtaget som `RemoveAppointment()` lämnar ut.** Formen `<ticks>-<ordningsnummer>` avvisas så fort
profilen även innehåller en veckotid. Bara ticks-delen accepteras. Urvalet måste dessutom sättas på
samma task-objekt och köras med `Execute()` utan någon läsning emellan – en läsning ogiltigförklarar
det pågående urvalet.

**`VideoOS.*.dll` i plugin-mappen.** Smart Client laddar redan sina egna; en andra uppsättning gör
att MIP läser in plattformen två gånger och pluginet slutar binda. `deploy.ps1` avbryter om bygget
råkat producera sådana.

**Att rendera lös XAML för verifiering.** `XamlReader` vägrar interna typer. Renderaren måste
konstruera den kompilerade vyn i stället – och projektet måste heta `TimeProfileEditor.Harness` för
att `InternalsVisibleTo` ska gälla. (Renderaren har hittills legat i sessionens scratchpad och
överlever alltså inte till nästa session; den byggs om på tio minuter om den behövs igen.)

---

## Nästa steg

### 1. #25 – meddelandekanalen är inte uppe när komponenten startar

Den enda öppna uppgiften, och den enda kända riktiga risken.

Uppmätt på båda maskinerna: Event Serverns `CommunicationService` kastar *"Event server not found in
registered services"* vid varje start och blir operativ först cirka **10 sekunder** senare (samma
mönster vid sex omstarter). På SERVER-01 hann `TimeProfileServerPlugin.Init()` köra
`MessageCommunicationManager.Start/Get` **0,7 sekunder innan** `CommunicationService:URL` loggades.

Det gick igenom den gången, men **ingen meddelanderunda har provats** – vi vet bara att `Init` inte
kastade. Registreringen kan ha skett mot en kanal som ännu inte fanns.

Behövs: att komponenten tål det (försök igen tills kanalen svarar, eller registrera om) och att
klienten inte tolkar tystnad direkt efter serverstart som "komponenten saknas".

**Verifieras med en riktig Ping-runda från Smart Client omedelbart efter en omstart av Event Server.**

### 2. Serverkomponentens `<Company>` är ändrad i källan men inte ompaketerad

`src/TimeProfileEditor.Server/TimeProfileEditor.Server.csproj` har fått
`Nordic InSupport Nätverksvideo AB`, men komponenten är **inte** byggd om – paketet står kvar på
1.0.2. Det enda ändringen påverkar är filegenskaperna. Att tvinga fram en ominstallation på kundens
Event Server för en textsträng är fel avvägning; strängen rättar sig vid nästa serverbygge, som
ändå kommer när #25 åtgärdas.

### 3. Ouppklarat sedan tidigare

* **Avinstallation av serverkomponenten har aldrig provats.** `TimeProfileEditor-EventServer-1.0.0.msi`
  installerades men `msiexec /x` har aldrig körts – det är alltså inte verifierat att den städar rätt.
* **Strö-`TimeProfileEditor.Server.pdb`** ligger kvar i komponentmappen på SERVER-01.

---

## Bygga och verifiera

```powershell
.\build\build-installer.ps1
```

Bygger `dist\TimeProfileEditor-<version>.msi` samt `dist\Diagnostik\`. Kräver WiX 5
(`dotnet tool install --global wix`). Serverkomponenten byggs med sitt **eget** skript,
`.\build\build-server-installer.ps1` – två skript med avsikt, eftersom paketen går till olika
maskiner och en administrativ komponent på en operatörs PC är just det misstag som är värt att göra
svårt.

Läsande testkörning, ofarlig var som helst:

```powershell
dotnet run --project tests\TimeProfileEditor.Harness -- --server http://localhost
```

Övriga lägen: `--diag` (full diagnostikrapport), `--report` (samma rapport som knappen *Kopiera
diagnostik*), `--tokenprobe`, `--cleanup`. **`--write`** skapar och skriver i en slaskprofil
`TEST - Harness` – **bara mot labbmiljö**, och se stående regel 2.

Snabb utvecklingsinstallation, i PowerShell **som administratör**:

```powershell
.\build\deploy.ps1
```

---

## Att veta innan man rör koden

* **`PluginIds.PluginDefinition` får aldrig ändras.** Samma GUID identifierar säkerhetsnamnrymden –
  ett byte skulle tyst nollställa alla tilldelade rättigheter i alla roller.
* **`UpgradeCode` i `installer/Package.wxs` ändras aldrig.** Det är den som gör att en ny version
  ersätter en installerad i stället för att lägga sig bredvid.
* **Versionsnumret ändras bara i `<Version>` i csproj:en.** MSI:n och panelen läser det därifrån.
* **Ordningen vid första installationen spelar roll:** Management Client måste installeras och
  startas *en gång* innan fliken Tidsprofiler finns under Roller – det är den som registrerar
  namnrymden på servern.
