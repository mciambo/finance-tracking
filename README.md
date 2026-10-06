# Finance Tracking

Applicazione desktop per Windows per importare movimenti da file Excel, tenere uno storico locale delle spese e consultarlo con filtri, report e una CLI. I dati restano in un database SQLite sul computer: il programma non richiede un servizio cloud.

> **Privacy:** non includere estratti conto, database, backup o esportazioni nel repository. Questa distribuzione non contiene regole bancarie personali: le regole incluse sono vuote e possono essere aggiunte per il proprio formato.

## Funzionalità

- Interfaccia desktop WPF in italiano e comando `finance`.
- Importazione Excel con anteprima prima dell'applicazione.
- Spese manuali, correzioni, avvisi e report mensili.
- Database SQLite locale, esportazione CSV e query in sola lettura.
- Regole estendibili tramite un progetto C# dedicato.

I dati originali importati sono conservati separatamente dalle modifiche manuali. Le letture e le anteprime non scrivono nel database; le operazioni di scrittura sono transazionali.

## Avvio

Scarica l'archivio Windows x64 dalla sezione **Releases**, estrailo e avvia `app/Finance.Desktop.exe`. Il pacchetto è autonomo e non richiede l'installazione di .NET o Microsoft Excel.

Il database predefinito si trova in `%LOCALAPPDATA%\FinanceTracking\finance.db`. GUI e CLI accettano `--db percorso` per selezionare un altro database. L'importazione non modifica il file Excel originale.

## CLI

Gli esempi presumono che `finance.exe` sia nella cartella corrente:

```powershell
./finance.exe help
./finance.exe import ./movements.xlsx --format json
./finance.exe import ./movements.xlsx --apply
./finance.exe expenses list --from 01/01/2026 --to 31/12/2026 --format json
./finance.exe expenses add --date 04/10/2026 --description 'Contanti' --amount 12,50
./finance.exe alerts list --format json
./finance.exe report --end 2026-10 --months 12 --format json
./finance.exe expenses export --format csv --out expenses.csv
```

Senza `--apply`, l'importazione mostra un'anteprima. Gli importi nei risultati JSON sono espressi in centesimi interi; le date sono `yyyy-MM-dd`. I comandi di lettura ed esportazione non modificano i dati.

## Sviluppo

Requisiti: Windows e .NET SDK 10.

```powershell
./scripts/build.ps1
dotnet run --project src/Finance.Desktop
dotnet run --project src/Finance.Cli -- help
./scripts/publish.ps1
```

`publish.ps1` crea il pacchetto autonomo Windows x64 in `dist/FinanceTracking-win-x64` e il relativo archivio ZIP. `dist/`, file Excel, database, backup e fixture locali sono esclusi da Git.

## Regole personalizzate

Il progetto `src/Finance.Rules` contiene il punto di registrazione delle regole ed è intenzionalmente vuoto. Implementa `IExpenseRule` per trasformazioni su un singolo movimento oppure `IExpenseBatchRule` per abbinamenti tra movimenti; registra le regole in `RuleRegistry.Create()`. Le regole devono essere deterministiche e non devono accedere a rete, file o orologio.

Consulta [schema e query](docs/schema.md), [recupero dagli errori](docs/recovery.md) e [guida alle regole](docs/rules.md).
