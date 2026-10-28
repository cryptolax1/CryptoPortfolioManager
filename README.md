# Crypto Portfolio Manager

Lokale crypto-portefeuille applicatie. Draait uitsluitend op je eigen Windows-pc: Blazor Web App (Interactive Server) + SQLite. Prijzen komen van de CoinGecko Demo API (zie verderop).

## Vereisten

- Windows
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (`dotnet --version` moet 10.x tonen)

## Bouwen, testen, starten

```powershell
dotnet tool restore        # eenmalig: installeert dotnet-ef (lokale tool)
dotnet build
dotnet test
dotnet run --project src\CryptoPortfolioManager.Web --launch-profile http
```

Open daarna <http://localhost:5017>.

De database wordt bij het opstarten automatisch aangemaakt/gemigreerd op
`%LOCALAPPDATA%\CryptoPortfolioManager\portfolio.db`. Een andere locatie kun je instellen via
`ConnectionStrings:Default` in `src\CryptoPortfolioManager.Web\appsettings.json`
(bijv. `Data Source=D:\\data\\portfolio.db`).

## Projectstructuur

| Pad | Doel |
|---|---|
| `src\CryptoPortfolioManager.Core` | Domein en businesslogica (entiteiten, services, repository-interfaces). Geen afhankelijkheid van EF of Blazor. |
| `src\CryptoPortfolioManager.Infrastructure` | EF Core + SQLite: `AppDbContext`, migraties, repository-implementatie. |
| `src\CryptoPortfolioManager.Web` | Blazor Web App (Interactive Server). Componenten roepen alleen services aan; DI-configuratie in `Program.cs`. |
| `tests\CryptoPortfolioManager.Tests` | xUnit-tests voor Core (met een fake repository, zonder database). |

Afhankelijkheden: `Web → Infrastructure → Core`, `Web → Core`, `Tests → Core/Infrastructure`.

## Datamodel

| Entiteit | Inhoud |
|---|---|
| `Asset` | Naam, symbool, CoinGecko-ID (uniek), optioneel contract (chain, adres, token-decimals). |
| `Position` | Hoort bij precies één asset. Wallet/exchange-naam, bron (`Manual`/`OnChainWallet`/`Exchange`), chain, walletadres, hoeveelheid, notities, `LastSyncedAtUtc`. |
| `PriceSnapshot` | Historische USD-prijs per asset, tijdstip (UTC) en bron. |
| `PriceUpdateLog` | Start/eind, status (`Running`/`Succeeded`/`PartiallySucceeded`/`Failed`) en foutmelding. |

Relaties: `Asset 1—n Position` (verwijderen geblokkeerd zolang er posities zijn), `Asset 1—n PriceSnapshot` (cascade).

Voorbereid op automatisch uitlezen van wallets: `Position.Chain` + `WalletAddress` (geïndexeerd, uniek per asset/chain/adres),
`SourceType` (een sync mag alleen niet-handmatige posities overschrijven), `LastSyncedAtUtc`, en `Asset.ContractAddress`/`TokenDecimals`
voor het omrekenen van ruwe token-saldi. Een wallet-sync hoeft dus geen schemawijziging.

Technische notities:
- Decimals zijn `decimal(38,18)`. SQLite slaat ze als TEXT op (exact), maar kan er niet in SQL op sorteren of sommeren; reken dat in het geheugen uit.
- Alle tijdstippen worden als UTC opgeslagen en teruggegeven.
- Enums worden als tekst opgeslagen; een nieuwe chain vereist dus geen migratie.
- Een asset is één token op één chain. Dezelfde stablecoin op meerdere chains heeft meerdere assets (eigen CoinGecko-ID).

### Echte database en testdatabase

De applicatie voegt zelf **nooit** testdata toe. Enige uitzondering: bij de eerste start (lege wallet- en whitelist-tabellen) worden de standaard wallets en token-whitelist aangemaakt (zie *Wallets scannen*); die bevatten geen saldo's of prijzen. Er zijn twee aparte SQLite-bestanden in `%LOCALAPPDATA%\CryptoPortfolioManager\`:

