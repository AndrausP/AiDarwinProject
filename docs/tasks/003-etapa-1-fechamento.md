# Task 003 — Fechamento da etapa 1

**Depende de:** 002 (1a–1d' feitos) · **Fecha com:** avaliação na seed 0 e parada para mostrar ao usuário.
Formato de cada task: objetivo · depende de · entrega · aceite.

| ID | Task | Depende de | Entrega | Aceite |
|---|---|---|---|---|
| T1.1 | **Diagnosticar a regressão do v3** (vida média caiu 474 → 392 s em `v3-gru-a`) | — | runs curtos comparáveis: v3 sem curiosidade (`--curiosity 0`), v3 com lr menor, v2 no mesmo nº de atualizações | causa identificada e registrada em `errors-aprendidos.md`, ou v3 ≥ v2 com o mesmo nº de atualizações |
| T1.2 | **Modo de avaliação congelada** | — | flag `--freeze` no servidor (sem updates, sem curiosidade, pesos fixos) + teste | teste prova que os pesos não mudam durante o run |
| T1.3 | **Métrica de sono** ("dormir com fadiga alta") no gravador de episódios e no relatório | — | campos `high_fatigue_steps`, `slept_while_tired` + testes | relatório mostra a taxa por política |
| T1.4 | **Avaliação de 3 políticas** | T1.2, T1.3 | `scripts/eval-stage.ps1`: seed 0, sem respawn, aleatório × v2 × v3 congelados, N repetições; relatório comparativo | relatório com IC bootstrap gerado em `benchmarks/runs/eval-etapa1-*` |
| T1.5 | **Terminar os treinos** (v3 até 72k ticks com a correção de T1.1; v2 já concluído) | T1.1 | checkpoints finais | treino até o horizonte, 0 ticks perdidos |
| T1.6 | **HUD leve + inspetor** (barra: dia/hora/estação, vivas, cérebro; inspetor abas Estado e Mente: ação atual, valor do crítico, medo) | — | `scripts/Spectator/*` | screenshot conferido |
| T1.7 | **Vigia de exploits** | — | contadores no profile: tempo em cima de folhas, tempo enterrado (bloco sólido acima da cabeça), tempo colado na borda | número reportado na avaliação; exploit grave vira bug |
| T1.8 | **Avaliação da etapa 1 + relatório** | T1.4, T1.5, T1.6, T1.7 | relatório na task 002 + página para o usuário | critérios: vida ≥ 3×, comer com fome ≥ 2×, beber com sede ≥ 2×, dormir com fadiga alta > aleatório. **Parar e mostrar.** |
