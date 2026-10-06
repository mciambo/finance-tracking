# Regole C#

Le regole applicative non sono incluse in questo repository. `Finance.Rules` espone `RuleRegistry.Create()` vuoto, così ogni installazione può aggiungere le proprie trasformazioni senza condividere descrizioni o criteri bancari personali.

## Regola su un movimento

Implementa `IExpenseRule` per trasformare una voce. L'interfaccia si trova in `Finance.Core`; ogni regola fornisce ID stabile, versione, ordine, nome, descrizione leggibile, data di modifica e `Apply(BankMovement original, RuleResult current)`.

`original` contiene i dati importati immutabili. `current` è il risultato delle regole precedenti. Restituisci un nuovo `RuleResult` e non scrivere sul database.

## Regola tra più movimenti

Implementa `IExpenseBatchRule` quando occorre abbinare più movimenti originali. `Match` restituisce coppie di ID e deve essere deterministico; un movimento non dovrebbe essere consumato da più abbinamenti. Le spese manuali sono gestite separatamente dal motore di importazione.

## Registrazione e verifica

Aggiungi l'istanza a `RuleRegistry.Create()`. Usa ID composti da lettere minuscole, cifre e trattini; incrementa la versione quando cambia il comportamento. Imposta `UpdatedOn` alla data effettiva della modifica.

Le regole vengono eseguite per ordine e poi per ID. Devono essere deterministiche e senza accesso a rete, file o orologio. Verifica ogni nuova regola con fixture sintetiche prima di usarla sui tuoi dati.