| Database | Bestand | Starten |
|---|---|---|
| Echt | `portfolio.db` | launch-profiel **Web (start app + open browser)** |
| Test | `portfolio.test.db` (vooraf gevuld) | launch-profiel **Web (test database)** |

In de testmodus (`Database__UseTestDatabase=true`) toont de pagina een gele **TEST DATABASE**-badge. Staat het testbestand er niet, dan stopt de app met een duidelijke melding.

De testdatabase wordt aangemaakt door een apart hulpprogramma (`tools\CryptoPortfolioManager.TestDb`) met 3 assets (BTC, ETH, USDC met echte CoinGecko-ID's) en 5 fictieve posities (wallets/exchanges) met per bron andere hoeveelheden, bijv. ETH: Ledger 3,44 en Bitvavo 2,41. Daarnaast bevat hij de standaard wallets en whitelist, zodat je **Re-scan wallets** tegen de testdatabase kunt proberen (de fictieve posities hebben geen wallet-koppeling en verdwijnen bij de eerste foutloze scan, zie hieronder; met `--reset` is alles weer schoon). Er staan bewust **geen prijzen** in, zodat alle prijzen en delta's van de echte API komen:

```powershell
dotnet run --project tools\CryptoPortfolioManager.TestDb                          # alleen aanmaken als hij nog niet bestaat
dotnet run --project tools\CryptoPortfolioManager.TestDb -- --reset               # opnieuw opbouwen (wist opgehaalde prijzen)
dotnet run --project tools\CryptoPortfolioManager.TestDb -- --reset --with-sample-prices   # incl. fictieve prijzen (bron "Sample")
```

Het launch-profiel maakt de testdatabase automatisch aan als hij ontbreekt; de VS Code-taak **reset test database** zet hem terug naar de beginsituatie. Het hulpprogramma weigert `portfolio.db` aan te raken.

**API → testdatabase testen:** start het profiel *Web (test database)* en klik op **Update prices**; de opgehaalde prijzen worden in `portfolio.test.db` opgeslagen. Zonder UI kan dit ook met de opt-in live test (gebruikt ca. 4 API-aanroepen en een tijdelijke database, de key komt uit User Secrets):

```powershell
$env:COINGECKO_LIVE_TESTS = "1"; dotnet test --filter "FullyQualifiedName~LiveCoinGecko"
```

Zonder `COINGECKO_LIVE_TESTS=1` wordt die test overgeslagen, dus `dotnet test` gebruikt normaal geen API-quota.

## Portfolio-overzicht

De startpagina (`/`) toont per asset één uitklapbare hoofdregel (naam, symbool, totale hoeveelheid, prijs, waarde en delta's YTD/3M/1M/1W/1D). Uitklappen toont de positions (wallet/exchange, chain, adres, hoeveelheid, waarde). Kolommen zijn sorteerbaar.

### CSV-export

De knop **Export CSV** exporteert alle assets naar `crypto-portfolio-<jjjj-mm-dd>_<UUmmss>.csv` (lokale datum en tijd, bijv. `crypto-portfolio-2026-10-14_233805.csv`). De logica staat in `PortfolioCsvBuilder` / `PortfolioExportService` (Core); de knop roept alleen die service aan.

- Eén rij per asset (geen positions of delta's). Een asset zonder positions blijft exporteerbaar met hoeveelheid `0`.
- Kolommen: Token name, Token symbol, Identifier, Total quantity, Price USD, Total value USD. "Identifier" is nu de CoinGecko-ID; de neutrale naam maakt andere bronnen later mogelijk.
- UTF-8 (met BOM voor Excel), komma als scheidingsteken, CRLF, RFC 4180-escaping (velden met komma's, aanhalingstekens of regeleinden worden tussen aanhalingstekens gezet).
- Getallen gebruiken altijd een punt als decimaal scheidingsteken, zonder duizendtallen en met de volledige decimal-precisie. Ontbrekende prijs is een leeg veld, nooit `0`.
- Tekst die met `=`, `+`, `-` of `@` begint krijgt een voorloop-apostrof, zodat spreadsheets er geen formule van maken.
- Nederlandse Excel verwacht vaak `;` als scheidingsteken: importeer via *Gegevens → Uit tekst/CSV* en kies de komma.

### Wallets scannen (Re-scan wallets)

De knop **Re-scan wallets** (naast *Update prices*) werkt de posities bij op basis van wat er op dit moment in je wallets staat. Wallets beheer je op de pagina **Wallets** (`/wallets`: naam, type cold/hot/cex, source, adres), de toegestane tokens op **Whitelist** (`/whitelist`: tokennaam, symbool, tokenadres, blockchain, decimals en identifier/CoinGecko-ID).

- **Alleen whitelisted tokens** worden opgehaald. Een leeg tokenadres betekent de native coin van de chain (CRO op Cronos, BNB op BNB Smart Chain).
- **Bron:** JSON-RPC via publieke nodes zonder API-key (zie `appsettings.json`, sectie `Blockchain:RpcUrls`). Publieke nodes kunnen rate-limiten; zet in `appsettings.json` of User Secrets een eigen node neer als dat gebeurt. Er wordt niets gescrapet.

  | Familie | Chains | Methoden | Adres van het token |
  |---|---|---|---|
  | EVM | Cronos, BNB Smart Chain, Ethereum, Polygon, Arbitrum, Optimism, Base, Avalanche, Scroll, Flare | `eth_getBalance` (native), `eth_call` met ERC-20 `balanceOf` | `0x…` contractadres |
  | XRP Ledger | `XrpLedger` (`xrplcluster.com`) | `account_info` (XRP), `account_lines` gefilterd op de issuer (issued tokens) | `CURRENCY.issuer`, bijv. `USD.rvYAf…`; decimals worden niet gebruikt |
  | Solana | `Solana` (`api.mainnet-beta.solana.com`) | `getBalance` (SOL), `getTokenAccountsByOwner` gefilterd op de mint (alle token accounts opgeteld, SPL en Token-2022) | mint-adres; decimals komen van de chain |
  | Bitcoin (SegWit) | `Bitcoin` (`mempool.space/api`, Esplora) | `GET /address/{adres}`: bevestigd ontvangen min uitgegeven | geen: alleen BTC (laat leeg) |
  | Sui | `Sui` (`graphql.mainnet.sui.io/graphql`; de publieke JSON-RPC is deprecated) | GraphQL `address.balance(coinType)` | coin type, bijv. `0xPACKAGE::module::NAME`; leeg = SUI |
  | Cardano | `Cardano` (`api.koios.rest/api/v1`, Koios) | `address_info` (`addr1…`) of `account_info` + `account_assets` (`stake1…`, hele account incl. rewards) | `policyId.assetNameHex` (de assetnaam mag ontbreken); leeg = ADA |
  | Stellar | `Stellar` (`horizon.stellar.org`) | `GET /accounts/{id}`: native saldo (XLM) en trustlines | `CODE:issuer`, bijv. `USDC:GA5ZSE…`; leeg = XLM |

- **Adresformaat bepaalt waar gescand wordt.** Een wallet wordt alleen gelezen op chains van de eigen familie: een `0x…`-adres van 40 hex-tekens op EVM-chains, een `r…`-adres (met checksum gecontroleerd) op de XRP Ledger, een Solana-adres (base58, 32 bytes) op Solana, een `bc1…`-, `3…`- (nested SegWit/P2SH) of `1…`-adres (met checksum gecontroleerd) op Bitcoin, een `0x…`-adres van 64 hex-tekens op Sui, een `addr1…`- of `stake1…`-adres (mainnet, Shelley; Byron wordt niet ondersteund) op Cardano en een `G…`-adres (checksum gecontroleerd) op Stellar. Het wordt dus nooit op een chain gevraagd waar het formaat niet bestaat. Een niet-geactiveerd XRP- of Stellar-account geldt als saldo 0 (`actNotFound` resp. HTTP 404); andere nodefouten blijven fouten. Het XRP- en XLM-saldo is inclusief de reserve die het netwerk vastzet. Bitcoin, Cardano en Stellar werken per adres/account: een Ledger- of Trezor-wallet met meerdere adressen voeg je per adres toe (bij Cardano volstaat het `stake1…`-adres voor het hele account).
- Gebruik voor XRP op de XRP Ledger identifier `ripple`; dat is dezelfde asset als de BEP-20 XRP op BNB Smart Chain, dus hoeveelheden worden bij elkaar opgeteld en de prijs wordt één keer opgehaald.
- **Per wallet en token** wordt een positie aangemaakt of bijgewerkt (`SourceType = OnChainWallet`, gekoppeld via `Position.WalletId`). Saldo `0` verwijdert de positie; tokens die niet meer op de whitelist staan verdwijnen bij verwijderen van het token of bij de volgende scan.
- **Mislukte leesacties** (time-out, rate limit, ongeldig antwoord) laten de opgeslagen positie ongemoeid en worden getoond. Een fout telt dus nooit als saldo `0`. 429/5xx/time-outs worden een paar keer opnieuw geprobeerd.
- **De scan is leidend.** Na een scan zonder fouten verdwijnen alle posities die geen wallet in de scan bezit (handmatige, exchange- en oude voorbeeldposities) uit de huidige weergave. Assets zonder posities worden niet getoond; hun prijsgeschiedenis blijft bewaard. Is er een leesfout, dan wordt er niets opgeruimd.
- **Historie (`/history`):** elke toevoeging, hoeveelheidswijziging en verwijdering van een positie wordt vastgelegd in `PositionChangeLogs` (tijdstip, asset, wallet, chain, vorige en nieuwe hoeveelheid, reden). Dit logboek wordt nooit gewist, ook niet als een asset of wallet later verdwijnt, zodat je kunt terugrekenen welke assets je op een moment had.
- **Handmatige posities** worden dus bij de eerstvolgende foutloze scan verwijderd (en gelogd). Een wallet verwijderen verwijdert ook de ingelezen posities van die wallet. Wijzigt het adres, dan worden de ingelezen posities verwijderd en weer opgebouwd bij de volgende scan.
- **CEX-wallets** worden opgeslagen maar niet gescand (geen exchange-integratie). Wallets met type cex accepteren een vrij identificatieveld i.p.v. een blockchain-adres.
- Een token met dezelfde identifier op meerdere chains (USDC) hoort bij één asset: hoeveelheden worden opgeteld en de prijs wordt één keer opgehaald. Nieuwe assets hebben pas een prijs na **Update prices**.
- Startwaarden (alleen als wallets én whitelist leeg zijn, dus bij de eerste start): wallets *Cronos Main* (`0x5971…8bFc`) en *Thor Vapor* (`0x1ED7…F8e3`), beide hot/Metamask; whitelist CRO en USDC op Cronos, en XRP, USDC en BNB op BNB Smart Chain. De tokencontracten en decimals zijn bij de publieke nodes gecontroleerd (symbool en decimals).

- Alle berekeningen staan in `Core` (`PortfolioService`, `AssetSorter`); de Razor-componenten tonen alleen.
- Prijs = laatste `PriceSnapshot` van de asset. Zonder prijs blijven prijs en waarde leeg.
- Een delta wordt alleen getoond als er een snapshot bestaat op of vlak vóór (maximaal 1 dag) het begin van de periode. Anders blijft de cel leeg; er worden geen koersen verzonnen.

## CoinGecko-prijsintegratie

Prijzen komen uitsluitend van de officiële **CoinGecko Demo API** (geen scraping). Een synchronisatie start alleen handmatig via de knop **Update prices** op de overzichtspagina; er is geen automatische achtergrondsync.

### API-key instellen (User Secrets)

De key staat nooit in de broncode of Git. Maak een gratis Demo-key aan op CoinGecko en sla hem lokaal op:

```powershell
dotnet user-secrets set "CoinGecko:ApiKey" "<jouw-key>" --project src\CryptoPortfolioManager.Web
```

User Secrets worden geladen in de omgeving `Development` (de launch-profielen doen dit). Buiten Development gebruik je de omgevingsvariabele `CoinGecko__ApiKey`. Zonder key toont de app een duidelijke foutmelding; bestaande prijzen blijven ongewijzigd.

Overige instellingen staan in `appsettings.json` onder `CoinGecko` (`BaseUrl`, `TimeoutSeconds`, `MaxRetries`, `MinRequestInterval`).

### Gebruikte endpoints

Basis-URL `https://api.coingecko.com/api/v3/`, header `x-cg-demo-api-key` (de key staat niet in de URL).

| Endpoint | Gebruik |
|---|---|
| `GET /simple/price` (`ids`, `vs_currencies=usd`, `include_last_updated_at`, `precision=full`) | Actuele prijzen van alle unieke assets, max. 100 ids per aanroep |
| `GET /coins/{id}/market_chart/range` (`vs_currency=usd`, `from`, `to`, `interval=daily`) | Dagelijkse historische prijzen per asset |

### Abonnementsbeperkingen (Demo)

- Rate limit: ca. 100 aanroepen per minuut; ook foutieve aanroepen tellen mee. Daarnaast geldt een maandelijks maximum (ca. 10.000 aanroepen, controleer de actuele CoinGecko-pricingpagina; dit is niet door mij geverifieerd).
- Historie is beperkt (de docs noemen voor Basic de afgelopen 2 jaar; de app vraagt maximaal 365 dagen op, genoeg voor YTD, 3M, 1M, 1W en 1D).
- Endpoints buiten het plan geven `PlanRestricted`; dit wordt als duidelijke fout getoond.

### Gedrag

- Eerste sync: 1 aanroep per 100 assets voor actuele prijzen + 1 historie-aanroep per asset. Latere syncs halen alleen actuele prijzen op (en vullen gaten van > 2 dagen aan).
- Elke asset heeft een verplicht, unieke `CoinGeckoId`; assets met een onbekend id krijgen een melding "geen prijs".
- Snapshots worden alleen toegevoegd (uniek op asset + bron + tijdstip), dus geen duplicaten en nooit overschrijven. Prijzen ≤ 0 of ontbrekende prijzen worden genegeerd: de laatst bekende goede prijs blijft staan.
- Verzoeken worden minimaal 2,5 s uit elkaar gestuurd; bij 429/5xx/time-out wordt met backoff (en `Retry-After`) opnieuw geprobeerd. Rate limit, ongeldige key of plan-beperking breken de run af.
- Elke run wordt gelogd in `PriceUpdateLog` (Succeeded / PartiallySucceeded / Failed); de UI toont de laatste geslaagde update en eventuele fouten.
- Bekende beperking: bij zeer jonge coins (minder historie dan nodig) wordt de historie bij elke sync opnieuw opgehaald.
- Voorbeelddata (bron `Sample`, alleen met `--with-sample-prices`) hoort niet gemengd te worden met echte prijzen; gebruik de echte database of een testdatabase zonder voorbeeldprijzen.
- Live-aanroepen zijn getest met een echte key via de opt-in live test; de overige tests gebruiken mockdata en een stub-`HttpMessageHandler`.

## Ontwerpkeuzes

- Posities en prijzen: zie Datamodel hierboven; geld- en hoeveelheidsvelden zijn `decimal`.
- `IDbContextFactory<AppDbContext>` met korte contexten per aanroep: de veilige aanpak voor Blazor Server.
- Tests draaien tegen in-memory SQLite met de echte migraties.

## Migraties

```powershell
dotnet tool run dotnet-ef migrations add <Naam> -p src\CryptoPortfolioManager.Infrastructure -s src\CryptoPortfolioManager.Web -o Persistence\Migrations
```

Migraties worden bij het opstarten toegepast.
