# Benchmarks das fases 1 e 2

Os artefatos reais são gravados em `benchmarks/runs/<id>/` pelo comando
`creature-benchmark random-episodes.jsonl ppo-episodes.jsonl --output benchmarks/runs/<id>`.

Cada linha de entrada representa uma vida e contém os campos `policy`,
`seed`, `creature_id`, `lifetime_seconds`, contadores de déficit/consumo
e `death_cause`. Random e PPO devem usar os mesmos seeds e a mesma
configuração do mundo. O gate de sobrevivência exige razão média PPO/random maior
ou igual a 5; o gate de busca exige taxas condicionadas de comer e beber
superiores às do baseline. Ausência de execução nunca é convertida em aprovação.
