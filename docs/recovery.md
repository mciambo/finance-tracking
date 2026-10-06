# Recupero dagli errori

Ogni comando che modifica dati produce prima un backup consistente con l'API SQLite. Rimangono gli ultimi 20 backup automatici. Tutte le scritture applicative sono atomiche; un errore non aggiorna parzialmente spese, alert o cursore.

## Mese chiuso

`MONTH_CLOSED` indica mese e righe Excel tardive. Verificare i movimenti e riaprire **esplicitamente** soltanto i mesi coinvolti:

```powershell
finance months reopen 2026-09
finance import movimenti.xlsx --format json
finance import movimenti.xlsx --apply
finance months close 2026-09
```

La riapertura mostra i singoli movimenti. Rimane attiva anche dopo `months close-pending`. Se l'aggregato è corretto manualmente, la riapertura si ferma con `OVERRIDE_CONFLICT`: annotare prima la correzione, rimuoverla con `expenses reset ID`, riaprire e importare; poi richiudere e ripristinare la correzione appropriata.

## Regole e correzioni

`RULES_PENDING`: il codice delle regole è cambiato. Eseguire `rules preview --format json` e applicare con `rules apply --token TOKEN`.

`OVERRIDE_CONFLICT`: una regola renderebbe incompatibile la composizione di un aggregato corretto. Nessun dato viene applicato. Conservare la correzione, poi rimuoverla esplicitamente con `expenses reset ID`; ripetere l'anteprima e riapplicare la correzione alla spesa risultante. Il reset rimuove l'override, non l'originale. Per un aggregato eliminato, gli override di cancellazione sono sui suoi singoli movimenti (`s:<id>`), visibili tramite query alla tabella `overrides`.

## Cursore o anteprima

- `ANCHOR_MISSING`: esportare un intervallo bancario che includa l'ultimo movimento importato.
- `ANCHOR_AMBIGUOUS`: esportare un intervallo più ampio che includa le righe precedenti utili a distinguere i movimenti identici. Non cancellare duplicati legittimi.
- `STALE_PREVIEW`: ripetere l'anteprima; file, database, data locale o regole sono cambiati.
- `MONTH_CLOSE_REQUIRED`: eseguire `months close-pending`, poi ripetere la lettura.
- `DATABASE_BUSY`: attendere la conclusione dell'altra scrittura; riprovare il comando.

## Backup e ripristino

```powershell
finance backup --out copia.db
finance restore copia.db --apply
```

Il ripristino verifica versione e integrità, salva il database attuale e usa l'API backup. Chiudere la GUI durante un ripristino per evitare di agire su righe visualizzate prima del recupero; riavviarla dopo. Non copiare manualmente un database attivo ignorando i file WAL.
