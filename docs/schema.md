# Schema e analisi

Schema SQLite versione 3, `PRAGMA user_version=3`. Le migrazioni sono automatiche sulle operazioni di scrittura, con backup: la versione 2 aggiunge le spese manuali e la versione 3 gli abbinamenti tra movimenti. Database predefinito `%LOCALAPPDATA%\FinanceTracking\finance.db`.

| Tabella | Scopo |
| --- | --- |
| `movements` | Originali immutabili: `id`, `sequence`, `value_date`, `income_cents`, `expense_cents`, `description`, `import_id`, `excel_row`. Uscite originali normalizzate positive. |
| `imports` | File di provenienza, istante UTC e numero di movimenti acquisiti. |
| `expenses` | Proiezione effettiva per lista e report, con `id`, `value_date`, `description`, `amount_cents`, `rule_id`. |
| `manual_expenses` | Spese inserite manualmente: ID `m:<uuid>`, data, descrizione, centesimi e cancellazione persistente. Non alterano il cursore Excel e non subiscono regole bancarie. |
| `expense_sources` | Relazione fra spesa visualizzata e movimenti originali. |
| `overrides` | Correzioni manuali per campo e cancellazioni. `members` identifica la composizione dell'aggregato corretto. |
| `alerts` | Entrate segnalate; `handled_at` conserva il fatto che l'alert è stato gestito. |
| `rule_cancellations` | Abbinamenti uno a uno tra movimenti originali, registrati dalla regola che li ha creati. Gli originali restano immutati. |
| `months` | Stato `closed` o `reopened`; i mesi correnti senza stato sono aperti. |
| `metadata` | Revisione, generazione del database e versione delle regole. |

Usare le viste per le analisi delle **spese effettive**:

- `v_expenses`: `id`, `date`, `description`, `amount_cents`, `rule_id`, `month`.
- `v_monthly_totals`: `month`, `total_cents`, `expense_count`.
- `v_active_alerts`: `id`, `date`, `description`, `amount_cents`.

La vista degli alert esclude sia quelli gestiti manualmente sia gli storni abbinati dalle regole. Cambiare una regola può ripristinare un alert non gestito; uno già gestito rimane tale.

Gli ID delle spese singole bancarie sono `s:<movement_id>`; quelli degli aggregati `g:<group_key>:yyyy-MM` e quelli manuali `m:<uuid>`. Una spesa che entra in un aggregato cambia ID visualizzato; gli ID originali restano stabili. Le spese manuali non hanno righe in `expense_sources`.

## Query

```sql
SELECT month, SUM(amount_cents) AS total_cents, COUNT(*) AS expense_count
FROM v_expenses
WHERE amount_cents > $min
GROUP BY month
ORDER BY month
```

Passare `--param min=2000` per spese superiori a 20 €. I parametri sono legati al comando, mai concatenati al testo SQL. Sono ammesse singole query `SELECT`/`WITH`, senza altri statement; connessione read-only e `query_only` impediscono scritture. Usare `--file` per query complesse.

`amount_cents` va diviso per 100 solo per la presentazione. Per confronti mensili specificare se il mese corrente, incompleto, è incluso. Gli aggregati contano come una spesa e partecipano al filtro per importo con il loro totale. Per studiare i singoli pagamenti bancari usare esplicitamente `movements`, distinguendo gli originali dalle spese corrette.

La GUI mantiene connessioni brevi; SQLite usa WAL, foreign key e timeout di 10 secondi. La CLI può leggere mentre l'app è aperta. Una lettura che incontra mesi da chiudere restituisce `MONTH_CLOSE_REQUIRED` e non esegue la chiusura.
